// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using FrozenSend = XMAT.WebServiceCapture.RequestComposerWindow.FrozenSend;
using SendCheck = XMAT.WebServiceCapture.RequestComposerWindow.SendCheck;

namespace XMAT.Tests.Replay
{
    // Send's preparation (Slice S-A): what Send froze at its entry is checked off the UI thread, the request first, then, for
    // a URL with '#', the run with the numbers that the user chose. Cancel request or closing the window stops either check,
    // whose late result is not used, and the window releases its client only once the check has returned. The checks refuse
    // in the order that Send always kept; the run is checked and sent with the request and the transport that Send froze;
    // the question names the origin that the check found; and a run that the operation refused once it was sent is worded
    // off the UI thread, as a single send words it. Requests go to loopback servers only.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerSendPreparationTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        public ComposerSendPreparationTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerSendPreparationTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // Send's first check, and for a URL with '#' the check of the run with its numbers, run off the UI thread; the run
        // that they checked is then sent.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task BothChecks_RunOffTheUiThread_ThenTheRunIsSentAsChecked(bool numbered) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            int uiThread = Environment.CurrentManagedThreadId;
            var checks = new List<(string Check, int Thread)>();
            window.PrepareSend = (send, token) =>
            {
                Record(checks, "send");
                return RequestComposerWindow.CheckSend(send, client, token);
            };
            window.PreviewSequence = (send, sequence, transport, token) =>
            {
                Record(checks, "sequence");
                return RequestComposerWindow.CheckSequence(send, sequence, transport, client, token);
            };
            window.ChooseSequence = () => "1-2";
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + (numbered ? "/n/#" : "/one");

                await window.SendAsync();

