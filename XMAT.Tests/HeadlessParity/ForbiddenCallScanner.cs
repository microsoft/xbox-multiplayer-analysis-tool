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

    internal sealed record ReachableMemberClosure(
        IReadOnlyList<string> Members,
        IReadOnlyDictionary<string, string> Paths);

    // Inventories calls from non-UI code to APIs that only work inside the WPF application: message boxes, modal
    // dialogs, Application.Current, the Dispatcher, CommandManager and PublicUtilities.SafeInvoke. Reads IL with
    // System.Reflection.Metadata only and never loads the scanned assembly. Callers in UI types (anything derived from
    // FrameworkElement, such as Window and UserControl, from Application, or XAML code-behind) are exempt, except in the
    // headless namespaces (XMAT.Operations and XMAT.Cli): those never get the exemption, and FindHeadlessUiTypes reports
    // any WPF type declared there. ScanHeadlessBans separately inventories the headless namespaces' calls to, delegates
    // of and field accesses to the members that HeadlessBans forbids there.
    internal sealed class ForbiddenCallScanner
    {
        private readonly record struct IlInstruction(OpCode OpCode, EntityHandle Operand);

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
        private readonly Dictionary<int, IReadOnlyList<IlInstruction>> _instructions = new();

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

        // Every member ("Type::Member") that the code of these types uses, nested types and compiler-generated code
        // (lambdas, local functions, async and iterator state machines) included: calls, delegates and field accesses.
        internal static IReadOnlyList<string> MembersUsedBy(string assemblyPath, IReadOnlyCollection<string> typeNames)
        {
            return MembersUsedBy(
                assemblyPath,
                typeNames,
                new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal));
        }

        // As above, plus selected user-written members of types whose unrelated members are outside the reviewed path.
        internal static IReadOnlyList<string> MembersUsedBy(
            string assemblyPath,
            IReadOnlyCollection<string> typeNames,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> membersByType)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using var image = new PEReader(stream);
            return new ForbiddenCallScanner(image).ScanMembersUsedBy(typeNames, membersByType);
        }

        // Every member used by the in-assembly call closure from entryMembers ("Type::Member"). The closure includes
        // compiler-generated methods belonging to a reached member, each reached type's static constructor, static-field
        // initialization, seeded types and their constructors, runtime callbacks on instantiated types, operand types,
        // member type arguments and in-assembly virtual/interface targets. Treating cast and array operand types as
        // instantiated deliberately over-approximates. Ordinary delegates are followed immediately; only the exact compiler
        // lambda-to-Lazy<T> construction edge is deferred until that Lazy is realized.
        internal static ReachableMemberClosure MembersReachableFrom(
            string assemblyPath,
            IReadOnlyCollection<string> entryMembers,
            IReadOnlyCollection<string> seedTypeNames)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using var image = new PEReader(stream);
            return new ForbiddenCallScanner(image).ScanMembersReachableFrom(entryMembers, seedTypeNames);
        }

        internal static bool TypeIsAssignableTo(string assemblyPath, string typeName, string targetType)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using var image = new PEReader(stream);
            var scanner = new ForbiddenCallScanner(image);
            TypeDefinitionHandle type = scanner._metadata.TypeDefinitions.FirstOrDefault(
                handle => scanner.TypeName(handle) == typeName);
            if (type.IsNil)
            {
                throw new InvalidOperationException($"Scanner fixture type not found: {typeName}.");
            }

            return scanner.IsAssignableTo(type, targetType);
        }

        private static Dictionary<ushort, OpCode> CreateOpCodeTable()
        {
            var table = new Dictionary<ushort, OpCode>();
            foreach (OpCode opCode in typeof(OpCodes)
                .GetFields(
                    BindingFlags.Public |
                    BindingFlags.Static)
                .Where(field =>
                    field.FieldType ==
                    typeof(OpCode))
                .Select(field =>
                    (OpCode)field.GetValue(null)))
            {
                table.TryAdd(unchecked((ushort)opCode.Value), opCode);
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
                foreach (MethodDefinition method in type.GetMethods()
                    .Select(_metadata.GetMethodDefinition))
                {
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
                    foreach (string key in callees.Select(callee =>
                        caller + "|" + callee))
                    {
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
                foreach (MethodDefinition method in type.GetMethods()
                    .Select(_metadata.GetMethodDefinition))
                {
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

        private IReadOnlyList<string> ScanMembersUsedBy(
            IReadOnlyCollection<string> typeNames,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> membersByType)
        {
            var members = new SortedSet<string>(StringComparer.Ordinal);
            foreach (TypeDefinitionHandle typeHandle in _metadata.TypeDefinitions)
            {
                TypeDefinition type = _metadata.GetTypeDefinition(typeHandle);
                foreach (MethodDefinition method in type.GetMethods()
                    .Select(_metadata.GetMethodDefinition))
                {
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    (TypeDefinitionHandle userType, string userMethod) = DeclaringMember(typeHandle, method);
                    string userTypeName = TypeName(userType);
                    bool wholeType = typeNames.Any(
                        name => userTypeName == name || userTypeName.StartsWith(name + "+", StringComparison.Ordinal));
                    bool selectedMember =
                        membersByType.TryGetValue(userTypeName, out IReadOnlyCollection<string> names) &&
                        names.Contains(userMethod);
                    if (!wholeType && !selectedMember)
                    {
                        continue;
                    }

                    foreach ((OpCode instruction, EntityHandle operand) in MemberOperands(method))
                    {
                        (string memberType, string member) = instruction.OperandType == OperandType.InlineField
                            ? DescribeField(operand)
                            : DescribeMethodMember(operand);
                        members.Add(memberType + "::" + member);
                    }
                }
            }

            return members.ToList();
        }

        private ReachableMemberClosure ScanMembersReachableFrom(
            IReadOnlyCollection<string> entryMembers,
            IReadOnlyCollection<string> seedTypeNames)
        {
            var typesByName = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
            var methodsByUserMember = new Dictionary<string, List<MethodDefinitionHandle>>(StringComparer.Ordinal);
            var methodsByDeclaredMember = new Dictionary<string, List<MethodDefinitionHandle>>(StringComparer.Ordinal);
            foreach (TypeDefinitionHandle typeHandle in _metadata.TypeDefinitions)
            {
                string typeName = TypeName(typeHandle);
                typesByName.TryAdd(typeName, typeHandle);
                TypeDefinition type = _metadata.GetTypeDefinition(typeHandle);
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                    Add(methodsByDeclaredMember, DeclaredMethodKey(typeHandle, method), methodHandle);
                    if (method.RelativeVirtualAddress != 0)
                    {
                        Add(methodsByUserMember, UserMethodKey(typeHandle, method), methodHandle);
                    }
                }
            }

            var methodReferences = new Dictionary<EntityHandle, int>();
            foreach (TypeDefinitionHandle typeHandle in _metadata.TypeDefinitions)
            {
                foreach (MethodDefinitionHandle methodHandle in _metadata.GetTypeDefinition(typeHandle).GetMethods())
                {
                    MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    foreach (IlInstruction instruction in Instructions(method).Where(
                        item => item.OpCode.OperandType == OperandType.InlineMethod))
                    {
                        methodReferences[instruction.Operand] =
                            methodReferences.TryGetValue(instruction.Operand, out int count) ? count + 1 : 1;
                    }
                }
            }

            var lazyFactoryMethods = new Dictionary<MethodDefinitionHandle, string>();
            var lazyFactoriesByOwner =
                new Dictionary<MethodDefinitionHandle, IReadOnlyDictionary<EntityHandle, string>>();
            IReadOnlyDictionary<EntityHandle, string> noLazyFactories =
                new Dictionary<EntityHandle, string>();
            foreach (TypeDefinitionHandle typeHandle in _metadata.TypeDefinitions)
            {
                foreach (MethodDefinitionHandle methodHandle in _metadata.GetTypeDefinition(typeHandle).GetMethods())
                {
                    MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    IReadOnlyDictionary<EntityHandle, string> factories = LazyFactories(method, methodReferences);
                    if (factories.Count > 0)
                    {
                        lazyFactoriesByOwner.Add(methodHandle, factories);
                    }

                    foreach (KeyValuePair<EntityHandle, string> factory in factories)
                    {
                        if (factory.Key.Kind == HandleKind.MethodDefinition)
                        {
                            lazyFactoryMethods.TryAdd((MethodDefinitionHandle)factory.Key, factory.Value);
                        }
                    }
                }
            }

            var pending = new Queue<string>();
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var processedMethods = new HashSet<MethodDefinitionHandle>();
            var predecessors = new Dictionary<string, string>(StringComparer.Ordinal);
            var members = new SortedSet<string>(StringComparer.Ordinal);
            var memberCallers = new Dictionary<string, string>(StringComparer.Ordinal);
            var instantiatedTypes = new HashSet<TypeDefinitionHandle>();
            var virtualSites = new List<(
                string Type,
                string Member,
                (int Generic, int Parameters) Shape,
                string Caller)>();
            var deferredLazyFactories =
                new Dictionary<string, List<(EntityHandle Method, string Caller, bool Virtual)>>(StringComparer.Ordinal);
            var realizedLazyTypes = new HashSet<string>(StringComparer.Ordinal);
            bool realizeEveryLazy = false;

            bool LazyRealized(string key) => realizeEveryLazy || realizedLazyTypes.Contains(key);

            void EnqueueUserMember(string key, string caller)
            {
                if (methodsByUserMember.ContainsKey(key) && reached.Add(key))
                {
                    predecessors[key] = caller;
                    pending.Enqueue(key);
                }
            }

            void EnqueueDeclaredMember(
                string typeName,
                string member,
                string caller,
                (int Generic, int Parameters)? shape = null)
            {
                if (!methodsByDeclaredMember.TryGetValue(typeName + "::" + member, out List<MethodDefinitionHandle> handles))
                {
                    return;
                }

                foreach (MethodDefinitionHandle handle in handles)
                {
                    if (shape == null || MethodShape(handle) == shape.Value)
                    {
                        MethodDefinition method = _metadata.GetMethodDefinition(handle);
                        EnqueueUserMember(UserMethodKey(method.GetDeclaringType(), method), caller);
                    }
                }
            }

            void EnqueueStaticConstructor(string typeName, string caller) =>
                EnqueueDeclaredMember(typeName, ".cctor", caller);

            void EnqueueMethod(EntityHandle handle, string caller)
            {
                switch (handle.Kind)
                {
                    case HandleKind.MethodDefinition:
                        MethodDefinition definition = _metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                        string definitionType = TypeName(definition.GetDeclaringType());
                        EnqueueStaticConstructor(definitionType, caller);
                        EnqueueUserMember(UserMethodKey(definition.GetDeclaringType(), definition), caller);
                        break;

                    case HandleKind.MemberReference:
                        (string type, string member, _) = DescribeMethod(handle);
                        EnqueueStaticConstructor(type, caller);
                        EnqueueDeclaredMember(type, member, caller, MethodShape(handle));
                        break;

                    case HandleKind.MethodSpecification:
                        EnqueueMethod(_metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method, caller);
                        break;
                }
            }

            void RequeueMethod(EntityHandle handle)
            {
                switch (handle.Kind)
                {
                    case HandleKind.MethodDefinition:
                        MethodDefinition definition = _metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                        pending.Enqueue(UserMethodKey(definition.GetDeclaringType(), definition));
                        break;

                    case HandleKind.MemberReference:
                        (string type, string member, _) = DescribeMethod(handle);
                        if (methodsByDeclaredMember.TryGetValue(
                                type + "::" + member, out List<MethodDefinitionHandle> handles))
                        {
                            (int Generic, int Parameters) shape = MethodShape(handle);
                            foreach (MethodDefinitionHandle methodHandle in handles.Where(item => MethodShape(item) == shape))
                            {
                                MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                                pending.Enqueue(UserMethodKey(method.GetDeclaringType(), method));
                            }
                        }
                        break;

                    case HandleKind.MethodSpecification:
                        RequeueMethod(_metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                        break;
                }
            }

            void ExpandVirtualTarget(
                (string Type, string Member, (int Generic, int Parameters) Shape, string Caller) site,
                TypeDefinitionHandle instantiatedType)
            {
                if (!IsAssignableTo(instantiatedType, site.Type))
                {
                    return;
                }

                foreach (TypeDefinitionHandle owner in InAssemblyVirtualDispatchOwners(instantiatedType))
                {
                    TypeDefinition type = _metadata.GetTypeDefinition(owner);
                    foreach (MethodImplementationHandle implementationHandle in type.GetMethodImplementations())
                    {
                        MethodImplementation implementation = _metadata.GetMethodImplementation(implementationHandle);
                        (string declarationType, string declarationMember, _) =
                            DescribeMethod(implementation.MethodDeclaration);
                        if (declarationType == site.Type &&
                            declarationMember == site.Member &&
                            MethodShape(implementation.MethodDeclaration) == site.Shape)
                        {
                            EnqueueMethod(implementation.MethodBody, site.Caller);
                        }
                    }

                    foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                    {
                        MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                        string name = _metadata.GetString(method.Name);
                        if (method.RelativeVirtualAddress != 0 &&
                            (method.Attributes & MethodAttributes.Virtual) != 0 &&
                            (name == site.Member || name.EndsWith("." + site.Member, StringComparison.Ordinal)) &&
                            MethodShape(methodHandle) == site.Shape)
                        {
                            EnqueueUserMember(UserMethodKey(owner, method), site.Caller);
                        }
                    }
                }
            }

            void MarkInstantiated(
                string typeName,
                string caller,
                bool required = false,
                bool reachConstructors = false)
            {
                if (!typesByName.TryGetValue(typeName, out TypeDefinitionHandle typeHandle))
                {
                    if (required)
                    {
                        throw new InvalidOperationException($"Preparation seed type not found: {typeName}.");
                    }

                    return;
                }

                if (!instantiatedTypes.Add(typeHandle))
                {
                    return;
                }

                EnqueueStaticConstructor(typeName, caller);
                if (reachConstructors)
                {
                    EnqueueDeclaredMember(typeName, ".ctor", caller);
                }

                EnqueueRuntimeCallbacks(typeHandle, caller);
                foreach ((string Type, string Member, (int Generic, int Parameters) Shape, string Caller) site in
                    virtualSites.ToArray())
                {
                    ExpandVirtualTarget(site, typeHandle);
                }
            }

            void EnqueueRuntimeCallbacks(TypeDefinitionHandle typeHandle, string caller)
            {
                foreach (MethodDefinitionHandle methodHandle in RuntimeCallbackMethods(typeHandle))
                {
                    MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                    EnqueueUserMember(UserMethodKey(method.GetDeclaringType(), method), caller);
                }
            }

            void AddVirtualSite(EntityHandle handle, string caller)
            {
                (string type, string member, _) = DescribeMethod(handle);
                var site = (type, member, MethodShape(handle), caller);
                virtualSites.Add(site);
                foreach (TypeDefinitionHandle instantiatedType in instantiatedTypes.ToArray())
                {
                    ExpandVirtualTarget(site, instantiatedType);
                }
            }

            void ReachDelegate(EntityHandle method, string caller, bool virtualTarget, bool requeue)
            {
                EnqueueMethod(method, caller);
                if (requeue)
                {
                    RequeueMethod(method);
                }

                if (virtualTarget)
                {
                    AddVirtualSite(method, caller);
                }
            }

            void DeferLazyFactory(string key, EntityHandle method, string caller, bool virtualTarget)
            {
                if (LazyRealized(key))
                {
                    ReachDelegate(method, caller, virtualTarget, requeue: true);
                    return;
                }

                if (!deferredLazyFactories.TryGetValue(
                        key, out List<(EntityHandle Method, string Caller, bool Virtual)> factories))
                {
                    factories = new List<(EntityHandle Method, string Caller, bool Virtual)>();
                    deferredLazyFactories.Add(key, factories);
                }

                factories.Add((method, caller, virtualTarget));
            }

            void RealizeLazy(string key)
            {
                if (!realizedLazyTypes.Add(key) ||
                    !deferredLazyFactories.TryGetValue(
                        key, out List<(EntityHandle Method, string Caller, bool Virtual)> factories))
                {
                    return;
                }

                foreach ((EntityHandle method, string caller, bool virtualTarget) in factories)
                {
                    ReachDelegate(method, caller, virtualTarget, requeue: true);
                }
            }

            void RealizeEveryLazy()
            {
                realizeEveryLazy = true;
                foreach (string key in deferredLazyFactories.Keys.ToArray())
                {
                    RealizeLazy(key);
                }
            }

            foreach (string seedTypeName in seedTypeNames)
            {
                MarkInstantiated(seedTypeName, caller: null, required: true, reachConstructors: true);
            }

            foreach (string entry in entryMembers)
            {
                EnqueueUserMember(entry, caller: null);
                if (!reached.Contains(entry))
                {
                    throw new InvalidOperationException($"Preparation entry point not found: {entry}.");
                }
            }

            while (pending.TryDequeue(out string key))
            {
                foreach (MethodDefinitionHandle methodHandle in methodsByUserMember[key])
                {
                    if (processedMethods.Contains(methodHandle) ||
                        (lazyFactoryMethods.TryGetValue(methodHandle, out string lazyFactoryKey) &&
                         !LazyRealized(lazyFactoryKey)))
                    {
                        continue;
                    }

                    processedMethods.Add(methodHandle);
                    MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                    EnqueueStaticConstructor(TypeName(method.GetDeclaringType()), key);
                    IReadOnlyDictionary<EntityHandle, string> lazyFactories =
                        lazyFactoriesByOwner.TryGetValue(
                            methodHandle, out IReadOnlyDictionary<EntityHandle, string> foundFactories)
                            ? foundFactories
                            : noLazyFactories;
                    IReadOnlySet<EntityHandle> constrainedDispatches = ConstrainedDispatchTargets(method);
                    foreach ((OpCode instruction, EntityHandle operand) in MemberOperands(method))
                    {
                        (string memberType, string member) = instruction.OperandType == OperandType.InlineField
                            ? DescribeField(operand)
                            : DescribeMethodMember(operand);
                        string memberKey = memberType + "::" + member;
                        members.Add(memberKey);
                        memberCallers.TryAdd(memberKey, key);

                        foreach (EntityHandle typeArgument in TypeArguments(operand))
                        {
                            MarkInstantiated(TypeName(typeArgument), key);
                        }

                        if (instruction.OperandType == OperandType.InlineMethod)
                        {
                            if (TryLazyMember(
                                    operand, "get_Value", out string lazyKey, out bool openGenericLazy))
                            {
                                if (openGenericLazy)
                                {
                                    RealizeEveryLazy();
                                }
                                else
                                {
                                    RealizeLazy(lazyKey);
                                }
                            }

                            if (instruction == OpCodes.Newobj ||
                                instruction == OpCodes.Ldftn ||
                                instruction == OpCodes.Ldvirtftn ||
                                (instruction == OpCodes.Call && member == ".ctor"))
                            {
                                (string constructedType, _, _) = DescribeMethod(operand);
                                MarkInstantiated(constructedType, key);
                            }

                            bool virtualTarget = instruction == OpCodes.Ldvirtftn;
                            if ((instruction == OpCodes.Ldftn || virtualTarget) &&
                                lazyFactories.TryGetValue(operand, out string factoryKey))
                            {
                                DeferLazyFactory(factoryKey, operand, key, virtualTarget);
                            }
                            else
                            {
                                EnqueueMethod(operand, key);
                                bool constrainedDispatch =
                                    constrainedDispatches.Contains(operand) &&
                                    (instruction == OpCodes.Call || instruction == OpCodes.Ldftn);
                                if (instruction == OpCodes.Callvirt || virtualTarget || constrainedDispatch)
                                {
                                    AddVirtualSite(operand, key);
                                }
                            }
                        }
                        else if (instruction == OpCodes.Ldsfld ||
                            instruction == OpCodes.Ldsflda ||
                            instruction == OpCodes.Stsfld)
                        {
                            EnqueueStaticConstructor(memberType, key);
                        }
                    }

                    foreach (EntityHandle typeOperand in InstantiatedTypeOperands(method))
                    {
                        MarkInstantiated(TypeName(typeOperand), key);
                        foreach (EntityHandle typeArgument in TypeArguments(typeOperand))
                        {
                            MarkInstantiated(TypeName(typeArgument), key);
                        }
                    }
                }
            }

            var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string member in members)
            {
                var path = new List<string> { member };
                string method = memberCallers[member];
                while (method != null)
                {
                    path.Add(method);
                    if (!predecessors.TryGetValue(method, out string previous))
                    {
                        break;
                    }

                    method = previous;
                }

                path.Reverse();
                paths.Add(member, string.Join(" -> ", path));
            }

            return new ReachableMemberClosure(members.ToList(), paths);
        }

        private IEnumerable<TypeDefinitionHandle> InAssemblyHierarchy(TypeDefinitionHandle typeHandle)
        {
            TypeDefinitionHandle current = typeHandle;
            while (!current.IsNil)
            {
                yield return current;
                EntityHandle parent = _metadata.GetTypeDefinition(current).BaseType;
                if (parent.Kind == HandleKind.TypeSpecification)
                {
                    parent = GenericTypeDefinition((TypeSpecificationHandle)parent);
                }

                current = parent.Kind == HandleKind.TypeDefinition
                    ? (TypeDefinitionHandle)parent
                    : default;
            }
        }

        private IEnumerable<TypeDefinitionHandle> InAssemblyInterfaces(TypeDefinitionHandle typeHandle)
        {
            var pending = new Stack<EntityHandle>();
            var visited = new HashSet<EntityHandle>();
            foreach (TypeDefinitionHandle owner in InAssemblyHierarchy(typeHandle))
            {
                foreach (InterfaceImplementationHandle implementation in
                    _metadata.GetTypeDefinition(owner).GetInterfaceImplementations())
                {
                    pending.Push(_metadata.GetInterfaceImplementation(implementation).Interface);
                }
            }

            while (pending.TryPop(out EntityHandle current))
            {
                if (current.Kind == HandleKind.TypeSpecification)
                {
                    current = GenericTypeDefinition((TypeSpecificationHandle)current);
                }

                if (current.Kind != HandleKind.TypeDefinition ||
                    !visited.Add(current))
                {
                    continue;
                }

                var definition = (TypeDefinitionHandle)current;
                yield return definition;
                foreach (InterfaceImplementationHandle implementation in
                    _metadata.GetTypeDefinition(definition).GetInterfaceImplementations())
                {
                    pending.Push(_metadata.GetInterfaceImplementation(implementation).Interface);
                }
            }
        }

        private IEnumerable<TypeDefinitionHandle> InAssemblyVirtualDispatchOwners(
            TypeDefinitionHandle instantiatedType) =>
            InAssemblyHierarchy(instantiatedType)
                .Union(InAssemblyInterfaces(instantiatedType));

        // A constructed type can hand its code to library code through (a) a MethodImpl for an external declaration,
        // (b) an override of an external base slot, or (c) an implementation of an external interface. Default methods on
        // in-assembly interfaces are owners too. Rules (b) and (c) match by name, generic arity and parameter count; that
        // deliberately over-approximates overload signatures. ECMA-335 I.8.4.4 requires a static virtual implementation to
        // use a MethodImpl, so rule (a) covers it. Soundness also relies on the preparation test rejecting the reflective
        // and dynamic APIs that could construct or invoke XMAT code outside these metadata relationships.
        private IReadOnlyList<MethodDefinitionHandle> RuntimeCallbackMethods(TypeDefinitionHandle instantiatedType)
        {
            var callbacks = new HashSet<MethodDefinitionHandle>();
            Type[] externalInterfaces = ExternalInterfaces(instantiatedType).Distinct().ToArray();

            foreach (TypeDefinitionHandle owner in InAssemblyHierarchy(instantiatedType)
                .Concat(InAssemblyInterfaces(instantiatedType))
                .Distinct())
            {
                TypeDefinition type = _metadata.GetTypeDefinition(owner);
                foreach (MethodImplementationHandle implementationHandle in type.GetMethodImplementations())
                {
                    MethodImplementation implementation = _metadata.GetMethodImplementation(implementationHandle);
                    var declaration = DescribeMethod(implementation.MethodDeclaration);
                    if (IsExternalType(declaration.DeclaringType) &&
                        implementation.MethodBody.Kind == HandleKind.MethodDefinition)
                    {
                        callbacks.Add((MethodDefinitionHandle)implementation.MethodBody);
                    }
                    else if (implementation.MethodBody.Kind == HandleKind.MethodDefinition &&
                        MethodDeclarationOverridesExternalSlot(implementation.MethodDeclaration))
                    {
                        callbacks.Add((MethodDefinitionHandle)implementation.MethodBody);
                    }
                }

                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0 ||
                        (method.Attributes & MethodAttributes.Virtual) == 0)
                    {
                        continue;
                    }

                    if (OverridesExternalSlot(owner, methodHandle) ||
                        externalInterfaces.Any(contract => MatchesRuntimeMethod(methodHandle, contract)))
                    {
                        callbacks.Add(methodHandle);
                    }
                }
            }

            return callbacks.ToList();
        }

        private IEnumerable<Type> ExternalInterfaces(TypeDefinitionHandle typeHandle)
        {
            var pending = new Stack<EntityHandle>();
            var visited = new HashSet<EntityHandle>();
            foreach (TypeDefinitionHandle owner in InAssemblyHierarchy(typeHandle))
            {
                foreach (InterfaceImplementationHandle implementation in
                    _metadata.GetTypeDefinition(owner).GetInterfaceImplementations())
                {
                    pending.Push(_metadata.GetInterfaceImplementation(implementation).Interface);
                }
            }

            while (pending.TryPop(out EntityHandle current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current.Kind == HandleKind.TypeSpecification)
                {
                    current = GenericTypeDefinition((TypeSpecificationHandle)current);
                }

                if (current.Kind == HandleKind.TypeDefinition)
                {
                    foreach (InterfaceImplementationHandle implementation in
                        _metadata.GetTypeDefinition((TypeDefinitionHandle)current).GetInterfaceImplementations())
                    {
                        pending.Push(_metadata.GetInterfaceImplementation(implementation).Interface);
                    }
                    continue;
                }

                yield return ResolveRequiredExternalType(current, "interface");
            }
        }

        private bool OverridesExternalSlot(TypeDefinitionHandle owner, MethodDefinitionHandle methodHandle)
        {
            MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
            if ((method.Attributes & MethodAttributes.NewSlot) != 0)
            {
                return false;
            }

            string name = _metadata.GetString(method.Name);
            (int Generic, int Parameters) shape = MethodShape(methodHandle);
            EntityHandle parent = _metadata.GetTypeDefinition(owner).BaseType;
            while (!parent.IsNil)
            {
                if (parent.Kind == HandleKind.TypeSpecification)
                {
                    parent = GenericTypeDefinition((TypeSpecificationHandle)parent);
                }

                if (parent.Kind == HandleKind.TypeDefinition)
                {
                    TypeDefinition definition = _metadata.GetTypeDefinition((TypeDefinitionHandle)parent);
                    MethodDefinitionHandle[] declarations = definition.GetMethods().Where(candidate =>
                    {
                        MethodDefinition candidateMethod = _metadata.GetMethodDefinition(candidate);
                        return (candidateMethod.Attributes & MethodAttributes.Virtual) != 0 &&
                            _metadata.GetString(candidateMethod.Name) == name &&
                            MethodShape(candidate) == shape;
                    }).ToArray();
                    if (declarations.Length != 0)
                    {
                        return declarations.Any(declaration =>
                            ParentMethodOverridesExternalSlot(parent, declaration));
                    }

                    parent = definition.BaseType;
                    continue;
                }

                return MatchesRuntimeMethod(
                    methodHandle,
                    ResolveRequiredExternalType(parent, "base type"));
            }

            return false;
        }

        private bool ParentMethodOverridesExternalSlot(
            EntityHandle parent,
            MethodDefinitionHandle declaration)
        {
            if (MethodImplementsExternalSlot(
                    (TypeDefinitionHandle)parent,
                    declaration,
                    new HashSet<MethodDefinitionHandle>()))
            {
                return true;
            }

            if ((_metadata.GetMethodDefinition(declaration).Attributes & MethodAttributes.NewSlot) != 0)
            {
                return false;
            }

            return OverridesExternalSlot((TypeDefinitionHandle)parent, declaration);
        }

        private bool MethodDeclarationOverridesExternalSlot(EntityHandle declaration)
        {
            foreach (MethodDefinitionHandle methodHandle in InAssemblyMethodDefinitions(declaration))
            {
                TypeDefinitionHandle owner = _metadata.GetMethodDefinition(methodHandle).GetDeclaringType();
                if (MethodImplementsExternalSlot(owner, methodHandle, new HashSet<MethodDefinitionHandle>()) ||
                    OverridesExternalSlot(owner, methodHandle))
                {
                    return true;
                }
            }

            return false;
        }

        private bool MethodImplementsExternalSlot(
            TypeDefinitionHandle owner,
            MethodDefinitionHandle methodHandle,
            HashSet<MethodDefinitionHandle> visited)
        {
            if (!visited.Add(methodHandle))
            {
                return false;
            }

            foreach (MethodImplementationHandle implementationHandle in
                _metadata.GetTypeDefinition(owner).GetMethodImplementations())
            {
                MethodImplementation implementation = _metadata.GetMethodImplementation(implementationHandle);
                if (!InAssemblyMethodDefinitions(implementation.MethodBody).Contains(methodHandle))
                {
                    continue;
                }

                var declaration = DescribeMethod(implementation.MethodDeclaration);
                if (IsExternalType(declaration.DeclaringType))
                {
                    return true;
                }

                foreach (MethodDefinitionHandle declarationHandle in
                    InAssemblyMethodDefinitions(implementation.MethodDeclaration))
                {
                    TypeDefinitionHandle declarationOwner =
                        _metadata.GetMethodDefinition(declarationHandle).GetDeclaringType();
                    if (MethodImplementsExternalSlot(declarationOwner, declarationHandle, visited) ||
                        OverridesExternalSlot(declarationOwner, declarationHandle))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private IEnumerable<MethodDefinitionHandle> InAssemblyMethodDefinitions(EntityHandle handle)
        {
            if (handle.Kind == HandleKind.MethodSpecification)
            {
                handle = _metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            }

            if (handle.Kind == HandleKind.MethodDefinition)
            {
                yield return (MethodDefinitionHandle)handle;
                yield break;
            }

            if (handle.Kind != HandleKind.MemberReference)
            {
                yield break;
            }

            MemberReference reference = _metadata.GetMemberReference((MemberReferenceHandle)handle);
            EntityHandle owner = reference.Parent;
            if (owner.Kind == HandleKind.TypeSpecification)
            {
                owner = GenericTypeDefinition((TypeSpecificationHandle)owner);
            }

            if (owner.Kind != HandleKind.TypeDefinition)
            {
                yield break;
            }

            string name = _metadata.GetString(reference.Name);
            (int Generic, int Parameters) shape = MethodShape(handle);
            foreach (MethodDefinitionHandle candidate in
                _metadata.GetTypeDefinition((TypeDefinitionHandle)owner).GetMethods())
            {
                MethodDefinition method = _metadata.GetMethodDefinition(candidate);
                if (_metadata.GetString(method.Name) == name &&
                    MethodShape(candidate) == shape)
                {
                    yield return candidate;
                }
            }
        }

        private bool MatchesRuntimeMethod(MethodDefinitionHandle methodHandle, Type runtimeType)
        {
            MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
            string name = _metadata.GetString(method.Name);
            (int Generic, int Parameters) shape = MethodShape(methodHandle);
            return RuntimeMethods(runtimeType).Any(candidate =>
                (candidate.Name == name || name.EndsWith("." + candidate.Name, StringComparison.Ordinal)) &&
                candidate.GetGenericArguments().Length == shape.Generic &&
                candidate.GetParameters().Length == shape.Parameters);
        }

        private static IEnumerable<MethodInfo> RuntimeMethods(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                foreach (MethodInfo method in current.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    yield return method;
                }
            }
        }

        private bool IsExternalType(EntityHandle handle)
        {
            if (handle.Kind == HandleKind.TypeSpecification)
            {
                handle = GenericTypeDefinition((TypeSpecificationHandle)handle);
            }

            return handle.Kind == HandleKind.TypeReference &&
                ReferencedAssembly((TypeReferenceHandle)handle) != _assemblyName;
        }

        private Type ResolveExternalType(EntityHandle handle)
        {
            if (handle.Kind == HandleKind.TypeSpecification)
            {
                handle = GenericTypeDefinition((TypeSpecificationHandle)handle);
            }

            return handle.Kind == HandleKind.TypeReference
                ? ResolveReference((TypeReferenceHandle)handle)
                : null;
        }

        private Type ResolveRequiredExternalType(EntityHandle handle, string relationship)
        {
            Type resolved = ResolveExternalType(handle);
            return resolved ??
                throw new InvalidOperationException(
                    $"Preparation external {relationship} could not be resolved: {TypeName(handle)}.");
        }

        private List<EntityHandle> InstantiatedTypeOperands(MethodDefinition method)
        {
            return Instructions(method)
                .Where(instruction =>
                    instruction.OpCode == OpCodes.Initobj ||
                    instruction.OpCode == OpCodes.Box ||
                    instruction.OpCode == OpCodes.Constrained ||
                    instruction.OpCode == OpCodes.Newarr ||
                    instruction.OpCode == OpCodes.Isinst ||
                    instruction.OpCode == OpCodes.Castclass ||
                    instruction.OpCode == OpCodes.Unbox ||
                    instruction.OpCode == OpCodes.Unbox_Any ||
                    instruction.OpCode == OpCodes.Ldelema ||
                    instruction.OpCode == OpCodes.Ldobj ||
                    instruction.OpCode == OpCodes.Stobj ||
                    instruction.OpCode == OpCodes.Sizeof)
                .Select(instruction => instruction.Operand)
                .ToList();
        }

        // ECMA-335 III.2.1 permits constrained. before call and ldftn. Both can dispatch an in-assembly static virtual
        // interface member to the implementation on the constrained type, so both create a virtual site.
        private IReadOnlySet<EntityHandle> ConstrainedDispatchTargets(MethodDefinition method)
        {
            IReadOnlyList<IlInstruction> instructions = Instructions(method);
            var targets = new HashSet<EntityHandle>();
            for (int index = 1; index < instructions.Count; index++)
            {
                if (instructions[index - 1].OpCode == OpCodes.Constrained &&
                    (instructions[index].OpCode == OpCodes.Call ||
                     instructions[index].OpCode == OpCodes.Ldftn))
                {
                    targets.Add(instructions[index].Operand);
                }
            }

            return targets;
        }

        // The exact C# lambda-to-Lazy<T> construction shape. Only a compiler-generated lambda referenced by this one ldftn
        // is deferred: ldftn, Func<T> construction, an optional compiler cache store, an optional mode, then Lazy<T>.
        private IReadOnlyDictionary<EntityHandle, string> LazyFactories(
            MethodDefinition method,
            IReadOnlyDictionary<EntityHandle, int> references)
        {
            var factories = new Dictionary<EntityHandle, string>();
            IReadOnlyList<IlInstruction> instructions = Instructions(method);
            for (int index = 0; index < instructions.Count; index++)
            {
                IlInstruction load = instructions[index];
                if (load.OpCode != OpCodes.Ldftn ||
                    load.Operand.Kind != HandleKind.MethodDefinition ||
                    !references.TryGetValue(load.Operand, out int count) ||
                    count != 1)
                {
                    continue;
                }

                MethodDefinition factory = _metadata.GetMethodDefinition((MethodDefinitionHandle)load.Operand);
                string factoryName = _metadata.GetString(factory.Name);
                if (!factoryName.StartsWith("<", StringComparison.Ordinal) ||
                    !factoryName.Contains(">b__", StringComparison.Ordinal))
                {
                    continue;
                }

                int next = index + 1;
                if (next >= instructions.Count ||
                    instructions[next].OpCode != OpCodes.Newobj ||
                    DescribeMethodMember(instructions[next].Operand) != ("System.Func`1", ".ctor"))
                {
                    continue;
                }

                next++;
                if (next < instructions.Count && instructions[next].OpCode == OpCodes.Dup)
                {
                    next++;
                    if (next >= instructions.Count ||
                        instructions[next].OpCode != OpCodes.Stsfld ||
                        !DescribeField(instructions[next].Operand).Member.StartsWith("<>9__", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    next++;
                }

                if (next < instructions.Count && IsInt32Constant(instructions[next].OpCode))
                {
                    next++;
                }

                if (next < instructions.Count &&
                    instructions[next].OpCode == OpCodes.Newobj &&
                    TryLazyMember(instructions[next].Operand, ".ctor", out string key, out _))
                {
                    factories[load.Operand] = key;
                }
            }

            return factories;
        }

        private bool TryLazyMember(EntityHandle method, string member, out string key, out bool openGeneric)
        {
            (string type, string name, EntityHandle declaringType) = DescribeMethod(method);
            if (type != "System.Lazy`1" || name != member || declaringType.Kind != HandleKind.TypeSpecification)
            {
                key = null;
                openGeneric = false;
                return false;
            }

            BlobHandle signatureHandle =
                _metadata.GetTypeSpecification((TypeSpecificationHandle)declaringType).Signature;
            key = Convert.ToHexString(_metadata.GetBlobBytes(signatureHandle));
            BlobReader signature = _metadata.GetBlobReader(signatureHandle);
            openGeneric = SignatureContainsGenericParameter(ref signature);
            return true;
        }

        private static bool SignatureContainsGenericParameter(ref BlobReader signature)
        {
            SignatureTypeCode code = signature.ReadSignatureTypeCode();
            switch (code)
            {
                case SignatureTypeCode.GenericTypeParameter:
                case SignatureTypeCode.GenericMethodParameter:
                    signature.ReadCompressedInteger();
                    return true;

                case SignatureTypeCode.Pointer:
                case SignatureTypeCode.ByReference:
                case SignatureTypeCode.SZArray:
                case SignatureTypeCode.Pinned:
                case SignatureTypeCode.Sentinel:
                    return SignatureContainsGenericParameter(ref signature);

                case SignatureTypeCode.TypeHandle:
                    signature.ReadTypeHandle();
                    return false;

                case SignatureTypeCode.Array:
                    bool arrayContainsGeneric = SignatureContainsGenericParameter(ref signature);
                    signature.ReadCompressedInteger();
                    int sizes = signature.ReadCompressedInteger();
                    for (int index = 0; index < sizes; index++)
                    {
                        signature.ReadCompressedInteger();
                    }
                    int bounds = signature.ReadCompressedInteger();
                    for (int index = 0; index < bounds; index++)
                    {
                        signature.ReadCompressedSignedInteger();
                    }
                    return arrayContainsGeneric;

                case SignatureTypeCode.GenericTypeInstance:
                    signature.ReadSignatureTypeCode();
                    signature.ReadTypeHandle();
                    int arguments = signature.ReadCompressedInteger();
                    bool genericInstanceContainsGeneric = false;
                    for (int index = 0; index < arguments; index++)
                    {
                        genericInstanceContainsGeneric |= SignatureContainsGenericParameter(ref signature);
                    }
                    return genericInstanceContainsGeneric;

                case SignatureTypeCode.FunctionPointer:
                    return MethodSignatureContainsGenericParameter(ref signature);

                case SignatureTypeCode.RequiredModifier:
                case SignatureTypeCode.OptionalModifier:
                    signature.ReadTypeHandle();
                    return SignatureContainsGenericParameter(ref signature);

                default:
                    return false;
            }
        }

        private static bool MethodSignatureContainsGenericParameter(ref BlobReader signature)
        {
            SignatureHeader header = signature.ReadSignatureHeader();
            if (header.IsGeneric)
            {
                signature.ReadCompressedInteger();
            }

            int parameters = signature.ReadCompressedInteger();
            bool containsGeneric = SignatureContainsGenericParameter(ref signature);
            for (int index = 0; index < parameters; index++)
            {
                containsGeneric |= SignatureContainsGenericParameter(ref signature);
            }

            return containsGeneric;
        }

        private static bool IsInt32Constant(OpCode opCode) =>
            opCode == OpCodes.Ldc_I4 ||
            opCode == OpCodes.Ldc_I4_S ||
            opCode == OpCodes.Ldc_I4_M1 ||
            opCode == OpCodes.Ldc_I4_0 ||
            opCode == OpCodes.Ldc_I4_1 ||
            opCode == OpCodes.Ldc_I4_2 ||
            opCode == OpCodes.Ldc_I4_3 ||
            opCode == OpCodes.Ldc_I4_4 ||
            opCode == OpCodes.Ldc_I4_5 ||
            opCode == OpCodes.Ldc_I4_6 ||
            opCode == OpCodes.Ldc_I4_7 ||
            opCode == OpCodes.Ldc_I4_8;

        private static void Add(
            Dictionary<string, List<MethodDefinitionHandle>> methods,
            string key,
            MethodDefinitionHandle handle)
        {
            if (!methods.TryGetValue(key, out List<MethodDefinitionHandle> handles))
            {
                handles = new List<MethodDefinitionHandle>();
                methods.Add(key, handles);
            }

            handles.Add(handle);
        }

        private string DeclaredMethodKey(TypeDefinitionHandle type, MethodDefinition method) =>
            TypeName(type) + "::" + _metadata.GetString(method.Name);

        private string UserMethodKey(TypeDefinitionHandle type, MethodDefinition method)
        {
            (TypeDefinitionHandle userType, string userMethod) = DeclaringMember(type, method);
            return TypeName(userType) + "::" + userMethod;
        }

        private (int Generic, int Parameters) MethodShape(EntityHandle handle)
        {
            BlobReader signature;
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                    signature = _metadata.GetBlobReader(
                        _metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Signature);
                    break;
                case HandleKind.MemberReference:
                    signature = _metadata.GetBlobReader(
                        _metadata.GetMemberReference((MemberReferenceHandle)handle).Signature);
                    break;
                case HandleKind.MethodSpecification:
                    return MethodShape(_metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                default:
                    return (-1, -1);
            }

            SignatureHeader header = signature.ReadSignatureHeader();
            int generic = header.IsGeneric ? signature.ReadCompressedInteger() : 0;
            return (generic, signature.ReadCompressedInteger());
        }

        private IReadOnlyList<EntityHandle> TypeArguments(EntityHandle handle)
        {
            var types = new List<EntityHandle>();
            switch (handle.Kind)
            {
                case HandleKind.MethodSpecification:
                    BlobReader method = _metadata.GetBlobReader(
                        _metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Signature);
                    method.ReadSignatureHeader();
                    int arguments = method.ReadCompressedInteger();
                    for (int index = 0; index < arguments; index++)
                    {
                        CollectSignatureType(ref method, types);
                    }
                    break;

                case HandleKind.MemberReference:
                    EntityHandle parent = _metadata.GetMemberReference((MemberReferenceHandle)handle).Parent;
                    if (parent.Kind == HandleKind.TypeSpecification)
                    {
                        BlobReader memberType = _metadata.GetBlobReader(
                            _metadata.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                        CollectSignatureType(ref memberType, types);
                    }
                    break;

                case HandleKind.TypeSpecification:
                    BlobReader type = _metadata.GetBlobReader(
                        _metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                    CollectSignatureType(ref type, types);
                    break;
            }

            return types;
        }

        private static void CollectSignatureType(ref BlobReader signature, List<EntityHandle> types)
        {
            SignatureTypeCode code = signature.ReadSignatureTypeCode();
            switch (code)
            {
                case SignatureTypeCode.Pointer:
                case SignatureTypeCode.ByReference:
                case SignatureTypeCode.SZArray:
                case SignatureTypeCode.Pinned:
                case SignatureTypeCode.Sentinel:
                    CollectSignatureType(ref signature, types);
                    break;

                case SignatureTypeCode.TypeHandle:
                    types.Add(signature.ReadTypeHandle());
                    break;

                case SignatureTypeCode.GenericTypeParameter:
                case SignatureTypeCode.GenericMethodParameter:
                    signature.ReadCompressedInteger();
                    break;

                case SignatureTypeCode.Array:
                    CollectSignatureType(ref signature, types);
                    signature.ReadCompressedInteger();
                    int sizes = signature.ReadCompressedInteger();
                    for (int index = 0; index < sizes; index++)
                    {
                        signature.ReadCompressedInteger();
                    }
                    int bounds = signature.ReadCompressedInteger();
                    for (int index = 0; index < bounds; index++)
                    {
                        signature.ReadCompressedSignedInteger();
                    }
                    break;

                case SignatureTypeCode.GenericTypeInstance:
                    signature.ReadSignatureTypeCode();
                    types.Add(signature.ReadTypeHandle());
                    int arguments = signature.ReadCompressedInteger();
                    for (int index = 0; index < arguments; index++)
                    {
                        CollectSignatureType(ref signature, types);
                    }
                    break;

                case SignatureTypeCode.FunctionPointer:
                    CollectMethodSignature(ref signature, types);
                    break;

                case SignatureTypeCode.RequiredModifier:
                case SignatureTypeCode.OptionalModifier:
                    types.Add(signature.ReadTypeHandle());
                    CollectSignatureType(ref signature, types);
                    break;
            }
        }

        private static void CollectMethodSignature(ref BlobReader signature, List<EntityHandle> types)
        {
            SignatureHeader header = signature.ReadSignatureHeader();
            if (header.IsGeneric)
            {
                signature.ReadCompressedInteger();
            }

            int parameters = signature.ReadCompressedInteger();
            CollectSignatureType(ref signature, types);
            for (int index = 0; index < parameters; index++)
            {
                CollectSignatureType(ref signature, types);
            }
        }

        private bool IsAssignableTo(TypeDefinitionHandle typeHandle, string targetType)
        {
            var pending = new Stack<EntityHandle>();
            var visited = new HashSet<EntityHandle>();
            pending.Push(typeHandle);
            while (pending.TryPop(out EntityHandle current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }

                if (TypeName(current) == targetType)
                {
                    return true;
                }

                if (current.Kind == HandleKind.TypeSpecification)
                {
                    pending.Push(GenericTypeDefinition((TypeSpecificationHandle)current));
                }
                else if (current.Kind == HandleKind.TypeDefinition)
                {
                    TypeDefinition definition = _metadata.GetTypeDefinition((TypeDefinitionHandle)current);
                    if (!definition.BaseType.IsNil)
                    {
                        pending.Push(definition.BaseType);
                    }

                    foreach (InterfaceImplementationHandle implementation in definition.GetInterfaceImplementations())
                    {
                        pending.Push(_metadata.GetInterfaceImplementation(implementation).Interface);
                    }
                }
                else if (current.Kind == HandleKind.TypeReference)
                {
                    Type runtimeType = ResolveReference((TypeReferenceHandle)current);
                    if (runtimeType == null)
                    {
                        throw new InvalidOperationException(
                            $"Preparation external hierarchy type could not be resolved: {TypeName(current)}.");
                    }

                    if (RuntimeTypeIsAssignableTo(runtimeType, targetType))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool RuntimeTypeIsAssignableTo(Type type, string targetType)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                if (current.FullName == targetType ||
                    current.GetInterfaces().Any(implemented => implemented.FullName == targetType))
                {
                    return true;
                }
            }

            return false;
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
            return Instructions(method)
                .Where(instruction =>
                    instruction.OpCode.OperandType is OperandType.InlineMethod or OperandType.InlineField)
                .Select(instruction => (instruction.OpCode, instruction.Operand))
                .ToList();
        }

        private IReadOnlyList<IlInstruction> Instructions(MethodDefinition method)
        {
            if (_instructions.TryGetValue(method.RelativeVirtualAddress, out IReadOnlyList<IlInstruction> cached))
            {
                return cached;
            }

            var instructions = new List<IlInstruction>();
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
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        instructions.Add(new IlInstruction(opCode, MetadataTokens.EntityHandle(il.ReadInt32())));
                        break;

                    case OperandType.InlineSwitch:
                        int targets = il.ReadInt32();
                        Skip(ref il, checked(targets * 4));
                        instructions.Add(new IlInstruction(opCode, default));
                        break;

                    default:
                        Skip(ref il, OperandSize(opCode.OperandType));
                        instructions.Add(new IlInstruction(opCode, default));
                        break;
                }
            }

            _instructions.Add(method.RelativeVirtualAddress, instructions);
            return instructions;
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
            return _metadata.GetTypeDefinition(handle)
                .GetInterfaceImplementations()
                .Any(implementation =>
                    TypeName(
                        _metadata.GetInterfaceImplementation(
                            implementation)
                        .Interface) ==
                    ComponentConnector);
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
