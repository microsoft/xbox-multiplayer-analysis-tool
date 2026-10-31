// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using XMAT.Models;
using XMAT.Operations.Capabilities;
using XMAT.SharedInterfaces;

namespace XMAT.Tests.HeadlessParity
{
    // Finds every GUI entry point the capability registry must classify. Reflection reads metadata only: no field
    // value is read and no static constructor runs. XAML comes from the product files embedded in this assembly.
    internal static class CapabilityDiscovery
    {
        internal const string XamlResourcePrefix = "XMAT.Tests.XamlSources\\";

        // Product XAML that other tests already embed under their own logical names.
        private static readonly (string ResourceName, string Path)[] SharedXamlResources =
        {
            ("XMAT.Tests.App.xaml", "App.xaml"),
            ("XMAT.Tests.MainWindow.xaml", "Windows/MainWindow.xaml"),
        };

        private static readonly XNamespace XamlLanguage = "http://schemas.microsoft.com/winfx/2006/xaml";

        // Enumerations whose values are capabilities in their own right: device kinds, capture methods, script
        // events and capture file formats.
        private static readonly Type[] CapabilityEnums =
        {
            typeof(DeviceType),
            typeof(CaptureType),
            typeof(WebServiceCaptureScriptableEventType),
            typeof(ImportType),
            typeof(ExportType),
        };

        internal static IReadOnlyCollection<string> DiscoverProduct()
        {
            var keys = new SortedSet<string>(DiscoverAssembly(typeof(PublicUtilities).Assembly), StringComparer.Ordinal);
            foreach ((string path, string text) in ProductXaml())
            {
                keys.UnionWith(DiscoverXaml(path, text));
            }

            keys.UnionWith(DiscoverEnums(CapabilityEnums));
            return keys;
        }

        // Routed commands, dialog windows, analyzers and preferences declared in an assembly.
        internal static IReadOnlyCollection<string> DiscoverAssembly(Assembly assembly)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Type type in assembly.GetTypes())
            {
                if (type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                {
                    continue;
                }

                const BindingFlags StaticMembers =
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (FieldInfo field in type.GetFields(StaticMembers))
                {
                    if (typeof(RoutedCommand).IsAssignableFrom(field.FieldType))
                    {
                        keys.Add(CapabilityKeys.Command(type.FullName + "." + field.Name));
                    }
                }

                foreach (PropertyInfo property in type.GetProperties(StaticMembers))
                {
                    if (typeof(RoutedCommand).IsAssignableFrom(property.PropertyType))
                    {
                        keys.Add(CapabilityKeys.Command(type.FullName + "." + property.Name));
                    }
                }

                if (typeof(System.Windows.Window).IsAssignableFrom(type))
                {
                    keys.Add(CapabilityKeys.Window(type.FullName));
                }

                if (IsConcrete(type) && typeof(ICaptureAnalyzer).IsAssignableFrom(type))
                {
                    keys.Add(CapabilityKeys.Analyzer(type.FullName));
                }

                if (IsPreferenceModel(type))
                {
                    foreach (PropertyInfo property in type.GetProperties(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (property.SetMethod != null && IsSimple(property.PropertyType))
                        {
                            keys.Add(CapabilityKeys.Preference(type.FullName, property.Name));
                        }
                    }
                }
            }

            return keys;
        }

        internal static IReadOnlyCollection<string> DiscoverEnums(IEnumerable<Type> enums)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Type enumType in enums)
            {
                foreach (string name in Enum.GetNames(enumType))
                {
                    keys.Add(CapabilityKeys.EnumValue(enumType.FullName, name));
                }
            }