                Assert.Equal(numbered ? new[] { "send", "sequence" } : new[] { "send" }, checks.Select(entry => entry.Check));
                Assert.DoesNotContain(checks, entry => entry.Thread == uiThread);
                Assert.Equal(
                    numbered ? new[] { "/n/1", "/n/2" } : new[] { "/one" }, server.Requests.Select(request => request.Target));
            }
            finally
            {
                window.Close();
            }
        });

        // Cancel request, or closing the window, while Send checks the request, or the run with its numbers, stops the check:
        // the window is busy and says that it checks until then, then nothing is asked, sent or recorded, whether the check
        // gives what it found once it was stopped or observes its stop. The status says that nothing was sent, and the
        // editors are available again, unless the window closed, which says nothing more.
        [Theory]
        [InlineData("send", false, true)]
        [InlineData("send", false, false)]
        [InlineData("send", true, true)]
        [InlineData("send", true, false)]
        [InlineData("sequence", false, true)]
        [InlineData("sequence", false, false)]
        [InlineData("sequence", true, true)]
        [InlineData("sequence", true, false)]
        public Task CancelOrClose_StopsACheck_AndNothingIsAskedOrSent(string check, bool close, bool late) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), Recording(model));
            int ranges = 0;
            window.ChooseSequence = () =>
            {
                ranges++;
                return "1-2";
            };
            var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool stopped = false;

            // The check finds what it would, a plan that the question would be asked about, then waits until it is stopped,
            // at most Patience, and gives it late, or observes the stop as a check does.
            SendCheck GivenOnceStopped(SendCheck found, CancellationToken token)
            {
                checking.TrySetResult();
                stopped = token.WaitHandle.WaitOne(Patience);
                if (!late)
                    token.ThrowIfCancellationRequested();
                return found;
            }

            if (check == "send")
            {
                window.PrepareSend = (send, token) =>
                    GivenOnceStopped(RequestComposerWindow.CheckSend(send, client, CancellationToken.None), token);
            }
            else
            {
                window.PreviewSequence = (send, sequence, transport, token) => GivenOnceStopped(
                    RequestComposerWindow.CheckSequence(send, sequence, transport, client, CancellationToken.None), token);
            }

            bool closed = false;
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = server.Origin + (check == "send" ? "/one" : "/n/#");
                Task sending = window.SendAsync();
                await checking.Task.WaitAsync(Patience);
                Assert.Equal(Words("COMPOSER_SEND_CHECKING"), Status(window));
                Assert.False(Find<Grid>(window, "RequestEditor").IsEnabled);
                Assert.True(Find<Button>(window, "CancelSendButton").IsEnabled);

                if (close)
                {
                    window.Close();
                    closed = true;
                }
                else
                {
                    window.CancelSend();
                }

                await sending.WaitAsync(Patience);

                Assert.True(stopped);
                Assert.Empty(questions);
                Assert.Equal(check == "send" ? 0 : 1, ranges);
                Assert.Equal(0, server.Connections);
                Assert.Empty(model.CaptureDeviceContexts);
                Assert.Null(window.LastResult);
                if (close)
                {
                    Assert.Equal(Words("COMPOSER_SEND_CHECKING"), Status(window));
                }
                else
                {
                    Assert.Equal(Words("COMPOSER_SEND_PREPARE_CANCELLED"), Status(window));
                    Assert.True(Find<Grid>(window, "RequestEditor").IsEnabled);
                    Assert.True(Find<Button>(window, "SendButton").IsEnabled);
                    Assert.False(Find<Button>(window, "CancelSendButton").IsEnabled);
                }
            }
            finally
            {
                if (!closed)
                    window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A window that closes while a check of Send runs disposes the client that it created only once the check has
        // returned, since the check may still be using it; then it does.
        [Theory]
        [InlineData("send")]
        [InlineData("sequence")]
        public Task ClosingDuringACheck_DisposesTheWindowsClient_OnlyOnceTheCheckHasReturned(string check) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            RequestReplayClient client = window.Client;
            window.ChooseSequence = () => "1-2";
            var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            bool? disposedWhileChecking = null;

            SendCheck HeldUntilReleased()
            {
                checking.TrySetResult();
                release.Wait(Patience);
                disposedWhileChecking = client.IsDisposed;
                return SendCheck.Refused("Given once the window closed.");
            }

            if (check == "send")
                window.PrepareSend = (_, _) => HeldUntilReleased();
            else
                window.PreviewSequence = (_, _, _, _) => HeldUntilReleased();
            bool closed = false;
            try
            {
                Find<TextBox>(window, "UrlBox").Text = check == "send" ? "http://127.0.0.1:9/one" : "http://127.0.0.1:9/n/#";
                Task sending = window.SendAsync();
                await checking.Task.WaitAsync(Patience);

                window.Close();
                closed = true;
                Assert.False(client.IsDisposed);
                release.Set();
                await sending.WaitAsync(Patience);

                Assert.False(disposedWhileChecking);
                Assert.True(client.IsDisposed);
            }
            finally
            {
                release.Set();
                if (!closed)
                    window.Close();
            }
        });

        // The run is checked and sent with the request and the transport that Send froze: what changes in the window while
        // the user chooses the numbers, in its draft too, reaches neither, and the check of the run takes the transport
        // that the first check parsed, never the controls.
        [Fact]
        public Task TheRunIsCheckedAndSent_WithTheRequestAndTheTransportThatSendFroze() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            var previewed = new List<ReplayTransportOptions>();
            window.PreviewSequence = (send, sequence, transport, token) =>
            {
                lock (previewed)
                    previewed.Add(transport);
                return RequestComposerWindow.CheckSequence(send, sequence, transport, client, token);
            };
            window.ChooseSequence = () =>
            {
                // HTTP/2 only would be refused for this http:// URL; switching Base64 copies the editors into the draft.
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/edited/#";
                Find<ComboBox>(window, "HttpVersionBox").SelectedIndex = 2;
                var base64 = Find<CheckBox>(window, "Base64Box");
                base64.IsChecked = base64.IsChecked != true;
                return "1-2";
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/n/#";

                await window.SendAsync();

                Assert.Equal(new[] { new ReplayTransportOptions() }, previewed);
                Assert.Equal(new[] { "/n/1", "/n/2" }, server.Requests.Select(request => request.Target));
            }
            finally
            {
                window.Close();
            }
        });

        // Send's first check refuses as Send always did, in this order: a form whose Content-Type the headers would drop, an
        // endpoint that is not one, a part that a request file left out, and a '#' outside the path and query. A URL with
        // '#' that passes them all gets the transport that the check parsed, and no plan yet: nothing is previewed before
        // the user chooses the numbers.
        [Fact]
        public void TheFirstCheck_RefusesInTheOrderThatSendKeeps()
        {
            using var client = new RequestReplayClient();
            int previews = 0;
            client.OperationFactory = () =>
            {
                Interlocked.Increment(ref previews);
                return new ReplayOperation();
            };
            RequestReplayDraft omitted = RequestReplayDraft.FromRequestFile(new RequestFileContent(
                RequestFileProfile.Template,
                "POST",
                "http://127.0.0.#:9/x",
                ReplayHttpVersion.Auto,
                new[] { new KeyValuePair<string, string>("Connection", "Content-Type") },
                null,
                new RequestOmissions(false, false, false, true)));
            var complete = new RequestReplayDraft
            {
                Method = "POST",
                Url = "http://127.0.0.#:9/x",
                HeadersText = "Connection: Content-Type",
            };

            Assert.Equal(
                new[]
                {
                    Words("COMPOSER_FORM_CONTENT_TYPE_DROPPED"),
                    Words("COMPOSER_ERROR", Words("COMPOSER_CONNECT_TO_INVALID")),
                    Words("COMPOSER_ERROR", Words("COMPOSER_INCOMPLETE", Words("COMPOSER_PART_BODY"))),
                    Words("COMPOSER_SEQUENCE_INVALID"),
                },
                new[]
                {
                    Check(omitted, form: true, connectTo: true).Refusal,
                    Check(omitted, form: false, connectTo: true).Refusal,
                    Check(omitted, form: false, connectTo: false).Refusal,
                    Check(complete, form: false, connectTo: false).Refusal,
                });
            SendCheck numbered = Check(new RequestReplayDraft { Url = "http://127.0.0.1:9/n/#" }, form: false, connectTo: false);
            Assert.Null(numbered.Refusal);
            Assert.Equal(new ReplayTransportOptions(), numbered.Transport);
            Assert.Null(numbered.Plan);
            Assert.Equal(0, previews);

            SendCheck Check(RequestReplayDraft draft, bool form, bool connectTo) => RequestComposerWindow.CheckSend(
                new FrozenSend(draft, form, 0, ReplayHttpVersion.Auto, connectTo, " xmat.test:443 ", true),
                client,
                CancellationToken.None);
        }

        // A check that is stopped gives no result: stopped before it starts, it previews nothing; stopped while its preview
        // runs, which takes no token, it gives neither the plan nor the refusal that the preview found.
        [Fact]
        public void AStoppedCheck_GivesNoResult_ThoughItsPreviewTakesNoToken()
        {
            FrozenSend single = Frozen("http://127.0.0.1:9/one");
            FrozenSend http2 = Frozen("http://127.0.0.1:9/one") with { HttpVersion = ReplayHttpVersion.Http2 };
            FrozenSend numbered = Frozen("http://127.0.0.1:9/n/#");
            Assert.True(ReplaySequence.TryParse("1-2", out ReplaySequence sequence));
            var direct = new ReplayTransportOptions();
            using var client = new RequestReplayClient();
            int previews = 0;
            client.OperationFactory = () =>
            {
                Interlocked.Increment(ref previews);
                return new ReplayOperation();
            };
            using var stopped = new CancellationTokenSource();
            stopped.Cancel();

            Assert.Throws<OperationCanceledException>(() => RequestComposerWindow.CheckSend(single, client, stopped.Token));
            Assert.Throws<OperationCanceledException>(() => RequestComposerWindow.CheckSend(numbered, client, stopped.Token));
            Assert.Throws<OperationCanceledException>(
                () => RequestComposerWindow.CheckSequence(numbered, sequence, direct, client, stopped.Token));
            Assert.Equal(0, previews);

            Assert.Throws<OperationCanceledException>(
                () => StoppedDuringPreview((previewing, token) => RequestComposerWindow.CheckSend(single, previewing, token)));
            Assert.Throws<OperationCanceledException>(
                () => StoppedDuringPreview((previewing, token) => RequestComposerWindow.CheckSend(http2, previewing, token)));
            Assert.Throws<OperationCanceledException>(() => StoppedDuringPreview(
                (previewing, token) => RequestComposerWindow.CheckSequence(numbered, sequence, direct, previewing, token)));
        }

        // A run that the operation refuses once it is sent, because one of its requests cannot be built, says why as a
        // single send of the widest request says it, worded off the UI thread; nothing is sent.
        [Fact]
        public Task ARunRefusedOnceSent_IsWordedOffTheUiThread_AsASingleSendWordsIt() => _application.RunAsync(async () =>
        {
            const string headers = "X-Kept: yes\r\nnot a header line";
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            int uiThread = Environment.CurrentManagedThreadId;
            var explained = new List<(string Code, int Thread)>();
            window.ChooseSequence = () => "1-2";
            window.PreviewSequence = LetThroughAsIfItCouldBeBuilt;
            window.ExplainRunRefusal = (diagnostic, plan) =>
            {
                lock (explained)
                    explained.Add((diagnostic?.Code, Environment.CurrentManagedThreadId));
                return RequestComposerWindow.RunRefusalText(diagnostic, plan);
            };
            try
            {
                Find<TextBox>(window, "HeadersBox").Text = headers;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/item/#";

                await window.SendAsync();

                Assert.Equal(Words("COMPOSER_ERROR", WidestRequestError(server, headers)), Status(window));
                (string code, int thread) = Assert.Single(explained);
                Assert.Equal(ReplayOperation.RequestInvalidCode, code);
                Assert.NotEqual(uiThread, thread);
                Assert.Equal(0, server.Connections);
                Assert.Null(window.LastResult);
            }
            finally
            {
                window.Close();
            }
        });

        // Cancel request once the run has ended, while the window still words why the operation refused it, changes nothing
        // in what the window says: the run's own refusal, never a run cancelled before sending.
        [Fact]
        public Task CancellingOnceTheRunHasEnded_KeepsWhatTheRunDid() => _application.RunAsync(async () =>
        {
            const string headers = "X-Kept: yes\r\nnot a header line";
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            var explaining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            window.ChooseSequence = () => "1-2";
            window.PreviewSequence = LetThroughAsIfItCouldBeBuilt;
            window.ExplainRunRefusal = (diagnostic, plan) =>
            {
                explaining.TrySetResult();
                release.Wait(Patience);
                return RequestComposerWindow.RunRefusalText(diagnostic, plan);
            };
            try
            {
                Find<TextBox>(window, "HeadersBox").Text = headers;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/item/#";
                Task sending = window.SendAsync();
                await explaining.Task.WaitAsync(Patience);

                window.CancelSend();
                release.Set();
                await sending.WaitAsync(Patience);

                Assert.Equal(Words("COMPOSER_ERROR", WidestRequestError(server, headers)), Status(window));
                Assert.Equal(0, server.Connections);
            }
            finally
            {
                release.Set();
                window.Close();
            }
        });

        // The question names the URL's origin for Connect to another endpoint as the check found it: the window parses
        // nothing to ask. Declined, nothing is sent, and the status is as it was before Send.
        [Fact]
        public Task TheQuestion_NamesTheOriginThatTheCheckFound() => _application.RunAsync(async () =>
        {
            const string Url = "https://xmat.test/item";
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, false));
            window.PrepareSend = (send, token) =>
                RequestComposerWindow.CheckSend(send, client, token) with { ConnectToOrigin = "origin as the check found it" };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = "127.0.0.1:8443";
                string before = Status(window);

                await window.SendAsync();

                Assert.Equal(
                    Words("COMPOSER_CONFIRM_SEND", "GET", Url) + "\n\n" +
                        Words("COMPOSER_CONFIRM_CONNECT_TO", "127.0.0.1:8443", "origin as the check found it"),
                    Assert.Single(questions));
                Assert.Equal(before, Status(window));
                Assert.Null(window.LastResult);
            }
            finally
            {
                window.Close();
            }
        });

        // An endpoint that is not one is refused by Send's first check, off the UI thread, from the text as it was typed,
        // which Send froze without trimming or parsing it.
        [Fact]
        public Task AnEndpointThatIsNotOne_IsRefusedByTheFirstCheck_OffTheUiThread() => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            int uiThread = Environment.CurrentManagedThreadId;
            var checks = new List<(string Check, int Thread)>();
            FrozenSend frozen = null;
            window.PrepareSend = (send, token) =>
            {
                Record(checks, "send");
                frozen = send;
                return RequestComposerWindow.CheckSend(send, client, token);
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = "https://xmat.test/item";
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = " xmat.test:443 ";

                await window.SendAsync();

                Assert.NotEqual(uiThread, Assert.Single(checks).Thread);
                Assert.Equal((true, " xmat.test:443 "), (frozen.ConnectTo, frozen.Endpoint));
                Assert.Equal(Words("COMPOSER_ERROR", Words("COMPOSER_CONNECT_TO_INVALID")), Status(window));
            }
            finally
            {
                window.Close();
            }
        });

        // A URL with '#' whose widest request cannot be built is refused by the check of the run, off the UI thread, as a
        // single send of that request says it; nothing is sent.
        [Fact]
        public Task ASequenceWhoseWidestRequestCannotBeBuilt_IsRefusedOffTheUiThread_AsASingleSendSaysIt() => _application.RunAsync(async () =>
        {
            const string headers = "X-Kept: yes\r\nnot a header line";
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            int uiThread = Environment.CurrentManagedThreadId;
            var checks = new List<(string Check, int Thread)>();
            window.ChooseSequence = () => "1-2";
            window.PreviewSequence = (send, sequence, transport, token) =>
            {
                Record(checks, "sequence");
                return RequestComposerWindow.CheckSequence(send, sequence, transport, client, token);
            };
            try
            {
                Find<TextBox>(window, "HeadersBox").Text = headers;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/item/#";

                await window.SendAsync();

                Assert.Equal(Words("COMPOSER_ERROR", WidestRequestError(server, headers)), Status(window));
                Assert.NotEqual(uiThread, Assert.Single(checks).Thread);
                Assert.Equal(0, server.Connections);
            }
            finally
            {
                window.Close();
            }
        });

        // A check of the run that lets its request through as if it could be built: the run's plan, as the preview would
        // give it, for the operation that sends it to check again.
        private static SendCheck LetThroughAsIfItCouldBeBuilt(
            FrozenSend send, ReplaySequence sequence, ReplayTransportOptions transport, CancellationToken token) =>
            new(null, transport, new ReplayPlanPreview(send.Draft, sequence, sequence.Count, false, false, transport), null);

        // What building the widest request of the numbers 1 to 2 says, as a single send of it says it.
        private static string WidestRequestError(LoopbackHttpServer server, string headers)
        {
            var widest = new RequestReplayDraft { Url = server.Origin + "/item/2", HeadersText = headers };
            return Assert.Throws<FormatException>(() => widest.CreateRequestToSend()).Message;
        }

        // A frozen GET without a body or options.
        private static FrozenSend Frozen(string url) =>
            new(new RequestReplayDraft { Url = url }, false, 0, ReplayHttpVersion.Auto, false, "", url.Contains('#'));

        // Runs a check with a client whose preview stops the check while it runs, and gives what the check gives.
        private static SendCheck StoppedDuringPreview(Func<RequestReplayClient, CancellationToken, SendCheck> check)
        {
            using var stop = new CancellationTokenSource();
            using var client = new RequestReplayClient();
            client.OperationFactory = () =>
            {
                stop.Cancel();
                return new ReplayOperation();
            };
            return check(client, stop.Token);
        }

        private static void Record(List<(string Check, int Thread)> checks, string check)
        {
            lock (checks)
                checks.Add((check, Environment.CurrentManagedThreadId));
        }

        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        private static ReplayRecordingTarget Recording(CaptureAppModel model)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(model, action => dispatcher.Invoke(action));
        }

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }

        private static string Words(string key, params object[] arguments) => Localization.GetLocalizedString(key, arguments);

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;
    }
}
