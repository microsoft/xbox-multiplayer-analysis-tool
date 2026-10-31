// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace XMAT.Tests.HeadlessParity
{
    // One caller's use of one forbidden member: a GUI-only API, or a member that headless code may not use (HeadlessBans).
    // Caller is "Type::Method" with compiler-generated code (lambdas, local functions, async and iterator state machines)
    // attributed to the method that declares it; overloads share a line.
    internal sealed record ForbiddenCall(string Assembly, string Caller, string Callee, int Count)
    {
        internal string Key => Assembly + "|" + Caller + "|" + Callee;

        internal string CallerType => Caller.Substring(0, Caller.IndexOf("::", StringComparison.Ordinal));

        internal string ToLine() => Key + "|" + Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Inventories calls from non-UI code to APIs that only work inside the WPF application: message boxes, modal
    // dialogs, Application.Current, the Dispatcher, CommandManager and PublicUtilities.SafeInvoke. Reads IL with
    // System.Reflection.Metadata only and never loads the scanned assembly. Callers in UI types (anything derived from
    // FrameworkElement, such as Window and UserControl, from Application, or XAML code-behind) are exempt, except in the
    // headless namespaces (XMAT.Operations and XMAT.Cli): those never get the exemption, and FindHeadlessUiTypes reports
    // any WPF type declared there. ScanHeadlessBans separately inventories the headless namespaces' calls to, delegates
    // of and field accesses to the members that HeadlessBans forbids there.
    internal sealed class ForbiddenCallScanner
    {
        private static readonly Dictionary<ushort, OpCode> OpCodesByValue = CreateOpCodeTable();

        private static readonly Type[] UiBaseTypes =
        {
            typeof(System.Windows.FrameworkElement),
            typeof(System.Windows.Window),
            typeof(System.Windows.Controls.UserControl),
            typeof(System.Windows.Application),
        };

        private const string ComponentConnector = "System.Windows.Markup.IComponentConnector";

        private readonly PEReader _image;
        private readonly MetadataReader _metadata;
        private readonly string _assemblyName;
        private readonly Dictionary<TypeDefinitionHandle, bool> _uiTypes = new();

        private ForbiddenCallScanner(PEReader image)
        {
            _image = image;
            _metadata = image.GetMetadataReader();
            _assemblyName = _metadata.GetString(_metadata.GetAssemblyDefinition().Name);
        }

        internal static IReadOnlyList<ForbiddenCall> Scan(string assemblyPath)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using var image = new PEReader(stream);
            return new ForbiddenCallScanner(image).ScanAll();
        }

        // The uses of HeadlessBans members by code in the headless namespaces. Callee is "Type::Member", for example
        // "XMAT.Models.CaptureAppModel::AppModel" for the singleton field or "XMAT.PublicUtilities::AppLog".
        internal static IReadOnlyList<ForbiddenCall> ScanHeadlessBans(string assemblyPath)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using var image = new PEReader(stream);
            return new ForbiddenCallScanner(image).ScanBannedMembers();
        }

        // The types in the headless namespaces that are WPF types: XAML code-behind or anything derived from
        // DispatcherObject (windows, controls, Application, dependency objects). Headless code may not declare any.
        internal static IReadOnlyList<string> FindHeadlessUiTypes(string assemblyPath)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using var image = new PEReader(stream);
            var scanner = new ForbiddenCallScanner(image);
            return scanner._metadata.TypeDefinitions
                .Where(handle => IsHeadlessNamespace(scanner.TypeName(handle)) &&
                    (scanner.ImplementsComponentConnector(handle) ||
                     scanner.DerivesFrom(handle, typeof(System.Windows.Threading.DispatcherObject))))
                .Select(handle => scanner.TypeName(handle))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        internal static bool IsHeadlessNamespace(string typeName) =>
            IsInNamespace(typeName, "XMAT.Operations") || IsInNamespace(typeName, "XMAT.Cli");

        private static Dictionary<ushort, OpCode> CreateOpCodeTable()
        {
            var table = new Dictionary<ushort, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(OpCode))
                {
                    var opCode = (OpCode)field.GetValue(null);
                    table.TryAdd(unchecked((ushort)opCode.Value), opCode);
                }
            }

            return table;
        }

        private static bool IsInNamespace(string typeName, string ns) =>
            typeName.StartsWith(ns + ".", StringComparison.Ordinal);

        private IReadOnlyList<ForbiddenCall> ScanAll()
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (TypeDefinitionHandle typeHandle in _metadata.TypeDefinitions)
            {
                TypeDefinition type = _metadata.GetTypeDefinition(typeHandle);
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    (TypeDefinitionHandle userType, string userMethod) = DeclaringMember(typeHandle, method);
                    List<string> callees = ForbiddenCallees(method);
                    if (callees.Count == 0)
                    {
                        continue;
                    }

                    // The UI exemption never applies in the headless namespaces: a WPF base class there must not hide
                    // its calls from the zero-call rule.
                    string userTypeName = TypeName(userType);
                    if (IsUiType(userType) && !IsHeadlessNamespace(userTypeName))
                    {
                        continue;
                    }

                    string caller = userTypeName + "::" + userMethod;
                    foreach (string callee in callees)
                    {
                        string key = caller + "|" + callee;
                        counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
                    }
                }
            }

            return ToCalls(counts);
        }

        private IReadOnlyList<ForbiddenCall> ScanBannedMembers()
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (TypeDefinitionHandle typeHandle in _metadata.TypeDefinitions)
            {
                TypeDefinition type = _metadata.GetTypeDefinition(typeHandle);
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    (TypeDefinitionHandle userType, string userMethod) = DeclaringMember(typeHandle, method);
                    string userTypeName = TypeName(userType);
                    if (!IsHeadlessNamespace(userTypeName))
                    {
                        continue;
                    }

                    foreach ((OpCode instruction, EntityHandle operand) in MemberOperands(method))
                    {
                        (string memberType, string member) = instruction.OperandType == OperandType.InlineField
                            ? DescribeField(operand)
                            : DescribeMethodMember(operand);
                        if (HeadlessBans.Find(memberType, member) == null)
                        {
                            continue;
                        }

                        string key = userTypeName + "::" + userMethod + "|" + memberType + "::" + member;
                        counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
                    }
                }
            }

            return ToCalls(counts);
        }

        // Keys are "caller|callee"; neither part contains '|'.
        private List<ForbiddenCall> ToCalls(SortedDictionary<string, int> counts) =>
            counts
                .Select(entry =>
                {
                    int separator = entry.Key.IndexOf('|');
                    return new ForbiddenCall(
                        _assemblyName, entry.Key.Substring(0, separator), entry.Key.Substring(separator + 1), entry.Value);
                })
                .ToList();

        // The user-written method that a compiled method belongs to: "<Run>b__3_0" (lambda) and "<Run>g__Local|3_1"
        // (local function) belong to Run, and so does MoveNext of "<Run>d__7" (async or iterator state machine),
        // including state machines nested in display classes and lambdas.
        private (TypeDefinitionHandle Type, string Method) DeclaringMember(TypeDefinitionHandle typeHandle, MethodDefinition method)
        {
            var generatedTypes = new List<string>();
            TypeDefinitionHandle current = typeHandle;
            while (true)
            {
                TypeDefinition definition = _metadata.GetTypeDefinition(current);
                string name = _metadata.GetString(definition.Name);
                TypeDefinitionHandle declaring = definition.GetDeclaringType();
                if (!name.StartsWith("<", StringComparison.Ordinal) || declaring.IsNil)
                {
                    break;
                }

                generatedTypes.Add(name);
                current = declaring;
            }

            string methodName = _metadata.GetString(method.Name);
            string userMethod = UserMemberName(methodName);
            if (string.IsNullOrEmpty(userMethod))
            {
                userMethod = generatedTypes.Count == 0 && userMethod == null
                    ? methodName
                    : generatedTypes.Select(UserMemberName).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ??
                        "<compiler-generated>";
            }

            return (current, userMethod);
        }

        // "<Outer>b__5_0", "<Outer>g__Local|5_1", "<Outer>d__12" and "<<Outer>b__5_0>d" all give "Outer";
        // "<>c" and "<>c__DisplayClass5_0" give an empty string; names that are not compiler generated give null.
        internal static string UserMemberName(string name)
        {
            if (!name.StartsWith("<", StringComparison.Ordinal))
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

        private List<string> ForbiddenCallees(MethodDefinition method)
        {
            var callees = new List<string>();
            foreach ((OpCode instruction, EntityHandle operand) in MemberOperands(method))
            {
                if (IsCallOrDelegate(instruction) && TryDescribeForbidden(operand, out string callee))
                {
                    callees.Add(callee);
                }
            }

            return callees;
        }

        // The method and field operands in the method's IL, each with the instruction that uses it.
        private List<(OpCode Instruction, EntityHandle Operand)> MemberOperands(MethodDefinition method)
        {
            var operands = new List<(OpCode Instruction, EntityHandle Operand)>();
            BlobReader il = _image.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            while (il.RemainingBytes > 0)
            {
                ushort value = il.ReadByte();
                if (value == 0xFE)
                {
                    value = (ushort)(0xFE00 | il.ReadByte());
                }

                if (!OpCodesByValue.TryGetValue(value, out OpCode opCode))
                {
                    throw new BadImageFormatException($"Unknown IL opcode 0x{value:X} in {_assemblyName}.");
                }

                switch (opCode.OperandType)
                {
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                        operands.Add((opCode, MetadataTokens.EntityHandle(il.ReadInt32())));
                        break;

                    case OperandType.InlineSwitch:
                        int targets = il.ReadInt32();
                        Skip(ref il, checked(targets * 4));
                        break;

                    default:
                        Skip(ref il, OperandSize(opCode.OperandType));
                        break;
                }
            }

            return operands;
        }

        private static bool IsCallOrDelegate(OpCode opCode) =>
            opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj ||
            opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn || opCode == OpCodes.Jmp;

        private static void Skip(ref BlobReader reader, int count)
        {
            for (int index = 0; index < count; index++)
            {
                reader.ReadByte();
            }
        }

        private static int OperandSize(OperandType operandType) => operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
                OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or
                OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            _ => throw new BadImageFormatException($"Unexpected IL operand type {operandType}."),
        };

        private bool TryDescribeForbidden(EntityHandle method, out string callee)
        {
            (string typeName, string memberName, EntityHandle declaringType) = DescribeMethod(method);
            callee = typeName + "::" + memberName;
            return IsForbidden(typeName, memberName, declaringType);
        }

        private bool IsForbidden(string typeName, string memberName, EntityHandle declaringType)
        {
            switch (typeName)
            {
                case "System.Windows.MessageBox":
                    return memberName.StartsWith("Show", StringComparison.Ordinal);
                case "System.Windows.Application":
                    return memberName == "get_Current";
                case "System.Windows.Threading.Dispatcher":
                case "System.Windows.Input.CommandManager":
                    return true;
                case "XMAT.PublicUtilities":
                    return memberName == "SafeInvoke";
            }

            return memberName == "ShowDialog" &&
                (typeName.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) ||
                 DerivesFrom(declaringType, typeof(System.Windows.Window)));
        }

        private (string Type, string Member, EntityHandle DeclaringType) DescribeMethod(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                    MethodDefinition definition = _metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                    TypeDefinitionHandle owner = definition.GetDeclaringType();
                    return (TypeName(owner), _metadata.GetString(definition.Name), owner);

                case HandleKind.MemberReference:
                    MemberReference reference = _metadata.GetMemberReference((MemberReferenceHandle)handle);
                    return (TypeName(reference.Parent), _metadata.GetString(reference.Name), reference.Parent);

                case HandleKind.MethodSpecification:
                    return DescribeMethod(_metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method);

                default:
                    return (string.Empty, string.Empty, default);
            }
        }

        private (string Type, string Member) DescribeMethodMember(EntityHandle handle)
        {
            (string type, string member, _) = DescribeMethod(handle);
            return (type, member);
        }

        private (string Type, string Member) DescribeField(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.FieldDefinition:
                    FieldDefinition field = _metadata.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return (TypeName(field.GetDeclaringType()), _metadata.GetString(field.Name));

                case HandleKind.MemberReference:
                    MemberReference reference = _metadata.GetMemberReference((MemberReferenceHandle)handle);
                    return (TypeName(reference.Parent), _metadata.GetString(reference.Name));

                default:
                    return (string.Empty, string.Empty);
            }
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

        private bool IsUiType(TypeDefinitionHandle handle)
        {
            if (_uiTypes.TryGetValue(handle, out bool cached))
            {
                return cached;
            }

            bool result = false;
            for (TypeDefinitionHandle current = handle; !current.IsNil && !result;
                current = _metadata.GetTypeDefinition(current).GetDeclaringType())
            {
                result = ImplementsComponentConnector(current) ||
                    UiBaseTypes.Any(baseType => DerivesFrom(current, baseType));
            }

            _uiTypes[handle] = result;
            return result;
        }

        private bool ImplementsComponentConnector(TypeDefinitionHandle handle)
        {
            foreach (InterfaceImplementationHandle implementation in _metadata.GetTypeDefinition(handle).GetInterfaceImplementations())
            {
                if (TypeName(_metadata.GetInterfaceImplementation(implementation).Interface) == ComponentConnector)
                {
                    return true;
                }
            }

            return false;
        }

        // Whether a type defined in or referenced by the scanned assembly is, or derives from, target. Types defined
        // in the scanned assembly are followed through its metadata; referenced types are resolved by name.
        private bool DerivesFrom(EntityHandle handle, Type target)
        {
            for (int depth = 0; depth < 64 && !handle.IsNil; depth++)
            {
                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition:
                        handle = _metadata.GetTypeDefinition((TypeDefinitionHandle)handle).BaseType;
                        continue;

                    case HandleKind.TypeSpecification:
                        handle = GenericTypeDefinition((TypeSpecificationHandle)handle);
                        continue;

                    case HandleKind.TypeReference:
                        Type runtimeType = ResolveReference((TypeReferenceHandle)handle);
                        return runtimeType != null && target.IsAssignableFrom(runtimeType);

                    default:
                        return false;
                }
            }

            return false;
        }

        private Type ResolveReference(TypeReferenceHandle handle)
        {
            string assembly = ReferencedAssembly(handle);
            return assembly == null ? null : Type.GetType(TypeName(handle) + ", " + assembly, throwOnError: false);
        }

        private string ReferencedAssembly(TypeReferenceHandle handle)
        {
            EntityHandle scope = _metadata.GetTypeReference(handle).ResolutionScope;
            switch (scope.Kind)
            {
                case HandleKind.AssemblyReference:
                    return _metadata.GetString(_metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                case HandleKind.TypeReference:
                    return ReferencedAssembly((TypeReferenceHandle)scope);
                case HandleKind.ModuleDefinition:
                    return _assemblyName;
                default:
                    return null;
            }
        }
    }
}
