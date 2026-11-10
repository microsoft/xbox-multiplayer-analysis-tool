// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The recording guarantee (L2 5.5; V2, W5, Y2): the recorder knows the last send that it was given (LastItem) before
    // it adds its row, and whether that very send's row is in the Replays tab (LastItemRecorded); a run that the tab could
    // not take is worded from that send, never from an exception's message, which only its type reaches the log. And how
    // the tab's row of a raw send is noted: by what XMAT wrote of the request, never by the outcome. Parsed requests go to
    // loopback servers that the tests own; raw sends go to a scripted connection (SeamConnection), and every other route
    // that their runs could take ends at RefusingReplayTransport, refused at once in memory (L2-ARCH-1). Each test records
    // into its own application model. Which tab a composer records into: Edit and resend's composer records into its
    // view's (W1), and Compose reaches only the application's composer, by one registration and one call (AA-2, AA-3),
    // read from the product's XAML and IL since no test builds MainWindow.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerRawRecordingTests
    {
        private const string Canary = "canary-6f1d0c2e";

        // The target origin of the raw sends here: a documentation address that no route reaches, since a raw send goes to a
        // scripted connection (SeamConnection) and every other route to RefusingReplayTransport.
        internal const string RawTarget = "http://192.0.2.10:8080";

        private const string MainWindowXaml = "Windows/MainWindow.xaml";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        // The ways that code can register a command's handler or change a CommandBindings collection, which nothing in the
        // product uses (AA-2).
        private static readonly string[] RegistrationMembers =
        {
            "System.Windows.Input.CommandBinding::.ctor",
            "System.Windows.Input.CommandManager::RegisterClassCommandBinding",
            "System.Windows.Input.CommandManager::AddExecutedHandler",
            "System.Windows.Input.CommandManager::AddPreviewExecutedHandler",
            "System.Windows.Input.CommandManager::ExecutedEvent",
            "System.Windows.Input.CommandManager::PreviewExecutedEvent",
            "System.Windows.Input.CommandBindingCollection::Add",
            "System.Windows.Input.CommandBindingCollection::AddRange",
            "System.Windows.Input.CommandBindingCollection::Insert",
            "System.Windows.Input.CommandBindingCollection::set_Item",
        };

        // A CommandBinding's handlers and command, which only the Connect methods that the XAML compiler generates set
        // (AA-2).
        private static readonly string[] ConnectMembers =
        {
            "System.Windows.Input.CommandBinding::add_Executed",
            "System.Windows.Input.CommandBinding::add_PreviewExecuted",
            "System.Windows.Input.CommandBinding::set_Command",
        };

        // What only the Connect methods that the XAML compiler generates may use of the command system (L5-1): any member of
        // these types, and these types themselves.
        private static readonly string[] CommandSystemTypes =
        {
            "System.Windows.Input.CommandBinding",
            "System.Windows.Input.CommandBindingCollection",
            "System.Windows.Input.ExecutedRoutedEventHandler",
        };

        // And these: an element's CommandBindings collection, through which any interface or base type of the collection
        // could change it unnamed; CommandManager as a type, which reflection starts from; and the routed events that
        // EventManager lists, Executed and PreviewExecuted among them (L5-1).
        private static readonly string[] CommandSystemOperands =
        {
            "System.Windows.UIElement::get_CommandBindings",
            "System.Windows.ContentElement::get_CommandBindings",
            "System.Windows.UIElement3D::get_CommandBindings",
            "System.Windows.EventManager::GetRoutedEvents",
            "System.Windows.EventManager::GetRoutedEventsForOwner",
            "type System.Windows.Input.CommandManager",
        };

        // The names that reflection or dynamic code would give as strings to reach the command system (L5-1).
        private static readonly string[] CommandSystemNames =
        {
            "CommandBinding",
            "CommandManager",
            "ExecutedEvent",
            "ExecutedHandler",
            "ExecutedRoutedEvent",
            "RoutedEvents",
        };

        // Reflection's execution families (L5-1, L6-2), classified to deny by default: what invokes, creates, reads or writes a
        // member that the IL does not name, loads XAML or compiles or runs code at run time, binds dynamically, or reads
        // members by path or descriptor. Every member of these types counts, whatever new members they get:
        private static readonly string[] ReflectionExecutionTypes =
        {
            "System.Activator",
            "System.Reflection.MethodInvoker",
            "System.Reflection.ConstructorInvoker",
            "System.RuntimeMethodHandle",
            "System.Windows.Markup.XamlReader",
            "System.ComponentModel.TypeDescriptor",
            "System.ComponentModel.MemberDescriptor",
            "System.ComponentModel.PropertyDescriptor",
            "System.ComponentModel.EventDescriptor",
            "System.ComponentModel.PropertyDescriptorCollection",
            "System.ComponentModel.DependencyPropertyDescriptor",
            "System.Windows.PropertyPath",
            "System.Windows.Data.Binding",
            "System.Windows.Data.BindingBase",
            "System.Windows.Data.MultiBinding",
            "System.Windows.Data.PriorityBinding",
            "System.Windows.Data.BindingOperations",
            "System.Windows.Data.ObjectDataProvider",
        };

        // So does every member of the types that stand for a member, but those that only describe it
        // (ReflectionMetadataMembers): Invoke, GetValue, SetValue, GetMethod, MethodHandle, CreateDelegate and AddEventHandler
        // among them.
        private static readonly string[] ReflectionMemberTypes =
        {
            "System.Reflection.MethodBase",
            "System.Reflection.MethodInfo",
            "System.Reflection.ConstructorInfo",
            "System.Reflection.PropertyInfo",
            "System.Reflection.FieldInfo",
            "System.Reflection.EventInfo",
        };

        private static readonly string[] ReflectionMetadataMembers =
        {
            "get_Name",
            "get_DeclaringType",
            "get_ReflectedType",
            "get_MemberType",
            "get_ReturnType",
            "get_PropertyType",
            "get_FieldType",
            "get_EventHandlerType",
            "get_Attributes",
            "get_IsStatic",
            "get_IsPublic",
            "get_IsPrivate",
            "get_IsSpecialName",
            "get_CanRead",
            "get_CanWrite",
            "get_MetadataToken",
            "get_Module",
            "GetParameters",
            "GetIndexParameters",
            "GetCustomAttributesData",
            "IsDefined",
            "Equals",
            "GetHashCode",
            "ToString",
            "op_Equality",
            "op_Inequality",
        };

        // And these members of types that are otherwise ordinary: delegates and instances made from reflection, a function
        // pointer made a delegate, and the loaders of XAML and of what it navigates to; and a method's attribute that makes it
        // an accessor of what it names (UnsafeAccessor), also on an extern method without IL.
        private static readonly string[] ReflectionExecutionMembers =
        {
            "System.Delegate::CreateDelegate",
            "System.Delegate::DynamicInvoke",
            "System.Type::InvokeMember",
            "System.Reflection.Assembly::CreateInstance",
            "System.AppDomain::CreateInstance",
            "System.AppDomain::CreateInstanceAndUnwrap",
            "System.AppDomain::CreateInstanceFrom",
            "System.AppDomain::CreateInstanceFromAndUnwrap",
            "System.Runtime.InteropServices.Marshal::GetDelegateForFunctionPointer",
            "System.Windows.Application::LoadComponent",
            "System.Windows.Application::set_StartupUri",
            "System.Windows.ResourceDictionary::set_Source",
            "System.Windows.Controls.Frame::set_Source",
            "System.Windows.Controls.Frame::Navigate",
            "System.Windows.Navigation.NavigationService::Navigate",
            "System.Windows.Navigation.NavigationWindow::set_Source",
            "System.Windows.Navigation.NavigationWindow::Navigate",
            "System.Windows.FrameworkElement::SetBinding",
            "System.Windows.FrameworkContentElement::SetBinding",
            "attribute System.Runtime.CompilerServices.UnsafeAccessorAttribute",
            "attribute System.Runtime.CompilerServices.UnsafeAccessorTypeAttribute",
        };

        // And every member of every type whose full name starts with one of these: code generation and compilation,
        // expression trees, dynamic binding (CallSite, CallSite<T> and CallSiteBinder included), XAML services and C#
        // scripting.
        private static readonly string[] ReflectionExecutionPrefixes =
        {
            "System.Reflection.Emit.",
            "System.Linq.Expressions.",
            "Microsoft.CSharp.RuntimeBinder.",
            "System.Dynamic.",
            "System.Runtime.CompilerServices.CallSite",
            "System.Xaml.",
            "Microsoft.CodeAnalysis.Scripting.",
            "Microsoft.CodeAnalysis.CSharp.Scripting.",
        };

        // And, on every type, what makes an attribute's object, which runs the attribute's constructor and the setters of its
        // named arguments (L2F-2): GetCustomAttribute and GetCustomAttributes, as MemberInfo, Assembly, Module,
        // ParameterInfo and ICustomAttributeProvider declare them, Attribute's statics and CustomAttributeExtensions'
        // extensions, generic or not. What reads only the attributes' metadata makes no object: GetCustomAttributesData and
        // CustomAttributes, CustomAttributeData's own GetCustomAttributes, which give CustomAttributeData, and IsDefined,
        // which filters the metadata's records by the attributes' types, as GetCustomAttributesData resolves them (in
        // dotnet/runtime, RuntimeCustomAttribute.IsCustomAttributeDefined returns at the first record that
        // FilterCustomAttributeRecord accepts, while GetCustomAttributes' AddCustomAttributes creates each attribute).
        private static readonly string[] AttributeObjectMembers = { "GetCustomAttribute", "GetCustomAttributes" };

        // The product's types whose own code does that, each reviewed (L5-1, L6-2, L2F-2): the GDK's XTF console clients,
        // which XMAT loads, creates and calls by reflection (GDKXHelper, XboxClient); the analysis engine, which creates its
        // rules from the types that its configuration names and reads its static properties by name (CaptureAnalysisEngine);
        // the theme manager, which loads only the product's own theme dictionaries, whose XAML the scans read
        // (XMAT.ThemeManager); the script host, which compiles and runs the user's scripts (XMAT.Scripting.ScriptHost); and
        // the script editor's type list and the scripts' models, which make the Description, Display and DefaultValue
        // attributes of the script API's own types, properties and events to show their names, descriptions and default
        // scripts (XMAT.Scripting.ScriptTypeCollection, XMAT.Scripting.ScriptModel): those are the System.ComponentModel
        // attributes, which keep their text, since the product declares no attribute that derives from them. None of them
        // reaches a command.
        private static readonly HashSet<string> ReviewedReflectionTypes = new(StringComparer.Ordinal)
        {
            "GDKXHelper",
            "XboxClient.XboxClientConnection",
            "CaptureAnalysisEngine.TraceAnalyzer",
            "CaptureAnalysisEngine.Utils",
            "XMAT.ThemeManager",
            "XMAT.Scripting.ScriptHost",
            "XMAT.Scripting.ScriptTypeCollection",
            "XMAT.Scripting.ScriptModel",
        };

        // This class's methods as OperandUse names their users, for its reflection controls (ReflectionControlAccessor,
        // ReflectionControlInvoker, ReflectionControlAttributes, ReflectionControlAttributeData).
        private static readonly string ReflectionControls = typeof(ComposerRawRecordingTests).FullName + "::";

        private readonly CaptureUiApplicationFixture _application;

        public ComposerRawRecordingTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerRawRecordingTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // Parsed mode takes the same path: a send that the tab could not take shows how it went, from the recorder's last
        // item, then that it is not recorded, instead of saying that it could not be sent. The log line names the
        // exception's type only, and the exception's message reaches neither the status nor the log.
        [Fact]
        public Task ASendThatTheTabCouldNotTake_IsWordedFromLastItem_AndLogsOnlyTheExceptionsType() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("reply"));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model, new RefusingTable(1)));
            window.LogSendStopped = logged.Add;
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/x";
                await window.SendAsync();

                string status = Status(window);
                Assert.Single(server.Requests);
                Assert.Empty(Replays(model).ProxyConnections);
                Assert.StartsWith("HTTP 200 | ", status);
                Assert.EndsWith(" ms | 5 bytes " + NotRecorded(), status);
                Assert.Equal(new[] { "Composer send stopped: " + typeof(RowRefusedException).FullName }, logged);
                Assert.False(status.Contains(Canary, StringComparison.Ordinal), "status");
                Assert.False(logged[0].Contains(Canary, StringComparison.Ordinal), "log line");
                Assert.True(model.CanModifyCaptureData);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // W5: when the table refuses the second request's row, the status speaks of that request, whose row is not in the
        // tab, although the first request's row is.
        [Fact]
        public Task ASequenceWhoseSecondRowTheTableRefuses_SaysThatThatSendIsNotRecorded() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(Numbered);
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model, new RefusingTable(2)));
            window.ChooseSequence = () => "1-2";
            window.LogSendStopped = logged.Add;
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/n/#";
                await window.SendAsync();

                string status = Status(window);
                Assert.Equal(new[] { "/n/1", "/n/2" }, server.Requests.Select(request => request.Target));
                Assert.Equal("/n/1", Assert.Single(Replays(model).ProxyConnections).Path);
                Assert.StartsWith("HTTP 201 | ", status);
                Assert.EndsWith(" ms | 6 bytes " + NotRecorded(), status);
                Assert.Single(logged);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // W5: when a handler of the tab's rows fails once the second request's row is in, the status speaks of that
        // request, whose row is in the tab, with or without the bodies that did not fit.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task ASequenceWhoseSecondRowFailsOnceItIsIn_SaysThatThatSendIsRecorded(bool tight) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(Numbered);
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(
                client, NoQuestion, Recording(model, table: null, tight ? 4 : ReplayRecorder.DefaultBodyBudgetBytes));
            window.ChooseSequence = () => "1-2";
            window.LogSendStopped = logged.Add;
            WebServiceDeviceCaptureController replays = Replays(model);
            NotifyCollectionChangedEventHandler failing = (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add &&
                    e.NewItems.OfType<ProxyConnectionModel>().Any(row => row.Path == "/n/2"))
                {
                    throw new RowRefusedException();
                }
            };
            replays.ProxyConnections.CollectionChanged += failing;
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/n/#";
                await window.SendAsync();

                string status = Status(window);
                Assert.Equal(new[] { "/n/1", "/n/2" }, replays.ProxyConnections.Select(row => row.Path));
                Assert.StartsWith("HTTP 201 | ", status);
                Assert.EndsWith(
                    " ms | 6 bytes " + (tight
                        ? Words("COMPOSER_RECORDED_WITHOUT_BODIES", CaptureAppModel.ReplayContextName, 0L)
                        : Words("COMPOSER_RECORDED", CaptureAppModel.ReplayContextName)),
                    status);
                Assert.Single(logged);
            }
            finally
            {
                replays.ProxyConnections.CollectionChanged -= failing;
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Closing the composer mid-send (design 8.1, X3): the scripted stream takes every write and holds the read, and
        // closing the window cancels the run. The send still gets exactly one row once its result reaches the recorder, as a
        // request that XMAT wrote whole, with the cancellation's text in its note; and the capture-data operation ends with
        // the run, not with the window: it still holds while the send's result waits, after the window has closed.
        [Fact]
        public Task ClosingMidSend_StillRecordsTheSend_AndTheOperationEndsWithTheRun() => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.AfterWrite = _ => written.TrySetResult();
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, _ =>
            {
                ended.TrySetResult();
                return release.Task;
            });
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model, table: null));
            ReplayRecorder recorder = null;
            window.RunEnded = run => recorder = run;
            bool closed = false;
            try
            {
                await EnterRawModeAsync(window, RawTarget);
                Task sending = window.SendAsync();
                await written.Task.WaitAsync(Patience);
                await connection.ReadWaiting.Task.WaitAsync(Patience);
                Assert.False(model.CanModifyCaptureData);

                window.Close();
                closed = true;
                await ended.Task.WaitAsync(Patience);
                Assert.False(model.CanModifyCaptureData);
                Assert.Empty(Replays(model).ProxyConnections);
                release.SetResult();
                await sending.WaitAsync(Patience);

                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                Assert.Equal((true, RawWriteState.Complete), (row.SentRaw, row.RawRequestWrite));
                string sentence = Words("COMPOSER_RAW_STATE_COMPLETE") + " " + Words("COMPOSER_RAW_NO_RECEIPT");
                Assert.Equal(
                    string.Join(
                        "\n", Words("REPLAY_NOTE_RAW", sentence), RawRequest.Message(RawCodes.SendCancelled), Version(false)),
                    row.ReplayNote);
                Assert.Same(row, recorder.LastRecorded);
                Assert.True(model.CanModifyCaptureData);
            }
            finally
            {
                release.TrySetResult();
                if (!closed)
                {
                    window.Close();
                }

                await RemoveAllAsync(model);
            }
        });

        // Cancel request in each phase of a raw send that began (design 8.1, V6), through the scripted connection: during
        // name resolution, the TCP connect or the TLS handshake, XMAT wrote nothing of the request; while a write call is in
        // flight, the first or a later one (each hands at most 16 KiB), XMAT cannot confirm the bytes of that call; between
        // two write calls, it stopped after the first one's, here as CancelSend runs on the UI thread once the first call
        // has completed. Each send gets exactly one row, and its write state, its note, the status's write sentence and the
        // recorder's last item say so, with what the completed write calls handed over. A cancel during the read is
        // ComposerRawSendTests.CancelDuringTheRead_KeepsTheRow_AndSaysWhatWasWritten.
        [Theory]
        [InlineData("lookup")]
        [InlineData("connect")]
        [InlineData("tls")]
        [InlineData("first-write")]
        [InlineData("later-write")]
        [InlineData("between-writes")]
        public Task CancelInEachPhase_KeepsOneRow_SayingWhatThePhaseWrote(string phase) => _application.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var connection = new SeamConnection();
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, seams: setup => setup with
            {
                Resolver = async (_, token) =>
                {
                    reached.TrySetResult();
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                    return Array.Empty<System.Net.IPAddress>();
                },
                Connector = async (_, token) =>
                {
                    if (phase == "connect")
                    {
                        reached.TrySetResult();
                        await Task.Delay(System.Threading.Timeout.Infinite, token);
                    }

                    return connection;
                },
            });
            var model = new CaptureAppModel(dispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model, table: null));
            ReplayRecorder recorder = null;
            window.RunEnded = run => recorder = run;
            switch (phase)
            {
                case "first-write":
                    connection.WriteGate = _ =>
                    {
                        reached.TrySetResult();
                        return new TaskCompletionSource().Task;
                    };
                    break;
                case "later-write":
                    connection.WriteGate = index =>
                    {
                        if (index == 0)
                        {
                            return Task.CompletedTask;
                        }

                        reached.TrySetResult();
                        return new TaskCompletionSource().Task;
                    };
                    break;
                case "between-writes":
                    connection.AfterWrite = index =>
                    {
                        if (index == 0)
                        {
                            dispatcher.Invoke(window.CancelSend);
                        }
                    };
                    break;
            }

            try
            {
                string origin = phase switch
                {
                    "lookup" => "http://example.test:8080",
                    "tls" => "https://192.0.2.10:8443",
                    _ => RawTarget,
                };
                await EnterRawModeAsync(
                    window, origin, phase is "later-write" or "between-writes" ? new string('x', 40_000) : "");

                Task sending = window.SendAsync();
                if (phase != "between-writes")
                {
                    await (phase == "tls" ? connection.ReadWaiting.Task : reached.Task).WaitAsync(Patience);
                    window.CancelSend();
                }

                await sending.WaitAsync(Patience);

                (string write, long written) = phase switch
                {
                    "first-write" => (RawWriteState.Uncertain, 0L),
                    "later-write" => (RawWriteState.Uncertain, 16_384L),
                    "between-writes" => (RawWriteState.Partial, 16_384L),
                    _ => (RawWriteState.None, 0L),
                };
                string sentence = write switch
                {
                    RawWriteState.None => Words("COMPOSER_RAW_STATE_NONE"),
                    RawWriteState.Partial => Words("COMPOSER_RAW_STATE_PARTIAL") + " " + Words("COMPOSER_RAW_HANDED", written),
                    _ => Words("COMPOSER_RAW_STATE_UNCERTAIN") + " " + Words("COMPOSER_RAW_HANDED", written),
                };
                Assert.Equal(
                    Words("COMPOSER_RAW_SEND_CANCELLED") + " " + sentence + " " +
                        Words("COMPOSER_RECORDED", CaptureAppModel.ReplayContextName),
                    Status(window));
                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                Assert.Equal((true, write), (row.SentRaw, row.RawRequestWrite));
                Assert.Equal(
                    string.Join(
                        "\n",
                        Words("REPLAY_NOTE_RAW_ATTEMPTED", sentence),
                        RawRequest.Message(RawCodes.SendCancelled),
                        Version(false)),
                    row.ReplayNote);
                ReplayItemResult last = recorder.LastItem;
                Assert.Equal(
                    (true, ReplayOutcome.Failed, RawCodes.SendCancelled, write, (long?)written),
                    (last.Raw, last.Outcome, last.Code, last.RequestWrite, last.RequestBytesWritten));
                Assert.True(recorder.LastItemRecorded);
                Assert.Same(row, recorder.LastRecorded);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A raw send that the Replays tab fails (design 8.1, W5): its table refuses the row with an exception that carries a
        // canary, before the row goes in, or a handler of the tab's rows throws once it is in. The send ended when the
        // connection did, after every byte was written. The status is that send's, from the recorder's last item, its
        // outcome and write sentence, then whether that very send is in the tab; the log line names the exception's type
        // only; and the canary is in no status, note or log line.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task ARawSendThatTheTabFails_IsWordedFromLastItem_AndRecordedOnlyOnceItsRowIsIn(bool rowIn) =>
            _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            connection.AfterWrite = _ => connection.End();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            var table = new RefusingTable(rowIn ? 0 : 1);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model, table));
            window.LogSendStopped = logged.Add;
            ReplayRecorder recorder = null;
            window.RunEnded = run => recorder = run;
            WebServiceDeviceCaptureController replays = Replays(model);
            NotifyCollectionChangedEventHandler failing = (_, e) =>
            {
                if (rowIn && e.Action == NotifyCollectionChangedAction.Add)
                {
                    throw new RowRefusedException();
                }
            };
            replays.ProxyConnections.CollectionChanged += failing;
            try
            {
                await EnterRawModeAsync(window, RawTarget);

                await window.SendAsync();

                string status = Status(window);
                Assert.Equal(
                    Words("COMPOSER_ERROR", Words("COMPOSER_RAW_SEND_FAILED")) + " " + Words("COMPOSER_RAW_STATE_COMPLETE") +
                        " " + Words("COMPOSER_RAW_NO_RECEIPT") + " " +
                        (rowIn ? Words("COMPOSER_RECORDED", CaptureAppModel.ReplayContextName) : NotRecorded()),
                    status);
                Assert.Equal(new[] { "Composer send stopped: " + typeof(RowRefusedException).FullName }, logged);
                ReplayItemResult last = recorder.LastItem;
                Assert.Equal(
                    (true, ReplayOutcome.Failed, ReplaySender.SendFailedCode, RawWriteState.Complete),
                    (last.Raw, last.Outcome, last.Code, last.RequestWrite));
                Assert.Equal(rowIn, recorder.LastItemRecorded);
                Assert.Equal(1, table.Offered);
                if (rowIn)
                {
                    ProxyConnectionModel row = Assert.Single(replays.ProxyConnections);
                    Assert.Equal((true, RawWriteState.Complete), (row.SentRaw, row.RawRequestWrite));
                    Assert.False(row.ReplayNote.Contains(Canary, StringComparison.Ordinal), "note");
                }
                else
                {
                    Assert.Empty(replays.ProxyConnections);
                }

                Assert.False(status.Contains(Canary, StringComparison.Ordinal), "status");
                Assert.False(logged[0].Contains(Canary, StringComparison.Ordinal), "log line");
                Assert.True(model.CanModifyCaptureData);
            }
            finally
            {
                replays.ProxyConnections.CollectionChanged -= failing;
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Y2: closing the composer while the read is held cancels the run, and the table then refuses the send's row with an
        // exception that carries a canary. No row is added to the tab or its table; the recorder's last item holds the send,
        // which XMAT wrote whole, and it is not recorded; the log gets exactly one line, the exception's type, though the
        // window is closed; the closed window's status stays what it showed when it closed, never the send's or that it is
        // not recorded; and the canary is nowhere.
        [Fact]
        public Task ClosingWhileTheTabRefusesTheRow_LogsItOnce_AndChangesNothingShown() => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.AfterWrite = _ => written.TrySetResult();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            var table = new RefusingTable(1);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model, table));
            window.LogSendStopped = logged.Add;
            ReplayRecorder recorder = null;
            window.RunEnded = run => recorder = run;
            bool closed = false;
            try
            {
                await EnterRawModeAsync(window, RawTarget);
                Task sending = window.SendAsync();
                await written.Task.WaitAsync(Patience);
                await connection.ReadWaiting.Task.WaitAsync(Patience);
                string shown = Status(window);

                window.Close();
                closed = true;
                await sending.WaitAsync(Patience);

                Assert.Equal(Words("COMPOSER_SENDING"), shown);
                Assert.Equal(shown, Status(window));
                Assert.Empty(Replays(model).ProxyConnections);
                Assert.Equal(1, table.Offered);
                Assert.Equal(new[] { "Composer send stopped: " + typeof(RowRefusedException).FullName }, logged);
                Assert.False(logged[0].Contains(Canary, StringComparison.Ordinal), "log line");
                Assert.Equal(
                    (true, RawCodes.SendCancelled, RawWriteState.Complete),
                    (recorder.LastItem.Raw, recorder.LastItem.Code, recorder.LastItem.RequestWrite));
                Assert.False(recorder.LastItemRecorded);
                Assert.True(model.CanModifyCaptureData);
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }

                await RemoveAllAsync(model);
            }
        });

        // BeginShutdown (design 8.1, W5), on a model of the test's own, since it is terminal. While the read is held, it
        // cancels the run, whose send then never waits for the UI thread (OnUiThreadUntilShutdown): the composer's dispatch
        // to it is not called again. When it comes while a send's step on the UI thread is queued, that step adds no row.
        // Either way no row goes into the tab or its table, the send is counted as not recorded, the recorder's last item
        // holds it, and the window, still open, says so.
        [Theory]
        [InlineData("read")]
        [InlineData("queued")]
        public Task BeginShutdown_AddsNoRow_AndNoLaterSendWaitsForTheUiThread(string when) => _application.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var connection = new SeamConnection();
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.AfterWrite = _ =>
            {
                if (when == "queued")
                {
                    connection.Respond("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
                }

                written.TrySetResult();
            };
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection);
            var model = new CaptureAppModel(dispatcher);
            var table = new RefusingTable(0);
            int calledOnceShuttingDown = 0;
            var target = new ReplayRecordingTarget(
                model,
                action =>
                {
                    if (model.IsShuttingDown)
                    {
                        Interlocked.Increment(ref calledOnceShuttingDown);
                    }

                    dispatcher.Invoke(() =>
                    {
                        if (when == "queued")
                        {
                            model.BeginShutdown();
                        }

                        action();
                    });
                },
                _ => table);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, target);
            ReplayRecorder recorder = null;
            window.RunEnded = run => recorder = run;
            try
            {
                await EnterRawModeAsync(window, RawTarget);
                Task sending = window.SendAsync();
                await written.Task.WaitAsync(Patience);
                if (when == "read")
                {
                    await connection.ReadWaiting.Task.WaitAsync(Patience);
                    model.BeginShutdown();
                }

                await sending.WaitAsync(Patience);

                Assert.True(model.IsShuttingDown);
                Assert.Equal(0, calledOnceShuttingDown);
                Assert.Empty(Replays(model).ProxyConnections);
                Assert.Equal(0, table.Offered);
                Assert.Equal(1, recorder.NotRecorded);
                Assert.False(recorder.LastItemRecorded);
                Assert.Equal(
                    (true, RawWriteState.Complete, when == "read" ? RawCodes.SendCancelled : null),
                    (recorder.LastItem.Raw, recorder.LastItem.RequestWrite, recorder.LastItem.Code));
                string status = Status(window);
                if (when == "read")
                {
                    Assert.Equal(
                        Words("COMPOSER_RAW_SEND_CANCELLED") + " " + Words("COMPOSER_RAW_STATE_COMPLETE") + " " +
                            Words("COMPOSER_RAW_NO_RECEIPT") + " " + NotRecorded(),
                        status);
                }
                else
                {
                    Assert.StartsWith("HTTP 200 | ", status, StringComparison.Ordinal);
                    Assert.EndsWith(" ms | 2 bytes " + NotRecorded(), status, StringComparison.Ordinal);
                }
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A raw send's row is noted by what XMAT wrote of its request, never by its outcome: a raw request once XMAT handed
        // every byte to the connection, which proves no receipt, and an attempted request otherwise, with the bytes that the
        // write calls handed it; then why it got no complete response, in its code's words.
        [Theory]
        [InlineData(RawWriteState.Complete, "Completed", null)]
        [InlineData(RawWriteState.Partial, "Completed", null)]
        [InlineData(RawWriteState.None, "Failed", ReplaySender.SendFailedCode)]
        [InlineData(RawWriteState.Complete, "TimedOut", ReplaySender.TimeoutCode)]
        [InlineData(RawWriteState.Complete, "Failed", RawCodes.SendCancelled)]
        public void ARawSendsRow_IsNotedByItsWriteState_NeverByItsOutcome(string write, string outcome, string code)
        {
            var ended = Enum.Parse<ReplayOutcome>(outcome);
            bool responded = ended == ReplayOutcome.Completed;
            long written = write == RawWriteState.None ? 0 : 16_384;
            ReplayItemResult sent = RawSent(ended, code, write, written, responded ? RawResponseReader.ContentLengthFraming : null);

            ProxyConnectionModel row = ReplayRecorder.ToRow(1, ReplayProvenance.Composed, RawExchange(ended, code, responded), sent: sent);

            var expected = new List<string>
            {
                write switch
                {
                    RawWriteState.Complete => Words("REPLAY_NOTE_RAW", Words("COMPOSER_RAW_STATE_COMPLETE") + " " + Words("COMPOSER_RAW_NO_RECEIPT")),
                    RawWriteState.Partial => Words(
                        "REPLAY_NOTE_RAW_ATTEMPTED", Words("COMPOSER_RAW_STATE_PARTIAL") + " " + Words("COMPOSER_RAW_HANDED", 16_384L)),
                    _ => Words("REPLAY_NOTE_RAW_ATTEMPTED", Words("COMPOSER_RAW_STATE_NONE")),
                },
            };
            if (!responded)
            {
                expected.Add(code switch
                {
                    ReplaySender.SendFailedCode => Words("COMPOSER_RAW_SEND_FAILED"),
                    ReplaySender.TimeoutCode => Words("COMPOSER_TIMEOUT"),
                    _ => RawRequest.Message(code),
                });
            }

            expected.Add(Version(responded));
            Assert.Equal(string.Join("\n", expected), row.ReplayNote);
            Assert.Equal((true, write), (row.SentRaw, row.RawRequestWrite));
        }

        // Everything that a raw send's row cannot show, in order: what XMAT wrote, why no complete response came, a response
        // that ended when the server closed the connection, response trailer fields, the bodies that the tab did not keep,
        // then Connect to another endpoint in Raw mode's words, and the HTTP version.
        [Fact]
        public void ARawSendsRow_SaysWhatItCannotShow_InOrder()
        {
            ReplayItemResult sent = RawSent(
                ReplayOutcome.Failed,
                RawCodes.ResponseIncomplete,
                RawWriteState.Uncertain,
                16_384,
                RawResponseReader.CloseFraming,
                ReplayTransportOptions.ConnectToName);
            ReplayExchange exchange = RawExchange(ReplayOutcome.Failed, RawCodes.ResponseIncomplete, true, trailers: 3);

            ProxyConnectionModel row = ReplayRecorder.ToRow(1, ReplayProvenance.Composed, exchange, keepRequestBody: false, sent: sent);

            Assert.Equal(
                string.Join(
                    "\n",
                    Words("REPLAY_NOTE_RAW_ATTEMPTED", Words("COMPOSER_RAW_STATE_UNCERTAIN") + " " + Words("COMPOSER_RAW_HANDED", 16_384L)),
                    RawRequest.Message(RawCodes.ResponseIncomplete),
                    Words("REPLAY_NOTE_RAW_CLOSE_DELIMITED"),
                    Words("REPLAY_NOTE_RAW_TRAILERS", 3),
                    Words("REPLAY_NOTE_REQUEST_BODY", 4L, 256L),
                    Words("REPLAY_NOTE_RAW_CONNECT_TO") + " " + Version(true)),
                row.ReplayNote);
            Assert.Equal((true, RawWriteState.Uncertain), (row.SentRaw, row.RawRequestWrite));
        }

        // Edit and resend (W1) opens a composer that records into the Replays tab of the view that opened it: the view's
        // own recording target, never a target of the composer's own, and never none.
        [Fact]
        public Task EditAndResend_OpensAComposerThatRecordsIntoTheViewsTarget() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var target = new ReplayRecordingTarget(model, action => dispatcher.Invoke(action));
            var capture = new ProxyConnectionModel(
                1, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "GET", "/edit",
                "GET /edit HTTP/1.1\r\nHost: 127.0.0.1:9\r\n", Array.Empty<byte>(), "HTTP/1.1 200 OK\r\n",
                Array.Empty<byte>(), "127.0.0.1");
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), capture);
            view.ReplayTarget = target;
            var messages = new List<string>();
            view.ShowReplayMessage = (text, _) => messages.Add(text);
            RequestComposerWindow[] before = Application.Current.Windows.OfType<RequestComposerWindow>().ToArray();
            try
            {
                WebServiceProxyCommands.EditAndResend.Execute(capture, view);

                RequestComposerWindow composer =
                    Assert.Single(Application.Current.Windows.OfType<RequestComposerWindow>().Except(before));
                Assert.True(composer.HasRecordingTarget);
                Assert.Same(target, composer.RecordingTarget);
                Assert.Empty(messages);
            }
            finally
            {
                foreach (RequestComposerWindow opened in
                    Application.Current.Windows.OfType<RequestComposerWindow>().Except(before).ToList())
                {
                    opened.Close();
                }

                await RemoveAllAsync(model);
            }
        });

        // Compose's registration in XAML (AA-2): every CommandBinding of the product's XAML has its Command resolved by
        // reflection, and exactly one is for AppCommands.ComposeRequest, by reference, so an alias counts too: MainWindow's,
        // whose only other attribute is Executed="ComposeRequest_Executed". The Compose menu item invokes that same command,
        // and no attribute or EventSetter handles CommandManager's Executed or PreviewExecuted. A binding by name (Close)
        // and ScriptEditorView's EventSetter are the scan's controls.
        [Fact]
        public void ComposeRegistration_InXaml_IsMainWindowsOneBinding() => _application.Run(_ =>
        {
            IReadOnlyList<ComposeRegistrationScan.XamlBinding> bindings = ComposeRegistrationScan.CommandBindings();
            IReadOnlyList<ComposeRegistrationScan.XamlEventSetter> setters = ComposeRegistrationScan.EventSetters();

            ComposeRegistrationScan.XamlBinding compose =
                Assert.Single(bindings, binding => ReferenceEquals(binding.Command, AppCommands.ComposeRequest));
            Assert.Equal(MainWindowXaml, compose.Path);
            Assert.Equal(new[] { "Executed=ComposeRequest_Executed" }, compose.OtherAttributes);
            Assert.Same(AppCommands.ComposeRequest, ComposeRegistrationScan.CommandOfElement(MainWindowXaml, "ComposeRequest"));
            Assert.Empty(ComposeRegistrationScan.ExecutedEventNames());
            Assert.DoesNotContain(
                setters,
                setter => setter.Event == CommandManager.ExecutedEvent || setter.Event == CommandManager.PreviewExecutedEvent);
            Assert.Contains(bindings, binding => ReferenceEquals(binding.Command, ApplicationCommands.Close));
            Assert.Contains(setters, setter => setter.Event == Control.MouseDoubleClickEvent);
        });

        // Compose's registration in code (AA-2): nothing in the product assembly, AppCommands' initializer included, makes a
        // CommandBinding, registers a handler with CommandManager, uses its Executed or PreviewExecuted event, or changes a
        // CommandBindings collection; and only the Connect methods that the XAML compiler generates add a CommandBinding's
        // handlers or set its command. MainWindow's generated Connect and ScriptEditorView's generated EventSetter code are
        // the scan's controls.
        [Fact]
        public void ComposeRegistration_InCode_IsOnlyTheXamlCompilersConnect()
        {
            string product = typeof(MainWindow).Assembly.Location;

            IReadOnlyList<ComposeRegistrationScan.MemberUse> uses =
                ComposeRegistrationScan.UsesOf(product, RegistrationMembers.Concat(ConnectMembers).ToArray());

            Assert.DoesNotContain(uses, use => RegistrationMembers.Contains(use.Member) || !use.InGeneratedConnect);
            Assert.Contains(
                new ComposeRegistrationScan.MemberUse(
                    "XMAT.MainWindow::" + ComposeRegistrationScan.ComponentConnect,
                    "System.Windows.Input.CommandBinding::add_Executed",
                    true),
                uses);
            Assert.Contains(
                new ComposeRegistrationScan.MemberUse(
                    "XMAT.ScriptEditorView::System.Windows.Markup.IStyleConnector.Connect",
                    "System.Windows.Controls.Control::MouseDoubleClickEvent",
                    false),
                ComposeRegistrationScan.UsesOf(product, new[] { "System.Windows.Controls.Control::MouseDoubleClickEvent" }));
        }

        // Compose's registration by any other route (AA-2, L5-1, L6-2, L6-3, L2F-2). In the product assembly, only the Connect
        // methods that the XAML compiler generates use the command system (CommandSystemTypes, CommandSystemOperands), so
        // nothing else gets an element's CommandBindings collection to change through IList or any other interface of it, or
        // makes, finds or casts a binding; no string names the command system (CommandSystemNames), as reflection or dynamic
        // code would; and only the reviewed types use reflection's execution families (ReflectionExecutionTypes,
        // ReflectionMemberTypes, ReflectionExecutionMembers, AttributeObjectMembers, ReflectionExecutionPrefixes), which deny
        // by default. In product XAML, each item of a CommandBindings collection is a CommandBinding element, never a markup
        // extension such as StaticResource, a type of the product's own or text; and nothing else mentions CommandBindings,
        // in any case, as a binding's path or another markup extension would to retrieve the collection. The controls:
        // MainWindow's Connect, which casts its targets to CommandBinding; XTF's console client; the attribute objects that
        // the script editor's type list and the scripts' models make; the XAML compiler's own reflection (_CreateDelegate in
        // a method and GeneratedInternalTypeHelper in a type that it generated); this class's unsafe accessor without IL,
        // its method invoker and its attribute objects, seen, and its attributes' metadata, not seen (ReflectionControls),
        // read from the test assembly; and MainWindow's collection, as an item and as the one kind of mention allowed.
        [Fact]
        public void ComposeRegistration_ByAnyOtherRoute_IsNowhereInTheProduct() => _application.Run(_ =>
        {
            string product = typeof(MainWindow).Assembly.Location;

            IReadOnlyList<ComposeRegistrationScan.OperandUse> commands =
                ComposeRegistrationScan.OperandUses(product, IsCommandSystem);
            IReadOnlyList<ComposeRegistrationScan.OperandUse> reflection =
                ComposeRegistrationScan.OperandUses(product, IsReflection);
            IReadOnlyList<ComposeRegistrationScan.OperandUse> controls =
                ComposeRegistrationScan.OperandUses(typeof(ComposerRawRecordingTests).Assembly.Location, IsReflection);
            IReadOnlyList<ComposeRegistrationScan.CommandBindingsItem> items = ComposeRegistrationScan.CommandBindingsItems();
            IReadOnlyList<ComposeRegistrationScan.CommandBindingsMention> mentions =
                ComposeRegistrationScan.CommandBindingsMentions();

            Assert.DoesNotContain(commands, use => !use.InGeneratedConnect);
            Assert.Contains(
                new ComposeRegistrationScan.OperandUse(
                    "XMAT.MainWindow::" + ComposeRegistrationScan.ComponentConnect,
                    "type System.Windows.Input.CommandBinding",
                    true,
                    true),
                commands);
            Assert.Subset(
                ReviewedReflectionTypes,
                reflection
                    .Where(use => !use.InXamlCompilerCode)
                    .Select(use => TypeOf(use.User))
                    .ToHashSet(StringComparer.Ordinal));
            Assert.Contains(
                reflection, use => !use.InXamlCompilerCode && TypeOf(use.User) == "XboxClient.XboxClientConnection");
            Assert.Contains(
                reflection,
                use => use.InXamlCompilerCode && use.User.EndsWith("::_CreateDelegate", StringComparison.Ordinal) &&
                    use.Operand == "System.Delegate::CreateDelegate");
            Assert.Contains(
                reflection,
                use => use.InXamlCompilerCode && TypeOf(use.User) == "XamlGeneratedNamespace.GeneratedInternalTypeHelper");
            Assert.Contains(
                new ComposeRegistrationScan.OperandUse(
                    ReflectionControls + nameof(ReflectionControlAccessor),
                    "attribute System.Runtime.CompilerServices.UnsafeAccessorAttribute",
                    false,
                    false),
                controls);
            Assert.Contains(
                new ComposeRegistrationScan.OperandUse(
                    ReflectionControls + nameof(ReflectionControlInvoker),
                    "System.Reflection.MethodInvoker::Invoke",
                    false,
                    false),
                controls);
            Assert.Contains(
                new ComposeRegistrationScan.OperandUse(
                    "XMAT.Scripting.ScriptTypeCollection::GetClassInfo", "System.Attribute::GetCustomAttribute", false, false),
                reflection);
            Assert.Contains(
                new ComposeRegistrationScan.OperandUse(
                    "XMAT.Scripting.ScriptTypeCollection::GetClassInfo",
                    "System.Reflection.CustomAttributeExtensions::GetCustomAttributes",
                    false,
                    false),
                reflection);
            Assert.Contains(
                new ComposeRegistrationScan.OperandUse(
                    "XMAT.Scripting.ScriptModel::GetEnumAttribute",
                    "System.Reflection.CustomAttributeExtensions::GetCustomAttribute",
                    false,
                    false),
                reflection);
            Assert.Contains(
                controls,
                use => use.User == ReflectionControls + nameof(ReflectionControlAttributes) &&
                    use.Operand.EndsWith("::GetCustomAttributes", StringComparison.Ordinal));
            Assert.DoesNotContain(controls, use => use.User == ReflectionControls + nameof(ReflectionControlAttributeData));
            Assert.DoesNotContain(items, item => item.Type != typeof(CommandBinding));
            Assert.Contains(items, item => item.Path == MainWindowXaml && item.Type == typeof(CommandBinding));
            Assert.DoesNotContain(mentions, mention => mention.Where != "element" || !IsCollectionElement(mention.Text));
            Assert.Contains(mentions, mention => mention.Path == MainWindowXaml && mention.Text == "Window.CommandBindings");
        });

        // Compose's handler (AA-3): ComposeRequest_Executed's whole instruction stream, nop left out, is one call of
        // OpenForApplication with the window itself, whose result it drops, and the compiler generated nothing for it; so
        // null, another window, a helper, an overload or a second composer all fail. A lambda's generated method is the
        // reader's control.
        [Fact]
        public void ComposeRequestExecuted_OnlyOpensTheApplicationsComposer_ForItsWindow()
        {
            Assert.Equal(
                new[]
                {
                    "ldarg.0",
                    "call XMAT.WebServiceCapture.RequestComposerWindow::OpenForApplication(System.Windows.Window):" +
                        "XMAT.WebServiceCapture.RequestComposerWindow",
                    "pop",
                    "ret",
                },
                ComposeRegistrationScan.MethodInstructions(
                    typeof(MainWindow),
                    "ComposeRequest_Executed(System.Object,System.Windows.Input.ExecutedRoutedEventArgs):System.Void"));
            Assert.Contains(
                ComposeRegistrationScan.MethodInstructions(
                    typeof(ComposerRawRecordingTests), "GeneratedControl():System.Func`1[System.Int32]"),
                line => line.StartsWith("generated: ", StringComparison.Ordinal) &&
                    line.Contains("<GeneratedControl>", StringComparison.Ordinal));
        }

        // A method for which the compiler generates a lambda's method: MethodInstructions' control.
        private static Func<int> GeneratedControl() => () => 1;

        // The reflection that the families must see, here in the test assembly, which nothing calls (L6-2): an unsafe accessor,
        // an extern method without IL whose attribute names what it reaches, and a method invoker.
        [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "ToString")]
        private static extern string ReflectionControlAccessor(object value);

        private static object ReflectionControlInvoker() =>
            System.Reflection.MethodInvoker.Create(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)).Invoke(new object());

        // And what they tell apart over attributes (L2F-2): making an attribute's object, which runs its constructor, and
        // reading only the attributes' metadata, which they must not see.
        private static object ReflectionControlAttributes() => typeof(ComposerRawRecordingTests).GetCustomAttributes(false);

        private static object ReflectionControlAttributeData() => (
            typeof(ComposerRawRecordingTests).GetCustomAttributesData(),
            typeof(ComposerRawRecordingTests).IsDefined(typeof(CollectionAttribute), false),
            System.Reflection.CustomAttributeData.GetCustomAttributes(typeof(ComposerRawRecordingTests)));

        // Whether an operand (OperandUse) is of the command system (L5-1).
        private static bool IsCommandSystem(string operand) =>
            CommandSystemOperands.Contains(operand) ||
            CommandSystemTypes.Any(type =>
                operand == "type " + type || operand.StartsWith(type + "::", StringComparison.Ordinal)) ||
            (operand.StartsWith("string ", StringComparison.Ordinal) &&
                CommandSystemNames.Any(name => operand.Contains(name, StringComparison.Ordinal)));

        // Whether an operand (OperandUse) is of one of reflection's execution families (L6-2, L2F-2): any member of an
        // execution type, a member of a type that stands for a member unless it only describes it, an execution member, a
        // member of any type that makes an attribute's object, or any member of a type under an execution prefix.
        private static bool IsReflection(string operand)
        {
            int separator = operand.IndexOf("::", StringComparison.Ordinal);
            string type = separator < 0 ? null : operand.Substring(0, separator);
            return ReflectionExecutionMembers.Contains(operand) ||
                (type != null &&
                    (ReflectionExecutionTypes.Contains(type) ||
                        (ReflectionMemberTypes.Contains(type) &&
                            !ReflectionMetadataMembers.Contains(operand.Substring(separator + 2))) ||
                        (AttributeObjectMembers.Contains(operand.Substring(separator + 2)) &&
                            type != "System.Reflection.CustomAttributeData") ||
                        ReflectionExecutionPrefixes.Any(prefix => type.StartsWith(prefix, StringComparison.Ordinal))));
        }

        // Whether an element's name is a property element that declares a CommandBindings collection, Owner.CommandBindings
        // exactly, the only mention of the collection that product XAML may hold (L6-3).
        private static bool IsCollectionElement(string name)
        {
            int dot = name.IndexOf('.');
            return dot > 0 && name.Substring(dot) == ".CommandBindings";
        }

        // The type of a user method as OperandUse names it, "Type::Method".
        private static string TypeOf(string user) => user.Substring(0, user.IndexOf("::", StringComparison.Ordinal));

        // A request of two numbers: the first answered 200 with 3 bytes, the second 201 with 6.
        private static LoopbackResponse Numbered(LoopbackRequest request) => request.Target == "/n/2"
            ? new LoopbackResponse(201, "Created", Body: "second"u8.ToArray())
            : LoopbackResponse.Ok("one");

        private static string NotRecorded() => Words("COMPOSER_NOT_RECORDED", CaptureAppModel.ReplayContextName);

        // The HTTP version note of a raw send, which asks for exactly HTTP/1.1.
        private static string Version(bool responded) => responded
            ? Words("REPLAY_NOTE_HTTP_VERSION", Words("COMPOSER_HTTP_VERSION_11"), "HTTP/1.1")
            : Words("REPLAY_NOTE_HTTP_VERSION_NO_RESPONSE", Words("COMPOSER_HTTP_VERSION_11"));

        // A raw send as the operation records it.
        private static ReplayItemResult RawSent(
            ReplayOutcome outcome,
            string code,
            string write,
            long written,
            string framing,
            string transport = ReplayTransportOptions.DirectName) =>
            new(1, null, null, 1, outcome, outcome == ReplayOutcome.Completed ? 200 : 0, TimeSpan.Zero, 5, 0, code, null,
                Transport: transport,
                HttpVersion: "1.1",
                ResponseVersion: framing == null ? null : "1.1",
                Raw: true,
                RequestBytesWritten: written,
                RequestWrite: write,
                ResponseFraming: framing);

        // A raw send's exchange: the normalized request, with a 4-byte body, and the response's head when one came.
        private static ReplayExchange RawExchange(ReplayOutcome outcome, string code, bool responded, int trailers = 0) => new()
        {
            Outcome = outcome,
            Code = code,
            Raw = true,
            Request = new ReplayMessage(
                "POST /items HTTP/1.1",
                new[]
                {
                    new KeyValuePair<string, string>("Host", "192.0.2.10:8080"),
                    new KeyValuePair<string, string>("Content-Length", "4"),
                },
                null,
                "body"u8.ToArray()),
            Response = responded ? new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()) : null,
            RequestUri = new Uri("http://192.0.2.10:8080/items"),
            StatusCode = responded ? 200 : 0,
            Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ResponseTrailerFields = trailers,
            ProvenanceOrigin = new Uri("http://192.0.2.10:8080/"),
        };

        private static string Words(string key, params object[] args) => Localization.GetLocalizedString(key, args);

        internal static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        // Records into the model's Replays tab, on this test's UI thread, and into table, keeping at most bodyBudgetBytes of
        // bodies.
        internal static ReplayRecordingTarget Recording(
            CaptureAppModel model, IDataTable table, long bodyBudgetBytes = ReplayRecorder.DefaultBodyBudgetBytes)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(model, action => dispatcher.Invoke(action), _ => table, bodyBudgetBytes);
        }

        private static WebServiceDeviceCaptureController Replays(CaptureAppModel model) =>
            (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;

        // Enters Raw mode as the user does, from a request to origin's /items without headers, which loses nothing: a GET,
        // or a POST of body.
        internal static async Task EnterRawModeAsync(RequestComposerWindow window, string origin, string body = "")
        {
            Find<TextBox>(window, "MethodBox").Text = body.Length == 0 ? "GET" : "POST";
            Find<TextBox>(window, "UrlBox").Text = origin + "/items";
            Find<TextBox>(window, "HeadersBox").Text = "";
            Find<TextBox>(window, "BodyBox").Text = body;
            await window.SwitchModeAsync();
            Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
        }

        // A client over transport whose raw sends connect to connection, through seams when given (RawSenderSetup), and
        // whose raw run awaits ended, when given, once its send has ended and before its result goes to the recorder
        // (ReplayOperation.RawSendEnded). Every other route of its runs ends at transport, refused at once.
        internal static RequestReplayClient Scripted(
            RefusingReplayTransport transport,
            SeamConnection connection,
            Func<int, Task> ended = null,
            Func<RawSenderSetup, RawSenderSetup> seams = null) =>
            transport.Client(
                setup =>
                {
                    RawSenderSetup scripted = setup with { Connector = (_, _) => Task.FromResult<Stream>(connection) };
                    return seams == null ? scripted : seams(scripted);
                },
                operation => operation.RawSendEnded = ended);

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }

        // What a table or a handler of the tab's rows throws when it cannot take a row; its message carries the canary.
        private sealed class RowRefusedException : Exception
        {
            internal RowRefusedException()
                : base(Canary)
            {
            }
        }

        // A table that takes rows until the one numbered refuseAt, counted from 1, which it refuses; 0 refuses none.
        private sealed class RefusingTable(int refuseAt) : IDataTable
        {
            private int _rows;

            // The rows that it was given, the one that it refused included.
            internal int Offered => _rows;

            public event Action<IDataTable, long> RowAdded
            {
                add { }
                remove { }
            }

            public event Action<IDataTable, long> RowUpdated
            {
                add { }
                remove { }
            }

            public event Action<IDataTable, long> RowsRemoved
            {
                add { }
                remove { }
            }

            public string Name => "Refusing";

            public IDataset Dataset { get; } = new EmptyDataset();

            public IDataset Subset(params string[] fieldNames) => null;

            public long AddRow(params object[] fieldValueArgs) =>
                ++_rows == refuseAt ? throw new RowRefusedException() : _rows;

            public void UpdateRow(long rowId, params IFieldValue[] fieldNamesAndValues)
            {
            }

            public void RemoveRowsWhere(params IFieldValue[] fieldNamesAndValues)
            {
            }
        }

        private sealed class EmptyDataset : IDataset
        {
            public IEnumerable<IFieldDefinition> FieldDefs => Array.Empty<IFieldDefinition>();

            public IEnumerable<IDataRecord> DataRecords => Array.Empty<IDataRecord>();

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId) => DataRecords;

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId, int limit) => DataRecords;

            public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] filters) => DataRecords;
        }
    }
}
