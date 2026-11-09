// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests.Replay
{
    // Compose's registration (L2 design 8.1, AA-2) and handler (AA-3), read without building MainWindow, which no test
    // builds. In product XAML, as the tests embed it (CapabilityDiscovery.ProductXaml), each CommandBinding's Command and
    // each EventSetter's Event is resolved by reflection to the object that it names, and a value that does not resolve
    // throws; so is the type of each item of a CommandBindings collection. In the product assembly, UsesOf and OperandUses
    // read IL with System.Reflection.Metadata, as ForbiddenCallScanner does, and load nothing; MethodInstructions reads the
    // IL of one loaded method by reflection. Neither follows data flow.
    internal static class ComposeRegistrationScan
    {
        // The method that the XAML compiler generates to connect a file's named elements and event handlers.
        internal const string ComponentConnect = "System.Windows.Markup.IComponentConnector.Connect";

        private const string XamlCompiler = "PresentationBuildTasks";

        private const string ClrNamespacePrefix = "clr-namespace:";

        private const string AssemblyPrefix = "assembly=";

        private const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private const BindingFlags Statics =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        private static readonly XNamespace XamlLanguage = "http://schemas.microsoft.com/winfx/2006/xaml";

        // The assemblies that map CLR namespaces to the presentation namespace (XmlnsDefinition): WindowsBase,
        // PresentationCore and PresentationFramework.
        private static readonly Assembly[] WpfAssemblies =
        {
            typeof(DependencyObject).Assembly,
            typeof(UIElement).Assembly,
            typeof(FrameworkElement).Assembly,
        };

        private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null))
            .GroupBy(opCode => unchecked((ushort)opCode.Value))
            .ToDictionary(group => group.Key, group => group.First());

        // A CommandBinding of product XAML: its file and line, the object that its Command names, and its other attributes
        // as name=value, in document order.
        internal sealed record XamlBinding(string Path, int Line, object Command, IReadOnlyList<string> OtherAttributes);

        // An EventSetter of product XAML: its file and line, and the routed event that its Event names.
        internal sealed record XamlEventSetter(string Path, int Line, RoutedEvent Event);

        // A use of a member ("Type::Member", in any overload) by the product's code: the user method that uses it, as
        // ForbiddenCallScanner's DeclaringMember names one ("Type::Method", with lambdas, local functions and state machines
        // given to the method that declares them), and whether the compiled method that uses it is a Connect method that the
        // XAML compiler generated (ComponentConnect, with GeneratedCode from PresentationBuildTasks).
        internal sealed record MemberUse(string User, string Member, bool InGeneratedConnect);

        // A use of an operand of the product's IL: a member as "Type::Member", a type as "type Type", or a string as
        // "string \"text\"", or of an attribute of a method, with IL or without, as "attribute Type", by its user, as
        // MemberUse names it; whether the compiled method that uses it is a generated Connect method; and whether the XAML
        // compiler generated it at all (GeneratedCode from PresentationBuildTasks on the method or on a type that holds
        // it), as it does Connect, _CreateDelegate and GeneratedInternalTypeHelper.
        internal sealed record OperandUse(string User, string Operand, bool InGeneratedConnect, bool InXamlCompilerCode);

        // An item of a CommandBindings collection of product XAML, as written: its file and line, its element's name, or
        // "#text" for text, or "@" and the name of an attribute that sets the collection; and the type that the element
        // names, null for text, an attribute, or an element that names no single type.
        internal sealed record CommandBindingsItem(string Path, int Line, string Name, Type Type);

        // A mention of CommandBindings, in any case, in product XAML: its file and line; where it is, "element" for an
        // element's name, "attribute" for an attribute's name, "value" for an attribute's value, markup extensions and
        // member paths included, or "text"; and the name, name=value or text that holds it.
        internal sealed record CommandBindingsMention(string Path, int Line, string Where, string Text);

        // Every CommandBinding of product XAML, in any namespace, with its Command resolved. A CommandBinding without a Command
        // attribute, or whose Command does not resolve, throws.
        internal static IReadOnlyList<XamlBinding> CommandBindings()
        {
            var bindings = new List<XamlBinding>();
            foreach ((string path, XDocument document) in Documents())
            {
                foreach (XElement element in document.Descendants().Where(candidate => candidate.Name.LocalName == "CommandBinding"))
                {
                    XAttribute command = element.Attribute("Command") ??
                        throw new InvalidOperationException(Where(path, element) + ": a CommandBinding without a Command attribute.");
                    bindings.Add(new XamlBinding(
                        path,
                        LineOf(element),
                        ResolveCommand(path, element, command.Value),
                        element.Attributes()
                            .Where(attribute => !attribute.IsNamespaceDeclaration && attribute != command)
                            .Select(attribute => attribute.Name + "=" + attribute.Value)
                            .ToList()));
                }
            }

            return bindings;
        }

        // The object that the Command attribute of the element named name (x:Name or Name) in the XAML file at path names.
        // An element without a Command attribute throws.
        internal static object CommandOfElement(string path, string name)
        {
            XElement element = Documents()
                .Single(document => document.Path == path).Document
                .Descendants()
                .Single(candidate =>
                    (string)candidate.Attribute(XamlLanguage + "Name") == name || (string)candidate.Attribute("Name") == name);
            XAttribute command = element.Attribute("Command") ??
                throw new InvalidOperationException(Where(path, element) + ": " + name + " has no Command attribute.");
            return ResolveCommand(path, element, command.Value);
        }

        // Every EventSetter of product XAML, in any namespace, with its Event resolved. An EventSetter without an Event
        // attribute, or whose Event does not name exactly one routed event, throws.
        internal static IReadOnlyList<XamlEventSetter> EventSetters()
        {
            var setters = new List<XamlEventSetter>();
            foreach ((string path, XDocument document) in Documents())
            {
                foreach (XElement element in document.Descendants().Where(candidate => candidate.Name.LocalName == "EventSetter"))
                {
                    string value = (string)element.Attribute("Event") ??
                        throw new InvalidOperationException(Where(path, element) + ": an EventSetter without an Event attribute.");
                    setters.Add(new XamlEventSetter(path, LineOf(element), ResolveEvent(path, element, value)));
                }
            }

            return setters;
        }

        // Every attribute of product XAML that names an event called Executed or PreviewExecuted, other than a
        // CommandBinding's own two events, and every EventSetter whose Event names one, as "path:line: name=value". A routed
        // event keeps its name in every owner that adds it, so these are the only names under which markup can handle
        // CommandManager's Executed and PreviewExecuted.
        internal static IReadOnlyList<string> ExecutedEventNames()
        {
            var found = new List<string>();
            foreach ((string path, XDocument document) in Documents())
            {
                foreach (XElement element in document.Descendants())
                {
                    bool binding = element.Name.LocalName == "CommandBinding";
                    bool setter = element.Name.LocalName == "EventSetter";
                    foreach (XAttribute attribute in element.Attributes().Where(candidate => !candidate.IsNamespaceDeclaration))
                    {
                        string name = attribute.Name.LocalName;
                        bool ownEvent = binding && attribute.Name.Namespace == XNamespace.None && !name.Contains('.');
                        if ((IsExecuted(name) && !ownEvent) || (setter && name == "Event" && IsExecuted(attribute.Value.Trim())))
                        {
                            found.Add(Where(path, element) + ": " + attribute.Name + "=\"" + attribute.Value + "\"");
                        }
                    }
                }
            }

            return found;
        }

        // Every item of every CommandBindings collection of product XAML, as written: each child of a property element named
        // Owner.CommandBindings, element or text, and each attribute named CommandBindings or Owner.CommandBindings, in
        // document order. An element's type is resolved as XAML resolves it (ElementType), so a markup extension such as
        // StaticResource, or a type of the product's own, is not a CommandBinding.
        internal static IReadOnlyList<CommandBindingsItem> CommandBindingsItems()
        {
            var items = new List<CommandBindingsItem>();
            foreach ((string path, XDocument document) in Documents())
            {
                foreach (XElement element in document.Descendants())
                {
                    foreach (XAttribute attribute in element.Attributes().Where(candidate =>
                        !candidate.IsNamespaceDeclaration && IsCommandBindings(candidate.Name.LocalName)))
                    {
                        items.Add(new CommandBindingsItem(path, LineOf(element), "@" + attribute.Name.LocalName, null));
                    }

                    if (!element.Name.LocalName.Contains('.') || !IsCommandBindings(element.Name.LocalName))
                    {
                        continue;
                    }

                    foreach (XNode node in element.Nodes())
                    {
                        if (node is XElement child)
                        {
                            items.Add(new CommandBindingsItem(path, LineOf(child), child.Name.LocalName, ElementType(child)));
                        }
                        else if (node is XText text && !string.IsNullOrWhiteSpace(text.Value))
                        {
                            items.Add(new CommandBindingsItem(path, LineOf(element), "#text", null));
                        }
                    }
                }
            }

            return items;
        }

        // Every mention of CommandBindings, in any case, in product XAML, in document order: in element names, attribute
        // names and values and text, comments aside. Only a property element named Owner.CommandBindings declares a
        // collection, whose items CommandBindingsItems reads; any other mention can reach one at run time, as a binding's
        // path or a markup extension's argument can, since no other member gives the collection.
        internal static IReadOnlyList<CommandBindingsMention> CommandBindingsMentions()
        {
            var mentions = new List<CommandBindingsMention>();
            foreach ((string path, XDocument document) in Documents())
            {
                foreach (XElement element in document.Descendants())
                {
                    int line = LineOf(element);
                    if (MentionsCommandBindings(element.Name.LocalName))
                    {
                        mentions.Add(new CommandBindingsMention(path, line, "element", element.Name.LocalName));
                    }

                    foreach (XAttribute attribute in element.Attributes().Where(candidate => !candidate.IsNamespaceDeclaration))
                    {
                        if (MentionsCommandBindings(attribute.Name.LocalName))
                        {
                            mentions.Add(new CommandBindingsMention(path, line, "attribute", attribute.Name.LocalName));
                        }

                        if (MentionsCommandBindings(attribute.Value))
                        {
                            mentions.Add(new CommandBindingsMention(
                                path, line, "value", attribute.Name.LocalName + "=" + attribute.Value));
                        }
                    }

                    foreach (XText text in element.Nodes().OfType<XText>().Where(text => MentionsCommandBindings(text.Value)))
                    {
                        mentions.Add(new CommandBindingsMention(path, line, "text", text.Value.Trim()));
                    }
                }
            }

            return mentions;
        }

        // Every use, in the assembly at assemblyPath, of the members named "Type::Member" (in any overload): calls, delegates,
        // field accesses and tokens, by every method of every type, the XAML compiler's generated code included.
        internal static IReadOnlyList<MemberUse> UsesOf(string assemblyPath, IReadOnlyCollection<string> members)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using var image = new PEReader(stream);
            return new MetadataScan(image).UsesOf(members);
        }

        // Every use, in the assembly at assemblyPath, of an operand that matches, described as OperandUse describes it: calls,
        // delegates, field accesses, tokens, types, strings and methods' attributes, by every method of every type, the XAML
        // compiler's generated code included.
        internal static IReadOnlyList<OperandUse> OperandUses(string assemblyPath, Func<string, bool> matches)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using var image = new PEReader(stream);
            return new MetadataScan(image).OperandUses(matches);
        }

        // The instructions of the method that type declares with signature "Name(ParameterType,...):ReturnType" (full type
        // names), in IL order with nop left out, then those of each method that the compiler generated for it (a lambda, a
        // local function or a state machine's), each after a "generated:" line. A method operand is written as
        // Type::Name(ParameterType,...):ReturnType, a field as Type::Name:FieldType, a type by its full name, a string quoted
        // and any other operand as its raw value. A signature that type does not declare throws.
        internal static IReadOnlyList<string> MethodInstructions(Type type, string signature)
        {
            MethodBase[] matching = MethodsOf(type).Where(method => Signature(method) == signature).ToArray();
            if (matching.Length != 1)
            {
                throw new MissingMethodException(type + " declares no method " + signature + ".");
            }

            MethodBase target = matching[0];
            List<string> lines = Instructions(target);
            foreach (MethodBase generated in TypeAndNested(type)
                .SelectMany(nested => MethodsOf(nested))
                .Where(method => method != target && GeneratedFor(method) == target.Name)
                .OrderBy(method => Describe(method), StringComparer.Ordinal))
            {
                lines.Add("generated: " + Describe(generated));
                lines.AddRange(Instructions(generated));
            }

            return lines;
        }

        private static IEnumerable<(string Path, XDocument Document)> Documents() =>
            CapabilityDiscovery.ProductXaml()
                .Select(xaml => (Path: xaml.Path, Document: XDocument.Parse(xaml.Text, LoadOptions.SetLineInfo)));

        // The object that a Command value names: {x:Static Type.Member}, "Type.Member" (a static property or field of Type),
        // or a WPF command by its name alone ("Close"), which CommandConverter finds as XAML does.
        private static object ResolveCommand(string path, XElement element, string value)
        {
            string text = value.Trim();
            if (text.StartsWith('{'))
            {
                return StaticValue(path, element, MarkupArgument(path, element, text, "Static", "Member"));
            }

            if (text.Contains('.'))
            {
                return StaticValue(path, element, text);
            }

            try
            {
                return new CommandConverter().ConvertFrom(null, CultureInfo.InvariantCulture, text) ??
                    throw new InvalidOperationException(Where(path, element) + ": Command=\"" + value + "\" names nothing.");
            }
            catch (NotSupportedException exception)
            {
                throw new InvalidOperationException(Where(path, element) + ": Command=\"" + value + "\" names no command.", exception);
            }
        }

        // The value of the static property or field that "Type.Member" names, where Type is a XAML type name.
        private static object StaticValue(string path, XElement element, string member)
        {
            int dot = member.LastIndexOf('.');
            object[] values = dot <= 0
                ? Array.Empty<object>()
                : ResolveType(path, element, member.Substring(0, dot))
                    .Select(type => StaticMemberValue(type, member.Substring(dot + 1)))
                    .Where(found => found != null)
                    .Distinct()
                    .ToArray();
            return values.Length == 1
                ? values[0]
                : throw new InvalidOperationException(Where(path, element) + ": " + member + " does not name exactly one static value.");
        }

        private static object StaticMemberValue(Type type, string name) =>
            type.GetProperty(name, Statics) is PropertyInfo property
                ? property.GetValue(null)
                : type.GetField(name, Statics)?.GetValue(null);

        // The routed event that an EventSetter's Event names: "Type.Name" on Type, or "Name" on the TargetType of the Style
        // that holds the setter, each looked up as WPF registers routed events, through the type and its base types.
        private static RoutedEvent ResolveEvent(string path, XElement element, string value)
        {
            string text = value.Trim();
            int dot = text.LastIndexOf('.');
            IReadOnlyList<Type> owners = dot < 0
                ? StyleTargetTypes(path, element)
                : ResolveType(path, element, text.Substring(0, dot));
            RoutedEvent[] events = owners
                .Select(owner => FindRoutedEvent(owner, text.Substring(dot + 1)))
                .Where(found => found != null)
                .Distinct()
                .ToArray();
            return events.Length == 1
                ? events[0]
                : throw new InvalidOperationException(
                    Where(path, element) + ": Event=\"" + value + "\" does not name exactly one routed event.");
        }

        private static IReadOnlyList<Type> StyleTargetTypes(string path, XElement element)
        {
            XElement style = element.Ancestors().FirstOrDefault(ancestor => ancestor.Name.LocalName == "Style");
            string targetType = (string)style?.Attribute("TargetType") ??
                throw new InvalidOperationException(Where(path, element) + ": an EventSetter outside a Style with a TargetType.");
            string text = targetType.Trim();
            return ResolveType(path, style, text.StartsWith('{') ? MarkupArgument(path, style, text, "Type", "TypeName") : text);
        }

        private static RoutedEvent FindRoutedEvent(Type owner, string name)
        {
            for (Type type = owner; type != null; type = type.BaseType)
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                RoutedEvent found = EventManager.GetRoutedEventsForOwner(type)?.FirstOrDefault(candidate => candidate.Name == name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        // Whether a name, or the member part of a dotted name, is Executed or PreviewExecuted.
        private static bool IsExecuted(string name)
        {
            string member = name.Substring(name.LastIndexOf('.') + 1);
            return member == "Executed" || member == "PreviewExecuted";
        }

        // Whether a name, or the member part of a dotted name, is CommandBindings.
        private static bool IsCommandBindings(string name) => name.Substring(name.LastIndexOf('.') + 1) == "CommandBindings";

        // Whether text holds CommandBindings in any case.
        private static bool MentionsCommandBindings(string text) =>
            text.Contains("CommandBindings", StringComparison.OrdinalIgnoreCase);

        // The type that an element names, as XAML resolves it in the element's namespace (ClrNamespaces), or null when it
        // names none or more than one.
        private static Type ElementType(XElement element)
        {
            Type[] types = ClrNamespaces(element.Name.NamespaceName)
                .Select(clr => clr.Assembly.GetType(clr.Namespace + "." + element.Name.LocalName, throwOnError: false))
                .Where(type => type != null)
                .Distinct()
                .ToArray();
            return types.Length == 1 ? types[0] : null;
        }

        // The argument of a markup extension of the XAML language namespace, {x:Name argument} or {x:Name Key=argument}, the
        // extension also written NameExtension; any other value throws.
        private static string MarkupArgument(string path, XElement element, string text, string name, string key)
        {
            string inner = text.EndsWith('}') ? text.Substring(1, text.Length - 2).Trim() : string.Empty;
            int space = inner.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            string extension = space < 0 ? inner : inner.Substring(0, space);
            string argument = space < 0 ? string.Empty : inner.Substring(space + 1).Trim();
            if (argument.StartsWith(key + "=", StringComparison.Ordinal))
            {
                argument = argument.Substring(key.Length + 1).Trim();
            }

            int colon = extension.IndexOf(':');
            string extensionName = extension.Substring(colon + 1);
            bool language = colon > 0 && element.GetNamespaceOfPrefix(extension.Substring(0, colon)) == XamlLanguage;
            return language && (extensionName == name || extensionName == name + "Extension") &&
                argument.Length > 0 && !argument.Contains(',')
                ? argument
                : throw new InvalidOperationException(
                    Where(path, element) + ": " + text + " is not an x:" + name + " that this scan resolves.");
        }

        // The types that a XAML type name names: "prefix:Name" in that prefix's namespace, or "Name" in the element's default
        // namespace, where the presentation namespace stands for every CLR namespace that WPF's assemblies map to it. A name
        // that names no type throws.
        private static IReadOnlyList<Type> ResolveType(string path, XElement element, string qualifiedName)
        {
            int colon = qualifiedName.IndexOf(':');
            XNamespace xmlNamespace = colon < 0
                ? element.GetDefaultNamespace()
                : element.GetNamespaceOfPrefix(qualifiedName.Substring(0, colon));
            string name = qualifiedName.Substring(colon + 1).Trim();
            List<Type> types = xmlNamespace == null
                ? new List<Type>()
                : ClrNamespaces(xmlNamespace.NamespaceName)
                    .Select(clr => clr.Assembly.GetType(clr.Namespace + "." + name, throwOnError: false))
                    .Where(type => type != null)
                    .Distinct()
                    .ToList();
            return types.Count > 0
                ? types
                : throw new InvalidOperationException(Where(path, element) + ": no type " + qualifiedName + ".");
        }

        // The CLR namespaces, each with its assembly, that a XAML namespace stands for: "clr-namespace:N;assembly=A" (the
        // product assembly when no assembly is named), or those that WPF's assemblies map to it by XmlnsDefinition.
        private static List<(Assembly Assembly, string Namespace)> ClrNamespaces(string xmlNamespace)
        {
            var namespaces = new List<(Assembly Assembly, string Namespace)>();
            if (xmlNamespace.StartsWith(ClrNamespacePrefix, StringComparison.Ordinal))
            {
                string[] parts = xmlNamespace.Substring(ClrNamespacePrefix.Length).Split(';');
                string assemblyName = parts.Skip(1)
                    .Select(part => part.Trim())
                    .Where(part => part.StartsWith(AssemblyPrefix, StringComparison.Ordinal))
                    .Select(part => part.Substring(AssemblyPrefix.Length))
                    .FirstOrDefault();
                Assembly assembly = string.IsNullOrEmpty(assemblyName) ? typeof(MainWindow).Assembly : Load(assemblyName);
                if (assembly != null)
                {
                    namespaces.Add((assembly, parts[0].Trim()));
                }

                return namespaces;
            }

            foreach (Assembly wpf in WpfAssemblies)
            {
                foreach (XmlnsDefinitionAttribute definition in wpf.GetCustomAttributes<XmlnsDefinitionAttribute>()
                    .Where(candidate => candidate.XmlNamespace == xmlNamespace))
                {
                    Assembly assembly = definition.AssemblyName == null ? wpf : Load(definition.AssemblyName);
                    if (assembly != null)
                    {
                        namespaces.Add((assembly, definition.ClrNamespace));
                    }
                }
            }

            return namespaces;
        }

        private static Assembly Load(string name)
        {
            try
            {
                return Assembly.Load(name);
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static int LineOf(XElement element) => ((IXmlLineInfo)element).LineNumber;

        private static string Where(string path, XElement element) =>
            path + ":" + LineOf(element).ToString(CultureInfo.InvariantCulture);

        private static IEnumerable<Type> TypeAndNested(Type type) =>
            new[] { type }.Concat(
                type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(nested => TypeAndNested(nested)));

        private static IEnumerable<MethodBase> MethodsOf(Type type) =>
            type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));

        // The user method that a compiler-generated method belongs to (UserMethod), or null for a method that the compiler
        // did not generate.
        private static string GeneratedFor(MethodBase method)
        {
            var generatedTypes = new List<string>();
            for (Type type = method.DeclaringType; type?.DeclaringType != null && type.Name.StartsWith('<'); type = type.DeclaringType)
            {
                generatedTypes.Add(type.Name);
            }

            return UserMemberName(method.Name) == null && generatedTypes.Count == 0
                ? null
                : UserMethod(method.Name, generatedTypes);
        }

        // The user method that a compiled method belongs to, as ForbiddenCallScanner's DeclaringMember names it:
        // "<Run>b__3_0" (a lambda), "<Run>g__Local|3_1" (a local function) and MoveNext of "<Run>d__7" (a state machine), with
        // the generated types that hold it named innermost first, all belong to Run; a method that the compiler did not
        // generate is its own.
        private static string UserMethod(string methodName, IReadOnlyList<string> generatedTypes)
        {
            string user = UserMemberName(methodName);
            if (!string.IsNullOrEmpty(user))
            {
                return user;
            }

            return user == null && generatedTypes.Count == 0
                ? methodName
                : generatedTypes.Select(generated => UserMemberName(generated)).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ??
                    "<compiler-generated>";
        }

        // "<Outer>b__5_0", "<Outer>g__Local|5_1", "<Outer>d__12" and "<<Outer>b__5_0>d" give "Outer"; "<>c" and
        // "<>c__DisplayClass5_0" give an empty string; a name that the compiler did not generate gives null.
        private static string UserMemberName(string name)
        {
            if (!name.StartsWith('<'))
            {
                return null;
            }

            int start = 0;
            while (start < name.Length && name[start] == '<')
            {
                start++;
            }

            int end = name.IndexOf('>', start);
            return end <= start ? string.Empty : name.Substring(start, end - start);
        }

        // "Name(ParameterType,...):ReturnType", with full type names.
        private static string Signature(MethodBase method) =>
            method.Name + "(" + string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType)) + "):" +
            ((method as MethodInfo)?.ReturnType ?? typeof(void));

        private static string Describe(MemberInfo member) => member switch
        {
            MethodBase method => method.DeclaringType + "::" + Signature(method),
            FieldInfo field => field.DeclaringType + "::" + field.Name + ":" + field.FieldType,
            _ => member.ToString(),
        };

        private static List<string> Instructions(MethodBase method)
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray() ??
                throw new InvalidOperationException(Describe(method) + " has no IL.");
            Type[] typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
            Type[] methodArguments = method is MethodInfo { IsGenericMethod: true } ? method.GetGenericArguments() : null;
            var lines = new List<string>();
            int offset = 0;
            while (offset < il.Length)
            {
                ushort value = il[offset++];
                if (value == 0xFE)
                {
                    value = (ushort)(0xFE00 | il[offset++]);
                }

                OpCode opCode = OpCodeOf(value);
                string operand = null;
                switch (opCode.OperandType)
                {
                    case OperandType.InlineNone:
                        break;

                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                        operand = Describe(
                            method.Module.ResolveMember(BitConverter.ToInt32(il, offset), typeArguments, methodArguments));
                        offset += 4;
                        break;

                    case OperandType.InlineString:
                        operand = "\"" + method.Module.ResolveString(BitConverter.ToInt32(il, offset)) + "\"";
                        offset += 4;
                        break;

                    case OperandType.InlineSwitch:
                        int targets = BitConverter.ToInt32(il, offset);
                        offset += 4;
                        var labels = new List<string>();
                        for (int index = 0; index < targets; index++, offset += 4)
                        {
                            labels.Add(BitConverter.ToInt32(il, offset).ToString(CultureInfo.InvariantCulture));
                        }

                        operand = string.Join(",", labels);
                        break;

                    default:
                        operand = RawOperand(il, offset, opCode.OperandType);
                        offset += OperandSize(opCode.OperandType);
                        break;
                }

                if (opCode != OpCodes.Nop)
                {
                    lines.Add(operand == null ? opCode.Name : opCode.Name + " " + operand);
                }
            }

            return lines;
        }

        private static string RawOperand(byte[] il, int offset, OperandType operandType) => operandType switch
        {
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI =>
                unchecked((sbyte)il[offset]).ToString(CultureInfo.InvariantCulture),
            OperandType.ShortInlineVar => il[offset].ToString(CultureInfo.InvariantCulture),
            OperandType.InlineVar => BitConverter.ToUInt16(il, offset).ToString(CultureInfo.InvariantCulture),
            OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineSig =>
                BitConverter.ToInt32(il, offset).ToString(CultureInfo.InvariantCulture),
            OperandType.InlineI8 => BitConverter.ToInt64(il, offset).ToString(CultureInfo.InvariantCulture),
            OperandType.ShortInlineR => BitConverter.ToSingle(il, offset).ToString("R", CultureInfo.InvariantCulture),
            OperandType.InlineR => BitConverter.ToDouble(il, offset).ToString("R", CultureInfo.InvariantCulture),
            _ => throw new BadImageFormatException("Unexpected IL operand type " + operandType + "."),
        };

        private static OpCode OpCodeOf(ushort value) =>
            OpCodesByValue.TryGetValue(value, out OpCode opCode)
                ? opCode
                : throw new BadImageFormatException("Unknown IL opcode 0x" + value.ToString("X", CultureInfo.InvariantCulture) + ".");

        private static int OperandSize(OperandType operandType) => operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
                OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or
                OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            _ => throw new BadImageFormatException("Unexpected IL operand type " + operandType + "."),
        };

        // Reads an assembly's metadata and IL without loading it.
        private sealed class MetadataScan
        {
            private readonly PEReader _image;
            private readonly MetadataReader _metadata;

            internal MetadataScan(PEReader image)
            {
                _image = image;
                _metadata = image.GetMetadataReader();
            }

            internal IReadOnlyList<MemberUse> UsesOf(IReadOnlyCollection<string> members) =>
                OperandUses(operand => members.Contains(operand))
                    .Select(use => new MemberUse(use.User, use.Operand, use.InGeneratedConnect))
                    .Distinct()
                    .ToList();

            internal IReadOnlyList<OperandUse> OperandUses(Func<string, bool> matches)
            {
                var uses = new HashSet<OperandUse>();
                foreach (TypeDefinitionHandle typeHandle in _metadata.TypeDefinitions)
                {
                    bool xamlCompilerType = IsXamlCompilerType(typeHandle);
                    foreach (MethodDefinitionHandle methodHandle in _metadata.GetTypeDefinition(typeHandle).GetMethods())
                    {
                        MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                        IEnumerable<string> operands = Attributes(method);
                        if (method.RelativeVirtualAddress != 0)
                        {
                            operands = operands.Concat(Operands(method));
                        }

                        foreach (string operand in operands.Where(described => described != null && matches(described)))
                        {
                            uses.Add(new OperandUse(
                                DeclaringMember(typeHandle, method),
                                operand,
                                IsGeneratedConnect(method),
                                xamlCompilerType || IsXamlCompilerCode(method.GetCustomAttributes())));
                        }
                    }
                }

                return uses
                    .OrderBy(use => use.User, StringComparer.Ordinal)
                    .ThenBy(use => use.Operand, StringComparer.Ordinal)
                    .ToList();
            }

            // The attributes of a method, each as "attribute Type", for a method without IL too, such as an extern one that
            // an attribute implements (UnsafeAccessor).
            private IEnumerable<string> Attributes(MethodDefinition method) =>
                method.GetCustomAttributes()
                    .Select(handle => AttributeTypeName(_metadata.GetCustomAttribute(handle)))
                    .Where(name => name != null)
                    .Select(name => "attribute " + name)
                    .ToList();

            // The operands in a method's IL that name something, in order: a method, field or type as Describe gives it, and
            // a string as "string \"text\"".
            private List<string> Operands(MethodDefinition method)
            {
                var operands = new List<string>();
                BlobReader il = _image.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                while (il.RemainingBytes > 0)
                {
                    ushort value = il.ReadByte();
                    if (value == 0xFE)
                    {
                        value = (ushort)(0xFE00 | il.ReadByte());
                    }

                    OpCode opCode = OpCodeOf(value);
                    switch (opCode.OperandType)
                    {
                        case OperandType.InlineMethod:
                        case OperandType.InlineField:
                        case OperandType.InlineTok:
                        case OperandType.InlineType:
                            operands.Add(Describe(MetadataTokens.EntityHandle(il.ReadInt32())));
                            break;

                        case OperandType.InlineString:
                            UserStringHandle text = MetadataTokens.UserStringHandle(il.ReadInt32() & 0x00FFFFFF);
                            operands.Add("string \"" + _metadata.GetUserString(text) + "\"");
                            break;

                        case OperandType.InlineSwitch:
                            Skip(ref il, checked(il.ReadInt32() * 4));
                            break;

                        default:
                            Skip(ref il, OperandSize(opCode.OperandType));
                            break;
                    }
                }

                return operands;
            }

            private static void Skip(ref BlobReader reader, int count)
            {
                for (int index = 0; index < count; index++)
                {
                    reader.ReadByte();
                }
            }

            // What an operand names: a member as "Type::Member", a type as "type Type"; null for anything else.
            private string Describe(EntityHandle handle)
            {
                switch (handle.Kind)
                {
                    case HandleKind.MethodDefinition:
                        MethodDefinition method = _metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                        return TypeName(method.GetDeclaringType()) + "::" + _metadata.GetString(method.Name);

                    case HandleKind.FieldDefinition:
                        FieldDefinition field = _metadata.GetFieldDefinition((FieldDefinitionHandle)handle);
                        return TypeName(field.GetDeclaringType()) + "::" + _metadata.GetString(field.Name);

                    case HandleKind.MemberReference:
                        MemberReference reference = _metadata.GetMemberReference((MemberReferenceHandle)handle);
                        return TypeName(reference.Parent) + "::" + _metadata.GetString(reference.Name);

                    case HandleKind.MethodSpecification:
                        return Describe(_metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method);

                    case HandleKind.TypeDefinition:
                    case HandleKind.TypeReference:
                    case HandleKind.TypeSpecification:
                        return "type " + TypeName(handle);

                    default:
                        return null;
                }
            }

            private string DeclaringMember(TypeDefinitionHandle typeHandle, MethodDefinition method)
            {
                var generatedTypes = new List<string>();
                TypeDefinitionHandle current = typeHandle;
                while (true)
                {
                    TypeDefinition definition = _metadata.GetTypeDefinition(current);
                    string typeName = _metadata.GetString(definition.Name);
                    TypeDefinitionHandle declaring = definition.GetDeclaringType();
                    if (!typeName.StartsWith('<') || declaring.IsNil)
                    {
                        break;
                    }

                    generatedTypes.Add(typeName);
                    current = declaring;
                }

                return TypeName(current) + "::" + UserMethod(_metadata.GetString(method.Name), generatedTypes);
            }

            private bool IsGeneratedConnect(MethodDefinition method) =>
                _metadata.GetString(method.Name) == ComponentConnect && IsXamlCompilerCode(method.GetCustomAttributes());

            // Whether attributes include GeneratedCode with the XAML compiler as its tool.
            private bool IsXamlCompilerCode(CustomAttributeHandleCollection attributes) =>
                attributes
                    .Select(handle => _metadata.GetCustomAttribute(handle))
                    .Any(attribute => AttributeTypeName(attribute) == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
                        FirstStringArgument(attribute) == XamlCompiler);

            // Whether the XAML compiler generated a type, or a type that holds it.
            private bool IsXamlCompilerType(TypeDefinitionHandle typeHandle)
            {
                TypeDefinitionHandle current = typeHandle;
                while (!current.IsNil)
                {
                    TypeDefinition definition = _metadata.GetTypeDefinition(current);
                    if (IsXamlCompilerCode(definition.GetCustomAttributes()))
                    {
                        return true;
                    }

                    current = definition.GetDeclaringType();
                }

                return false;
            }

            private string AttributeTypeName(CustomAttribute attribute) => attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference =>
                    TypeName(_metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
                HandleKind.MethodDefinition =>
                    TypeName(_metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
                _ => null,
            };

            // The first fixed argument of an attribute whose constructor takes a string first, such as GeneratedCode's tool.
            private string FirstStringArgument(CustomAttribute attribute)
            {
                BlobReader value = _metadata.GetBlobReader(attribute.Value);
                return value.ReadUInt16() == 1 ? value.ReadSerializedString() : null;
            }

            private string TypeName(EntityHandle handle)
            {
                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition:
                        TypeDefinition definition = _metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                        TypeDefinitionHandle declaring = definition.GetDeclaringType();
                        return declaring.IsNil
                            ? Qualify(_metadata.GetString(definition.Namespace), _metadata.GetString(definition.Name))
                            : TypeName(declaring) + "+" + _metadata.GetString(definition.Name);

                    case HandleKind.TypeReference:
                        TypeReference reference = _metadata.GetTypeReference((TypeReferenceHandle)handle);
                        return reference.ResolutionScope.Kind == HandleKind.TypeReference
                            ? TypeName(reference.ResolutionScope) + "+" + _metadata.GetString(reference.Name)
                            : Qualify(_metadata.GetString(reference.Namespace), _metadata.GetString(reference.Name));

                    case HandleKind.TypeSpecification:
                        EntityHandle generic = GenericTypeDefinition((TypeSpecificationHandle)handle);
                        return generic.IsNil ? "<type-specification>" : TypeName(generic);

                    case HandleKind.MethodDefinition:
                        return TypeName(_metadata.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType());

                    default:
                        return "<" + handle.Kind + ">";
                }
            }

            private static string Qualify(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

            private EntityHandle GenericTypeDefinition(TypeSpecificationHandle handle)
            {
                BlobReader signature = _metadata.GetBlobReader(_metadata.GetTypeSpecification(handle).Signature);
                if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                {
                    return default;
                }

                signature.ReadSignatureTypeCode();
                return signature.ReadTypeHandle();
            }
        }
    }
}
