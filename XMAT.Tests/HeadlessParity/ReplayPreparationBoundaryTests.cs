// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Analysis;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.Tests.HeadlessParity
{
    public class ReplayPreparationBoundaryTests
    {
        // The transitive in-assembly closure covers argument parsing, parsed/raw source construction, target checking and
        // every preparation entry called before HeadlessStorage.Configure. RunAsync's inline report-building prefixes are
        // covered by HeadlessBanTests' direct namespace scan. Rapid type analysis expands dispatch only to types that the
        // reached IL constructs or that those prefixes pass in, including their library callback slots, operand types and
        // member type arguments. Cast and array operand types deliberately over-approximate instantiation. Ordinary
        // delegates are followed immediately; the exact C# lambda-to-Lazy<T> construction edge is deferred until get_Value
        // for that T is reached, including an open-generic read reached before its construction. The reflection guard below
        // excludes unmodeled dynamic construction and invocation. ReplayOperationTests pins that both file-preparation
        // routes leave the capture reader's analysis-assets Lazy uncreated.
        [Fact]
        public void PreparationCallClosure_UsesNoLegacyGlobalBeforeStorageConfiguration()
        {
            string assembly = typeof(ReplayOperation).Assembly.Location;
            ReachableMemberClosure closure = ForbiddenCallScanner.MembersReachableFrom(
                assembly,
                new[]
                {
                    typeof(CliOutputFolder).FullName + "::CheckTarget",
                    typeof(CliCommandLine).FullName + "::Parse",
                    typeof(CliRequestBody).FullName + "::PrepareAsync",
                    typeof(RequestFileReader).FullName + "::ReadFileAsync",
                    typeof(CliReplayCommand).FullName + "::CreateRequest",
                    typeof(CliReplayCommand).FullName + "::ReadRawFileAsync",
                    typeof(RawRequest).FullName + "::FromBytes",
                    typeof(ReplayOperation).FullName + "::PrepareRunAsync",
                    typeof(ReplayOperation).FullName + "::PrepareForExport",
                    typeof(CliCurlCommand).FullName + "::Prepare",
                    typeof(CliSaveRequestCommand).FullName + "::Prepare",
                },
                new[]
                {
                    typeof(CliReport).FullName,
                    typeof(CliReplayReport).FullName,
                    typeof(CliCurlReport).FullName,
                    typeof(CliSaveRequestReport).FullName,
                    typeof(ReplayOperation).FullName,
                });
            string[] banned = closure.Members.Where(member =>
                {
                    int separator = member.IndexOf("::", StringComparison.Ordinal);
                    return separator > 0 &&
                        HeadlessBans.Find(member[..separator], member[(separator + 2)..]) != null;
                })
                .ToArray();

            Assert.True(
                banned.Length == 0,
                $"The {closure.Members.Count} reachable members include {banned.Length} banned members:\n" +
                    string.Join("\n", banned.Select(member => closure.Paths[member])));
            string[] reflection = closure.Members.Where(IsReflectiveOrDynamicAccess).ToArray();
            Assert.True(
                reflection.Length == 0,
                $"Preparation reaches {reflection.Length} reflective/dynamic construction, invocation, serialization or " +
                    "read members:\n" +
                    string.Join("\n", reflection.Select(member => closure.Paths[member])));
        }

        // This hand-reviewed manifest remains a floor: whole reader/helper types are listed; FiddlerSazHandler is
        // member-scoped because its export methods deliberately use GUI-era logging. The closure test above supplies the
        // transitive guarantee.
        [Fact]
        public void PreparationReachableTypes_UseNoLegacyGlobalBeforeStorageConfiguration()
        {
            string assembly = typeof(ReplayOperation).Assembly.Location;
            IReadOnlyList<string> used = ForbiddenCallScanner.MembersUsedBy(
                assembly,
                new[]
                {
                    typeof(ReplayOperation).FullName,
                    typeof(AnalyzeCaptureOperation).FullName,
                    typeof(SqLtReadonlyDatabase).FullName,
                    typeof(SqLtSchemaPreflight).FullName,
                    typeof(SqLtSchemaProfile).FullName,
                    typeof(SqLtCreateTableParser).FullName,
                    typeof(SqLtFileIdentity).FullName,
                    typeof(SqLtReadonlyDataTable).FullName,
                    typeof(SqLtDataset).FullName,
                    typeof(SqLtDataRecord).FullName,
                    typeof(SqLtDataEnumerable).FullName,
                    typeof(BoundedSazReader).FullName,
                    typeof(SazArchivePreflight).FullName,
                    typeof(SazHeaderFields).FullName,
                    typeof(HostAuthority).FullName,
                    typeof(StoredAuthority).FullName,
                    typeof(BinaryReadingExtensions).FullName,
                    typeof(WebServiceCaptureRecordConverter).FullName,
                    typeof(ProxyConnectionModel).FullName,
                    typeof(RequestReplayDraft).FullName,
                    typeof(ReplayDestination).FullName,
                    typeof(RequestCurlExporter).FullName,
                    typeof(RequestFileReader).FullName,
                    typeof(RequestFileWriter).FullName,
                    typeof(UriUtils).FullName,
                },
                new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
                {
                    [typeof(FiddlerSazHandler).FullName] = new[]
                    {
                        "FromFiddlerFrame",
                        "OpenEntryForParsing",
                        "TryReadAbsoluteTarget",
                        "TryGetHostText",
                        "ReadHeaders",
                        "ReadHeaderLine",
                        "ConsumeHeaderSeparator",
                        "RewindIfSeekable",
                        "IsAsciiWhitespace",
                        "IsBlankHeadLine",
                        "AddHeaderOrSkip",
                        "IsValidHeaderLine",
                        "IsValidHeaderNameCharacter",
                        "ParseTimer",
                        "IsHeaderValueTrimCharacter",
                    },
                });
            string[] banned = used.Where(member =>
                {
                    int separator = member.IndexOf("::", StringComparison.Ordinal);
                    return separator > 0 &&
                        HeadlessBans.Find(member[..separator], member[(separator + 2)..]) != null;
                })
                .ToArray();

            Assert.Empty(banned);
        }

        [Fact]
        public void ClosureModelsRuntimeCallbacksGenericValueTypesAndExternalBases()
        {
            string assembly = typeof(ReplayPreparationBoundaryTests).Assembly.Location;
            ReachableMemberClosure closure = ForbiddenCallScanner.MembersReachableFrom(
                assembly,
                new[] { typeof(ScannerFixture).FullName + "::" + nameof(ScannerFixture.Run) },
                new[] { typeof(SeededFixture).FullName });

            Assert.Contains(typeof(ScannerWitness).FullName + "::Callback", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::GenericType", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::Lazy", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::NamedFactory", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::Seed", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::ValueType", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::ExternalInterface", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::InheritedSlot", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::CovariantSlot", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::Overload", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::CovariantParent", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::CovariantDeclaration", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::DefaultMethod", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::ArrayElement", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::StaticVirtual", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::StaticDelegate", closure.Members);
            Assert.Contains(typeof(ScannerWitness).FullName + "::DefaultOverride", closure.Members);
            Assert.DoesNotContain(typeof(ScannerWitness).FullName + "::InternalInterface", closure.Members);
            ReachableMemberClosure lateLazy = ForbiddenCallScanner.MembersReachableFrom(
                assembly,
                new[] { typeof(ScannerFixture).FullName + "::" + nameof(ScannerFixture.ReadsBeforeConstruction) },
                Array.Empty<string>());
            Assert.Contains(typeof(ScannerWitness).FullName + "::LateLazy", lateLazy.Members);
            ReachableMemberClosure deferred = ForbiddenCallScanner.MembersReachableFrom(
                assembly,
                new[] { typeof(ScannerFixture).FullName + "::" + nameof(ScannerFixture.MakeDeferred) },
                Array.Empty<string>());
            Assert.DoesNotContain(typeof(ScannerWitness).FullName + "::Deferred", deferred.Members);
            Assert.True(ForbiddenCallScanner.TypeIsAssignableTo(
                assembly, typeof(CallbackStream).FullName, typeof(Stream).FullName));
            Assert.True(ForbiddenCallScanner.TypeIsAssignableTo(
                assembly, typeof(CallbackStream).FullName, typeof(object).FullName));
            Assert.Throws<InvalidOperationException>(() => ForbiddenCallScanner.MembersReachableFrom(
                assembly,
                new[] { typeof(ScannerFixture).FullName + "::" + nameof(ScannerFixture.Run) },
                new[] { "XMAT.Tests.HeadlessParity.MissingSeedType" }));
        }

        private static bool IsReflectiveOrDynamicAccess(string member)
        {
            int separator = member.IndexOf("::", StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            string type = member[..separator];
            string name = member[(separator + 2)..];
            if (type is
                    "System.Activator" or
                    "System.Text.Json.JsonSerializer" or
                    "System.Xml.Serialization.XmlSerializer" or
                    "System.ComponentModel.TypeDescriptor" ||
                type.StartsWith("System.Runtime.Serialization.", StringComparison.Ordinal) ||
                type.StartsWith("System.Net.Http.Json.", StringComparison.Ordinal) ||
                type.StartsWith("Microsoft.CSharp.RuntimeBinder.", StringComparison.Ordinal) ||
                type.StartsWith("Microsoft.CodeAnalysis.Scripting.", StringComparison.Ordinal) ||
                type.StartsWith("Microsoft.CodeAnalysis.CSharp.Scripting.", StringComparison.Ordinal) ||
                type == "System.Windows.Markup.XamlReader" ||
                type.StartsWith("System.Xaml.", StringComparison.Ordinal))
            {
                return true;
            }

            if (name.StartsWith("GetCustomAttribute", StringComparison.Ordinal) &&
                (type.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
                 type is "System.Attribute" or "System.Type"))
            {
                return true;
            }

            if ((type.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
                 type is "System.Type" or "System.AppDomain") &&
                (name is
                        "Invoke" or
                        "InvokeMember" or
                        "CreateDelegate" or
                        "GetValue" or
                        "SetValue" ||
                 name.StartsWith("CreateInstance", StringComparison.Ordinal)))
            {
                return true;
            }

            return member is
                    "System.Type::GetType" or
                    "System.Reflection.Assembly::GetType" or
                    "System.Delegate::DynamicInvoke" or
                    "System.Delegate::CreateDelegate" or
                    "System.Runtime.CompilerServices.RuntimeHelpers::GetUninitializedObject" or
                    "System.Runtime.InteropServices.Marshal::PtrToStructure" or
                    "System.ComponentModel.PropertyDescriptor::GetValue" or
                    "System.ComponentModel.PropertyDescriptor::SetValue" ||
                name == "Compile" &&
                    type.StartsWith("System.Linq.Expressions.", StringComparison.Ordinal);
        }

        private interface IRepeatedRoot
        {
            void Root();
        }

        private interface IRepeatedLeft : IRepeatedRoot
        {
            void Left();
        }

        private interface IRepeatedRight : IRepeatedRoot
        {
            void Right();
        }

        private interface IValueCallback
        {
            void Invoke();
        }

        private sealed class CallbackStream : MemoryStream, IRepeatedLeft, IRepeatedRight
        {
            public void Left()
            {
            }

            public void Right()
            {
            }

            public void Root()
            {
                ScannerWitness.InternalInterface();
            }

            protected override void Dispose(bool disposing)
            {
                ScannerWitness.Callback();
                base.Dispose(disposing);
            }
        }

        private sealed class ComparerFixture : IComparer<CallbackStream>
        {
            public int Compare(CallbackStream x, CallbackStream y)
            {
                ScannerWitness.ExternalInterface();
                return 0;
            }
        }

        private abstract class StreamBase : MemoryStream
        {
            protected override void Dispose(bool disposing) => base.Dispose(disposing);
        }

        private sealed class DerivedStream : StreamBase
        {
            protected override void Dispose(bool disposing)
            {
                ScannerWitness.InheritedSlot();
                base.Dispose(disposing);
            }
        }

        private sealed class CovariantWriter : StringWriter
        {
            public override System.Text.UTF8Encoding Encoding
            {
                get
                {
                    ScannerWitness.CovariantSlot();
                    return new System.Text.UTF8Encoding(false);
                }
            }
        }

        private abstract class OverloadBase : MemoryStream
        {
            public virtual int Read(Memory<byte> buffer) => 0;

            public override int Read(Span<byte> buffer) => base.Read(buffer);
        }

        private sealed class OverloadLeaf : OverloadBase
        {
            public override int Read(Span<byte> buffer)
            {
                ScannerWitness.Overload();
                return base.Read(buffer);
            }
        }

        private class CovariantMid : StringWriter
        {
            public override System.Text.UTF8Encoding Encoding => new(false);
        }

        private sealed class CovariantLeaf : CovariantMid
        {
            public override System.Text.UTF8Encoding Encoding
            {
                get
                {
                    ScannerWitness.CovariantParent();
                    return new System.Text.UTF8Encoding(false);
                }
            }
        }

        private sealed class TailEncoding : System.Text.UTF8Encoding
        {
        }

        private sealed class CovariantTail : CovariantMid
        {
            public override TailEncoding Encoding
            {
                get
                {
                    ScannerWitness.CovariantDeclaration();
                    return new TailEncoding();
                }
            }
        }

        private interface IDefaultProgress : IProgress<int>
        {
            void IProgress<int>.Report(int value) => ScannerWitness.DefaultMethod();
        }

        private sealed class DefaultProgress : IDefaultProgress
        {
        }

        private struct ArrayOnlyValue : IComparable
        {
            public int CompareTo(object obj)
            {
                ScannerWitness.ArrayElement();
                return 0;
            }
        }

        private interface IStaticFactory<TSelf>
            where TSelf : IStaticFactory<TSelf>
        {
            static abstract TSelf Create();
        }

        private sealed class StaticMade : IStaticFactory<StaticMade>
        {
            public static StaticMade Create()
            {
                ScannerWitness.StaticVirtual();
                return new StaticMade();
            }
        }

        private interface IStaticDelegateFactory<TSelf>
            where TSelf : IStaticDelegateFactory<TSelf>
        {
            static abstract TSelf CreateForDelegate();
        }

        private sealed class StaticDelegateMade : IStaticDelegateFactory<StaticDelegateMade>
        {
            public static StaticDelegateMade CreateForDelegate()
            {
                ScannerWitness.StaticDelegate();
                return new StaticDelegateMade();
            }
        }

        private interface IOverridable
        {
            void Act();
        }

        private interface IOverridingDefault : IOverridable
        {
            void IOverridable.Act() => ScannerWitness.DefaultOverride();
        }

        private sealed class DefaultOverrider : IOverridingDefault
        {
        }

        private readonly struct CallbackValue : IValueCallback
        {
            public void Invoke() => ScannerWitness.ValueType();
        }

        private readonly struct GenericOnlyValue : IValueCallback
        {
            public void Invoke() => ScannerWitness.GenericType();
        }

        private sealed class SeededFixture
        {
            internal SeededFixture() => ScannerWitness.Seed();
        }

        private static class ScannerFixture
        {
            internal static void Run()
            {
                _ = new CallbackStream();
                _ = new ComparerFixture();
                _ = new DerivedStream();
                _ = new CovariantWriter();
                _ = new OverloadLeaf();
                _ = new CovariantLeaf();
                _ = new CovariantTail();
                _ = new DefaultProgress();
                Array.Sort((Array)new ArrayOnlyValue[2]);
                _ = Make<StaticMade>();
                _ = MakeLater<StaticDelegateMade>();
                IOverridable overridable = new DefaultOverrider();
                overridable.Act();
                Invoke(new CallbackValue());
                GenericTypeOnly<GenericOnlyValue>();
                _ = ReadLazy(new Lazy<CallbackStream>(() =>
                {
                    ScannerWitness.Lazy();
                    return new CallbackStream();
                }));
                _ = new Lazy<CallbackStream>(NamedFactory);
                _ = NamedFactory();
            }

            internal static void ReadsBeforeConstruction()
            {
                _ = ReadLazy<CallbackStream>(null);
                ConstructsLater();
            }

            internal static void MakeDeferred()
            {
                _ = new Lazy<CallbackStream>(() =>
                {
                    ScannerWitness.Deferred();
                    return new CallbackStream();
                });
            }

            private static void ConstructsLater() => ConstructsLatest();

            private static void ConstructsLatest()
            {
                _ = new Lazy<CallbackStream>(() =>
                {
                    ScannerWitness.LateLazy();
                    return new CallbackStream();
                });
            }

            private static void Invoke<T>(T value) where T : struct, IValueCallback => value.Invoke();

            private static T Make<T>() where T : IStaticFactory<T> => T.Create();

            private static Func<T> MakeLater<T>() where T : IStaticDelegateFactory<T> => T.CreateForDelegate;

            private static void GenericTypeOnly<T>() where T : struct, IValueCallback
            {
                GC.KeepAlive(typeof(T));
            }

            private static T ReadLazy<T>(Lazy<T> value) => value.Value;

            private static CallbackStream NamedFactory()
            {
                ScannerWitness.NamedFactory();
                return new CallbackStream();
            }
        }

        private static class ScannerWitness
        {
            internal static void Callback()
            {
            }

            internal static void ArrayElement()
            {
            }

            internal static void CovariantDeclaration()
            {
            }

            internal static void CovariantParent()
            {
            }

            internal static void CovariantSlot()
            {
            }

            internal static void DefaultMethod()
            {
            }

            internal static void DefaultOverride()
            {
            }

            internal static void Deferred()
            {
            }

            internal static void ExternalInterface()
            {
            }

            internal static void GenericType()
            {
            }

            internal static void InheritedSlot()
            {
            }

            internal static void Lazy()
            {
            }

            internal static void LateLazy()
            {
            }

            internal static void InternalInterface()
            {
            }

            internal static void NamedFactory()
            {
            }

            internal static void Overload()
            {
            }

            internal static void Seed()
            {
            }

            internal static void StaticVirtual()
            {
            }

            internal static void StaticDelegate()
            {
            }

            internal static void ValueType()
            {
            }
        }
    }
}