            return keys;
        }

        // Command, Click and CommandBinding entry points in one XAML file. relativePath is relative to the XMAT
        // project folder and uses '/'. Markup in XML comments is not an entry point.
        internal static IReadOnlyCollection<string> DiscoverXaml(string relativePath, string text)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            XDocument document = XDocument.Parse(text);
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName.EndsWith(".Command", StringComparison.Ordinal))
                {
                    keys.Add(CapabilityKeys.Dynamic(relativePath, "<" + element.Name.LocalName + ">"));
                }

                foreach (XAttribute attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None)
                    {
                        continue;
                    }

                    string name = attribute.Name.LocalName;
                    if (name == "Command" || name.EndsWith(".Command", StringComparison.Ordinal))
                    {
                        keys.Add(CommandKey(element, relativePath, attribute.Value));
                    }
                    else if (name == "Click" || name.EndsWith(".Click", StringComparison.Ordinal))
                    {
                        keys.Add(CapabilityKeys.Click(relativePath, attribute.Value.Trim()));
                    }
                }
            }

            return keys;
        }

        internal static IEnumerable<(string Path, string Text)> ProductXaml()
        {
            Assembly tests = typeof(CapabilityDiscovery).Assembly;
            var resources = tests.GetManifestResourceNames()
                .Where(name => name.StartsWith(XamlResourcePrefix, StringComparison.Ordinal))
                .Select(name => (ResourceName: name, Path: name.Substring(XamlResourcePrefix.Length).Replace('\\', '/')))
                .Concat(SharedXamlResources)
                .OrderBy(resource => resource.Path, StringComparer.Ordinal);
            foreach ((string resourceName, string path) in resources)
            {
                using Stream stream = tests.GetManifestResourceStream(resourceName) ??
                    throw new InvalidOperationException($"XMAT.Tests does not embed {resourceName}.");
                using var reader = new StreamReader(stream);
                yield return (path, reader.ReadToEnd());
            }
        }

        private static string CommandKey(XElement element, string relativePath, string rawValue)
        {
            string value = rawValue.Trim();
            if (!value.StartsWith("{", StringComparison.Ordinal))
            {
                // A WPF built-in such as "New" or "ScrollBar.PageUpCommand".
                return CapabilityKeys.WpfCommand(value, relativePath);
            }

            string inner = value.TrimStart('{').TrimEnd('}').Trim();
            int space = inner.IndexOf(' ');
            string extension = space < 0 ? inner : inner.Substring(0, space);
            string argument = space < 0 ? string.Empty : inner.Substring(space + 1).Trim();
            if (argument.StartsWith("Member=", StringComparison.Ordinal))
            {
                argument = argument.Substring("Member=".Length).Trim();
            }

            if (IsStaticExtension(element, extension) && argument.Length > 0 && !argument.Contains(','))
            {
                int colon = argument.IndexOf(':');
                if (colon < 0)
                {
                    // {x:Static ApplicationCommands.Close} names a WPF type.
                    return CapabilityKeys.WpfCommand(argument, relativePath);
                }

                string clrNamespace = ClrNamespace(element, argument.Substring(0, colon));
                if (clrNamespace != null)
                {
                    return CapabilityKeys.Command(clrNamespace + "." + argument.Substring(colon + 1));
                }
            }

            // Bindings and other markup extensions are resolved at run time, so they are classified as written.
            return CapabilityKeys.Dynamic(relativePath, "Command=" + Regex.Replace(value, @"\s+", " "));
        }

        private static bool IsStaticExtension(XElement element, string extension)
        {
            int colon = extension.IndexOf(':');
            if (colon < 0)
            {
                return false;
            }

            string name = extension.Substring(colon + 1);
            return (name == "Static" || name == "StaticExtension") &&
                element.GetNamespaceOfPrefix(extension.Substring(0, colon)) == XamlLanguage;
        }

        // "clr-namespace:XMAT.WebServiceCapture;assembly=..." gives "XMAT.WebServiceCapture".
        private static string ClrNamespace(XElement element, string prefix)
        {
            string declared = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            const string ClrPrefix = "clr-namespace:";
            if (declared == null || !declared.StartsWith(ClrPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            string clrNamespace = declared.Substring(ClrPrefix.Length);
            int separator = clrNamespace.IndexOf(';');
            return (separator < 0 ? clrNamespace : clrNamespace.Substring(0, separator)).Trim();
        }

        private static bool IsConcrete(Type type) => !type.IsAbstract && !type.IsInterface;

        private static bool IsPreferenceModel(Type type) =>
            type == typeof(AppPreferencesModel) ||
            (IsConcrete(type) &&
             (typeof(ICaptureMethodParameters).IsAssignableFrom(type) ||
              typeof(ICaptureAnalyzerPreferences).IsAssignableFrom(type)));

        private static bool IsSimple(Type type)
        {
            Type underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) || underlying == typeof(decimal);
        }
    }
}
