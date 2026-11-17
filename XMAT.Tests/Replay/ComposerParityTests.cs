// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The composer's Fiddler parity: following same-origin redirects, numbered URLs, Upload File, Inspect session
    // and dragging a request in, with the lifecycle of a run. Requests go to loopback servers only, and each test records
    // into its own application model.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerParityTests
    {
        private const string SourceTab = "Source tab";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private readonly CaptureUiApplicationFixture _application;

        public ComposerParityTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerParityTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // Off by default, a redirect is the response. Selected, redirects are followed within the origin, at most as many as
        // chosen, 10 at first; the status says how many were followed, or why the last was not.
        [Fact]
        public Task FollowRedirects_IsOffByDefault_AndFollowsAtMostTheChosenNumber() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(request => request.Target switch
            {
                "/start" => LoopbackResponse.Redirect(302, "/next"),
                "/next" => LoopbackResponse.Redirect(307, "/final"),
                _ => LoopbackResponse.Ok("arrived"),
            });
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            try
            {
                var follow = Find<CheckBox>(window, "FollowRedirectsBox");
                var limit = Find<ComboBox>(window, "RedirectLimitBox");
                Assert.False(follow.IsChecked == true);
                Assert.Equal(Enumerable.Range(1, 10), limit.Items.Cast<int>());
                Assert.Equal(10, limit.SelectedItem);
                Assert.False(limit.IsEnabled);
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/start";

                await window.SendAsync();

                Assert.Equal("302", window.LastResult.Response.Status);
                Assert.Equal(new[] { "/start" }, Targets(server));

                follow.IsChecked = true;
                Assert.True(limit.IsEnabled);
                await window.SendAsync();

                Assert.Equal("200", window.LastResult.Response.Status);
                Assert.Equal("arrived"u8.ToArray(), window.LastResult.Response.BodyBytes);
                Assert.Contains(Localization.GetLocalizedString("COMPOSER_REDIRECTS_FOLLOWED", 2), Status(window));

                limit.SelectedItem = 1;
                await window.SendAsync();

                Assert.Equal("307", window.LastResult.Response.Status);
                Assert.Contains(Localization.GetLocalizedString("COMPOSER_REDIRECT_LIMIT_REACHED", 1), Status(window));
                Assert.Equal(new[] { "/start", "/start", "/next", "/final", "/start", "/next" }, Targets(server));
            }
            finally
            {
                window.Close();
            }
        });

        // A redirect to another origin is never followed: the credentials, headers and body that the user typed reach only
        // the origin the request was sent to, and the status says why the redirect is the result. The one confirmation of
        // the unsafe method says that redirects can be more requests.
        [Fact]
        public Task FollowRedirects_NeverTakesCredentialsHeadersOrTheBodyToAnotherOrigin() => _application.RunAsync(async () =>
        {
            await using var other = new LoopbackHttpServer(_ => LoopbackResponse.Ok("must not be reached"));
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Redirect(307, other.Origin + "/collect"));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), Recording(model));
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/start";
                Find<TextBox>(window, "HeadersBox").Text = "Authorization: Bearer canary-credential\r\nX-Canary: canary-header";
                Find<TextBox>(window, "BodyBox").Text = "canary-body";
                Find<CheckBox>(window, "FollowRedirectsBox").IsChecked = true;

                await window.SendAsync();

                Assert.Contains(
                    Localization.GetLocalizedString("COMPOSER_CONFIRM_REDIRECTS", 10), Assert.Single(questions));
                Assert.Empty(other.Requests);
                Assert.Equal(0, other.Connections);
                LoopbackRequest sent = Assert.Single(server.Requests);
                Assert.Equal(
                    ("Bearer canary-credential", "canary-header", "canary-body"),
                    (sent.Header("Authorization"), sent.Header("X-Canary"), Encoding.UTF8.GetString(sent.Body)));
                Assert.Equal("307", window.LastResult.Response.Status);
                Assert.Contains(Localization.GetLocalizedString("COMPOSER_REDIRECT_ORIGIN"), Status(window));
                Assert.Equal("307", Assert.Single(Replays(model).ProxyConnections).Status);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // As in Fiddler, '#' in the URL sends one request per number of the range, padded to the width of a Start At with
        // a leading zero, in one run with one confirmation. Only the URL is numbered, and what is sent is what was checked
        // and confirmed, whatever is typed meanwhile.
        [Fact]
        public Task NumberedUrl_SendsOneRequestPerNumber_InOneConfirmedRun() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok("reply " + request.Target));
            using var client = new RequestReplayClient();
            var operations = new List<ReplayOperation>();
            client.OperationFactory = () =>
            {
                var operation = new ReplayOperation();
                operations.Add(operation);
                return operation;
            };
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            RequestComposerWindow window = null;
            window = RequestComposerWindow.ForNewRequest(
                client,
                message =>
                {
                    questions.Add(message);
                    Find<TextBox>(window, "UrlBox").Text = server.Origin + "/typed-later/#";
                    Find<TextBox>(window, "HeadersBox").Text = "X-Tag: typed later";
                    return true;
                },
                Recording(model));
            int ranges = 0;
            window.ChooseSequence = () =>
            {
                ranges++;
                return "08-11";
            };
            try
            {
                string template = server.Origin + "/#/?a=#";
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = template;
                Find<TextBox>(window, "HeadersBox").Text = "X-Tag: #1";
                Find<TextBox>(window, "BodyBox").Text = "#body#";

                await window.SendAsync();

                Assert.Equal(1, ranges);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_CONFIRM_SEQUENCE", 4, "POST", "08", "11", template),
                    Assert.Single(questions));
                Assert.Equal(2, operations.Count);
                string[] expected = { "/08/?a=08", "/09/?a=09", "/10/?a=10", "/11/?a=11" };
                Assert.Equal(expected, Targets(server));
                Assert.All(server.Requests, request =>
                {
                    Assert.Equal("POST", request.Method);
                    Assert.Equal("#1", request.Header("X-Tag"));
                    Assert.Equal("#body#"u8.ToArray(), request.Body);
                });
                Assert.Equal(expected, Replays(model).ProxyConnections.Select(row => row.Path));
                Assert.Equal("reply /11/?a=11"u8.ToArray(), window.LastResult.Response.BodyBytes);
                Assert.StartsWith(
                    Localization.GetLocalizedString("COMPOSER_SEQUENCE_COMPLETED", 4, 4, 0), Status(window));
                Assert.True(model.CanModifyCaptureData);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // "%23" is an escaped '#': one request, sent as typed, and no range is asked for.
        [Fact]
        public Task EscapedHash_IsOneRequest_SentAsTyped() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            window.ChooseSequence = () => throw new Xunit.Sdk.XunitException("%23 does not number requests.");
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/a%23b?c=%23";
                Assert.True(Find<Button>(window, "CopyCurlButton").IsEnabled);

                await window.SendAsync();

                Assert.Equal(new[] { "/a%23b?c=%23" }, Targets(server));
                Assert.Equal("200", window.LastResult.Response.Status);
            }
            finally
            {
                window.Close();
            }
        });

        // Cancelling the range, or declining the confirmation, sends nothing and records nothing.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task NumberedUrl_SendsNothing_WhenTheRangeIsCancelledOrTheRunDeclined(bool decline) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, false), Recording(model));
            window.ChooseSequence = () => decline ? "1-3" : null;
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "DELETE";
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/item/#";

                await window.SendAsync();

                Assert.Equal(decline ? 1 : 0, questions.Count);
                Assert.Equal(0, server.Connections);
                Assert.Empty(model.CaptureDeviceContexts);
                Assert.Null(window.LastResult);
                Assert.True(Find<Button>(window, "SendButton").IsEnabled);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A run whose URLs would be too long together is refused by the preview, before any number is put in the URL and
        // without running anything; a '#' outside the path and query is refused without asking for a range.
        [Fact]
        public Task NumberedUrl_IsCheckedBeforeAnythingIsSent_AndNeverNumbersTheHost() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var operations = new List<ReplayOperation>();
            client.OperationFactory = () =>
            {
                var operation = new ReplayOperation { MaxSequenceUrlCharacters = 1000 };
                operations.Add(operation);
                return operation;
            };
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            int ranges = 0;
            window.ChooseSequence = () =>
            {
                ranges++;
                return "1-100";
            };
            try
            {
                // A hundred URLs of about 30 characters are more than 1,000 characters together.
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/item/#";
                await window.SendAsync();

                Assert.Equal(1, ranges);
                Assert.Single(operations);
                Assert.StartsWith(Localization.GetLocalizedString("COMPOSER_ERROR", string.Empty), Status(window));
                Assert.Contains("too long together", Status(window));

                Find<TextBox>(window, "UrlBox").Text = $"http://127.0.0.#:{server.Port}/item";
                await window.SendAsync();

                Assert.Equal(1, ranges);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SEQUENCE_INVALID"), Status(window));
                Assert.Equal(0, server.Connections);
                Assert.Null(window.LastResult);
            }
            finally
            {
                window.Close();
            }
        });

        // A numbered URL whose request cannot be built says what is wrong as a single send of the request says it:
        // what building it with the widest number says, not the operation's message for any request that cannot be
        // built. Nothing is sent.
        [Fact]
        public Task NumberedUrl_ThatCannotBeBuilt_SaysWhyAsASingleSendDoes() => _application.RunAsync(async () =>
        {
            const string headers = "X-Kept: yes\r\nnot a header line";
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            int ranges = 0;
            window.ChooseSequence = () =>
            {
                ranges++;
                return "1-2";
            };
            try
            {
                Find<TextBox>(window, "HeadersBox").Text = headers;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/item/2";
                await window.SendAsync();
                string singleSend = Status(window);

                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/item/#";
                await window.SendAsync();

                Assert.Equal(1, ranges);
                Assert.Equal(singleSend, Status(window));
                var widest = new RequestReplayDraft { Url = server.Origin + "/item/2", HeadersText = headers };
                FormatException built = Assert.Throws<FormatException>(() => widest.CreateRequestToSend());
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_ERROR", built.Message), singleSend);
                Assert.True(ReplaySequence.TryParse("1-2", out ReplaySequence sequence));
                var preview = client.Preview(
                    ReplaySource.FromTemplate(
                        new RequestReplayDraft { Url = server.Origin + "/item/#", HeadersText = headers }, sequence),
                    0);
                var refused = Assert.Single(preview.Diagnostics);
                Assert.Equal(ReplayOperation.RequestInvalidCode, refused.Code);
                Assert.NotEqual(Localization.GetLocalizedString("COMPOSER_ERROR", refused.Message), Status(window));
                Assert.Equal(0, server.Connections);
                Assert.Null(window.LastResult);
            }
            finally
            {
                window.Close();
            }
        });

        // Numbered URLs keep the captured-origin rule: a GET whose numbers go to another origin than the captured one, with
        // the captured headers, needs the run's one confirmation.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task NumberedUrl_ToAnotherOrigin_NeedsTheOneConfirmation_EvenForAGet(bool accept) => _application.RunAsync(async () =>
        {
            await using var captured = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var other = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            var window = new RequestComposerWindow(
                Capture(captured.Port, "/orig", "Authorization: Bearer captured\r\n"), client, Answer(questions, accept));
            window.ChooseSequence = () => "1-2";
            try
            {
                Find<TextBox>(window, "UrlBox").Text = other.Origin + "/x/#";

                await window.SendAsync();

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_CONFIRM_SEQUENCE", 2, "GET", "1", "2", other.Origin + "/x/#"),
                    Assert.Single(questions));
                Assert.Equal(0, captured.Connections);
                Assert.Equal(accept ? new[] { "/x/1", "/x/2" } : Array.Empty<string>(), Targets(other));
            }
            finally
            {
                window.Close();
            }
        });

        // Upload File replaces the body with the exact bytes read then, shared by every request of a numbered URL: changing
        // the file afterwards changes nothing, no content header is guessed, and the editor shows the file's name and size
        // rather than its Base64. Clear body removes it.
        [Fact]
        public Task UploadFile_SendsTheExactBytesReadAtUpload_AndClearBodyRemovesThem() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, _ => true);
            try
            {
                byte[] original = { 0, 255, 13, 10, 0x89, (byte)'P', (byte)'N', (byte)'G' };
                string path = Path.Join(directory, "upload.bin");
                File.WriteAllBytes(path, original);
                window.ChooseBodyFile = () => path;
                window.ChooseSequence = () => "1-3";
                Find<TextBox>(window, "MethodBox").Text = "PUT";
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/item/#";
                Find<TextBox>(window, "BodyBox").Text = "replaced by the upload";

                await window.UploadBodyAsync();

                var body = Find<TextBox>(window, "BodyBox");
                Assert.Equal(Visibility.Collapsed, body.Visibility);
                Assert.Equal(string.Empty, body.Text);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_BODY_FILE", "upload.bin", original.LongLength),
                    Find<TextBlock>(window, "BodyFileText").Text);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_UPLOADED", "upload.bin", original.LongLength), Status(window));
                File.WriteAllBytes(path, "changed after the upload"u8.ToArray());

                await window.SendAsync();

                Assert.Equal(new[] { "/item/1", "/item/2", "/item/3" }, Targets(server));
                Assert.All(server.Requests, request =>
                {
                    Assert.Equal(original, request.Body);
                    Assert.Null(request.Header("Content-Type"));
                });

                window.ClearBody();
                Assert.Equal(Visibility.Visible, body.Visibility);
                Assert.Equal(string.Empty, body.Text);
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/cleared";
                await window.SendAsync();

                Assert.Equal("/cleared", server.Requests[^1].Target);
                Assert.Empty(server.Requests[^1].Body);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // A file that is missing or larger than 64 MiB, or no file chosen, leaves the body as it was, and that is sent.
        [Theory]
        [InlineData("missing")]
        [InlineData("too large")]
        [InlineData("none chosen")]
        public Task UploadFile_ThatIsNotRead_LeavesTheBodyAsItWas(string kind) => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, _ => true);
            try
            {
                string path = Path.Join(directory, "body.bin");
                if (kind == "too large")
                {
                    using var file = new FileStream(path, FileMode.CreateNew);
                    file.SetLength(ReplayBodyFile.MaxBytes + 1);
                }

                window.ChooseBodyFile = () => kind == "none chosen" ? null : path;
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/kept";
                Find<TextBox>(window, "BodyBox").Text = "kept";
                string status = Status(window);

                await window.UploadBodyAsync();

                Assert.Equal(
                    kind switch
                    {
                        "missing" => Localization.GetLocalizedString("COMPOSER_UPLOAD_NOT_FOUND"),
                        "too large" => Localization.GetLocalizedString("COMPOSER_UPLOAD_TOO_LARGE"),
                        _ => status,
                    },
                    Status(window));
                Assert.Equal(Visibility.Visible, Find<TextBox>(window, "BodyBox").Visibility);
                Assert.Equal("kept", Find<TextBox>(window, "BodyBox").Text);
                Assert.True(Find<Button>(window, "SendButton").IsEnabled);

                await window.SendAsync();

                Assert.Equal("kept"u8.ToArray(), Assert.Single(server.Requests).Body);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // The window can close while the file dialog is open, with its owner, say: the chosen file is then not read, and
        // the window's status and body stay as they were.
        [Fact]
        public Task UploadFile_WhenTheWindowClosesWhileChoosing_ReadsNothing() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, _ => true);
            bool closed = false;
            try
            {
                string path = Path.Join(directory, "body.bin");
                File.WriteAllBytes(path, "uploaded"u8.ToArray());
                window.ChooseBodyFile = () =>
                {
                    window.Close();
                    closed = true;
                    return path;
                };
                Find<TextBox>(window, "BodyBox").Text = "kept";
                string status = Status(window);

                await window.UploadBodyAsync();

                Assert.True(closed);
                Assert.Equal(status, Status(window));
                Assert.Equal(Visibility.Visible, Find<TextBox>(window, "BodyBox").Visibility);
                Assert.Equal("kept", Find<TextBox>(window, "BodyBox").Text);
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }

                Directory.Delete(directory, recursive: true);
            }
        });

        // Fiddler's <@INCLUDE@> tokens are not supported: the text is sent as typed, and the file it names is not read.
        [Fact]
        public Task IncludeTokens_AreSentAsTyped_WithoutReadingTheFile() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, _ => true);
            window.ChooseBodyFile = () => throw new Xunit.Sdk.XunitException("Nothing asks for a file.");
            try
            {
                string secret = Path.Join(directory, "secret.txt");
                File.WriteAllText(secret, "file contents that must not be sent");
                string token = $"<@INCLUDE *{secret}*@>";
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/upload";
                Find<TextBox>(window, "BodyBox").Text = token;

                await window.SendAsync();

                Assert.Equal(Encoding.UTF8.GetBytes(token), Assert.Single(server.Requests).Body);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // Inspect session, off by default, selects the row that the send recorded last, by reference: not a row with the
        // same number in a tab with the same name, as a capture loaded from a saved file can have.
        [Fact]
        public Task InspectSession_SelectsTheRowItRecorded_NotOneWithTheSameNumberInASameNamedTab() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok(request.Target));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ICaptureDeviceContext replays = model.GetOrAddReplayContext();
            ICaptureDeviceContext twin = AddTab(
                model,
                replays.DeviceName,
                Capture(server.Port, "/twin1", number: 1),
                Capture(server.Port, "/twin2", number: 2),
                Capture(server.Port, "/twin3", number: 3));
            ICaptureDeviceContext other = AddTab(model, "Other");
            model.SelectedDeviceContext = other;
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/one";
                await window.SendAsync();

                Assert.False(Find<CheckBox>(window, "InspectSessionBox").IsChecked == true);
                Assert.Same(other, model.SelectedDeviceContext);
                Assert.Null(ControllerOf(replays).SelectedConnectionModel);

                Find<CheckBox>(window, "InspectSessionBox").IsChecked = true;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/n/#";
                window.ChooseSequence = () => "1-2";
                await window.SendAsync();

                ProxyConnectionModel last = ControllerOf(replays).ProxyConnections[^1];
                Assert.Equal(("/n/2", 3L), (last.Path, last.RequestNumber));
                Assert.Same(replays, model.SelectedDeviceContext);
                Assert.Same(last, ControllerOf(replays).SelectedConnectionModel);
                Assert.Null(ControllerOf(twin).SelectedConnectionModel);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A send that got no response is recorded and inspected too. A send whose row could not be recorded, because its
        // tab was closed meanwhile, selects nothing: not the older row of the earlier send.
        [Fact]
        public Task InspectSession_SelectsAFailedSendsRow_AndNothingWhenTheSendWasNotRecorded() => _application.RunAsync(async () =>
        {
            var held = new TaskCompletionSource<LoopbackResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new LoopbackHttpServer(request => request.Target switch
            {
                "/fail" => throw new InvalidOperationException("The server closes this connection without answering."),
                "/held" => held.Task,
                _ => Task.FromResult(LoopbackResponse.Ok()),
            });
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ICaptureDeviceContext other = AddTab(model, "Other");
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<CheckBox>(window, "InspectSessionBox").IsChecked = true;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/fail";
                await window.SendAsync();

                ICaptureDeviceContext replays = model.GetOrAddReplayContext();
                ProxyConnectionModel failed = Assert.Single(ControllerOf(replays).ProxyConnections);
                Assert.Equal(string.Empty, failed.Status);
                Assert.Same(replays, model.SelectedDeviceContext);
                Assert.Same(failed, ControllerOf(replays).SelectedConnectionModel);

                model.SelectedDeviceContext = other;
                ControllerOf(replays).SelectedConnectionModel = null;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/held";
                Task sending = window.SendAsync();
                await WaitUntilAsync(() => server.Requests.Count == 2);
                model.CaptureDeviceContexts.Remove(replays);
                held.SetResult(LoopbackResponse.Ok());
                await sending.WaitAsync(Patience);

                Assert.Same(other, model.SelectedDeviceContext);
                Assert.Null(ControllerOf(replays).SelectedConnectionModel);
                Assert.Same(failed, Assert.Single(ControllerOf(replays).ProxyConnections));
                Assert.Contains(
                    Localization.GetLocalizedString("COMPOSER_NOT_RECORDED", replays.DeviceName), Status(window));
            }
            finally
            {
                held.TrySetResult(LoopbackResponse.Ok());
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The recording keeps the exact tab and row: only a row that this recording added, in its tab while that is open,
        // and never once XMAT is shutting down.
        [Fact]
        public Task InspectSession_SelectsOnlyARowThatTheRecordingAdded_InItsOpenTab() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            ICaptureDeviceContext other = AddTab(model, "Other");
            using var cancellation = new CancellationTokenSource();
            try
            {
                using (ReplayRecording first = target.TryBegin(cancellation, _ => ReplayProvenance.Composed))
                {
                    first.Recorder.Add(Sent(), Answered());
                    ProxyConnectionModel row = first.Recorder.LastRecorded;
                    Assert.Same(row, ControllerOf(first.Destination).ProxyConnections[^1]);
                    Assert.True(target.TrySelectLastRecorded(first));
                    Assert.Same(first.Destination, model.SelectedDeviceContext);
                    Assert.Same(row, ControllerOf(first.Destination).SelectedConnectionModel);
                    model.SelectedDeviceContext = other;
                    ControllerOf(first.Destination).SelectedConnectionModel = null;
                }

                using (ReplayRecording empty = target.TryBegin(cancellation, _ => ReplayProvenance.Composed))
                {
                    Assert.Null(empty.Recorder.LastRecorded);
                    Assert.False(target.TrySelectLastRecorded(empty));
                    Assert.Same(other, model.SelectedDeviceContext);
                    Assert.Null(ControllerOf(empty.Destination).SelectedConnectionModel);
                }

                using (ReplayRecording closed = target.TryBegin(cancellation, _ => ReplayProvenance.Composed))
                {
                    closed.Recorder.Add(Sent(), Answered());
                    model.CaptureDeviceContexts.Remove(closed.Destination);
                    Assert.False(target.TrySelectLastRecorded(closed));
                    Assert.Same(other, model.SelectedDeviceContext);
                    Assert.Null(ControllerOf(closed.Destination).SelectedConnectionModel);
                }

                // XMAT takes the run back to close, as its main window does: its row stays, and nothing is selected.
                Task<bool> closing;
                using (ReplayRecording revoked = target.TryBegin(cancellation, _ => ReplayProvenance.Composed))
                {
                    revoked.Recorder.Add(Sent(), Answered());
                    Assert.False(revoked.IsRevoked);
                    closing = model.CancelCaptureDataOperationAsync();
                    Assert.True(revoked.IsRevoked);
                    Assert.False(target.TrySelectLastRecorded(revoked));
                    Assert.Same(other, model.SelectedDeviceContext);
                    Assert.Null(ControllerOf(revoked.Destination).SelectedConnectionModel);
                    Assert.Same(revoked.Recorder.LastRecorded, ControllerOf(revoked.Destination).ProxyConnections[^1]);
                }

                Assert.True(await closing.WaitAsync(Patience));

                using (ReplayRecording late = target.TryBegin(cancellation, _ => ReplayProvenance.Composed))
                {
                    late.Recorder.Add(Sent(), Answered());
                    Assert.False(late.IsRevoked);
                    model.BeginShutdown();
                    Assert.False(target.TrySelectLastRecorded(late));
                    Assert.Same(other, model.SelectedDeviceContext);
                    Assert.Null(ControllerOf(late.Destination).SelectedConnectionModel);
                }
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // A view that does not show the row that Inspect session selects, as when a filter hides it, selects nothing: never
        // the row that it had selected before.
        [Fact]
        public Task InspectSession_NeverLeavesAnotherRowSelected_InAViewThatHidesTheRow() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            using var cancellation = new CancellationTokenSource();
            var host = new Window
            {
                Width = 1280,
                Height = 720,
                Left = -12000,
                Top = -12000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
            };
            try
            {
                ProxyConnectionModel older;
                using (ReplayRecording first = target.TryBegin(cancellation, _ => ReplayProvenance.Composed))
                {
                    first.Recorder.Add(Sent(), Answered());
                    older = first.Recorder.LastRecorded;
                }

                ICaptureDeviceContext replays = model.GetOrAddReplayContext();
                var view = new CaptureConnectionView { AutoScroll = false, DataContext = replays };
                host.Content = view;
                host.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var grid = (DataGrid)view.FindName("ConnectionList");
                ControllerOf(replays).SelectedConnectionModel = older;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Same(older, grid.SelectedItem);

                using (ReplayRecording second = target.TryBegin(cancellation, _ => ReplayProvenance.Composed))
                {
                    second.Recorder.Add(Sent(), Answered());
                    ProxyConnectionModel newer = second.Recorder.LastRecorded;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    grid.Items.Filter = item => !ReferenceEquals(item, newer);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Same(older, grid.SelectedItem);

                    Assert.True(target.TrySelectLastRecorded(second));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    Assert.True(grid.SelectedItem is null || ReferenceEquals(grid.SelectedItem, newer));
                    Assert.NotSame(older, grid.SelectedItem);
                }
            }
            finally
            {
                host.Close();
                await RemoveAllAsync(model);
            }
        });

        // Cancel request, closing the window, XMAT closing or XMAT exiting stops a numbered run before its next request: the
        // rows recorded stay, the Replays tab is released and nothing waits on anything. Inspect session then selects exactly
        // the run's last recorded row when the user cancelled it, and nothing when the window closed or XMAT stopped the run,
        // also when XMAT closes after the user cancelled but before the run ended. The run shows its progress meanwhile, and
        // a window that closes disposes its own transport only once the run has ended.
        [Theory]
        [InlineData("cancel")]
        [InlineData("close")]
        [InlineData("closing")]
        [InlineData("cancel then closing")]
        [InlineData("shutdown")]
        public Task StoppingANumberedRun_SendsNothingMore_KeepsItsRows_AndInspectsThemOnlyWhenTheUserCancelled(string stop) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(request =>
                request.Target == "/item/2" ? new LoopbackResponse(Hang: true) : LoopbackResponse.Ok());
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ICaptureDeviceContext other = AddTab(model, "Other");
            model.SelectedDeviceContext = other;
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: Recording(model));
            window.ChooseSequence = () => "1-3";
            bool closed = false;
            try
            {
                Find<CheckBox>(window, "InspectSessionBox").IsChecked = true;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/item/#";
                Task sending = window.SendAsync();
                await WaitUntilAsync(() => server.Requests.Count == 2);
                await WaitUntilAsync(() =>
                    Status(window) == Localization.GetLocalizedString("COMPOSER_SENDING_SEQUENCE", 3, 1));
                Assert.False(model.CanModifyCaptureData);
                Assert.False(Find<Grid>(window, "RequestEditor").IsEnabled);
                Assert.False(Find<CheckBox>(window, "FollowRedirectsBox").IsEnabled);
                Assert.False(Find<Button>(window, "SendButton").IsEnabled);
                Assert.False(Find<Button>(window, "CopyCurlButton").IsEnabled);
                Assert.True(Find<Button>(window, "CancelSendButton").IsEnabled);

                Task<bool> closing = null;
                switch (stop)
                {
                    case "cancel":
                        window.CancelSend();
                        break;
                    case "close":
                        window.Close();
                        closed = true;
                        Assert.False(window.Client.IsDisposed);
                        break;
                    case "closing":
                        // As XMAT's main window does when it closes (CaptureAppModel.CanCloseAsync).
                        closing = model.CancelCaptureDataOperationAsync();
                        break;
                    case "cancel then closing":
                        // The user cancels, then closes XMAT's main window before the run has ended.
                        window.CancelSend();
                        closing = model.CancelCaptureDataOperationAsync();
                        break;
                    default:
                        model.BeginShutdown();
                        break;
                }

                await sending.WaitAsync(Patience);
                if (closing != null)
                {
                    Assert.True(await closing.WaitAsync(Patience));
                }

                ICaptureDeviceContext replays = model.GetOrAddReplayContext();
                Assert.Equal(new[] { "/item/1", "/item/2" }, Targets(server));
                ProxyConnectionModel recorded = Assert.Single(ControllerOf(replays).ProxyConnections);
                Assert.Equal("/item/1", recorded.Path);
                Assert.Equal(stop != "shutdown", model.CanModifyCaptureData);
                Assert.Equal(closed, window.Client.IsDisposed);
                if (stop == "cancel")
                {
                    Assert.Same(replays, model.SelectedDeviceContext);
                    Assert.Same(recorded, ControllerOf(replays).SelectedConnectionModel);
                    Assert.Equal(
                        Localization.GetLocalizedString("COMPOSER_SEQUENCE_CANCELLED", 1, 3) + " " +
                            Localization.GetLocalizedString("COMPOSER_RECORDED", CaptureAppModel.ReplayContextName),
                        Status(window));
                    Assert.True(Find<Button>(window, "SendButton").IsEnabled);
                    Assert.True(Find<Grid>(window, "RequestEditor").IsEnabled);
                    Assert.Null(window.LastResult);
                }
                else
                {
                    Assert.Same(other, model.SelectedDeviceContext);
                    Assert.Null(ControllerOf(replays).SelectedConnectionModel);
                }
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

        // A run that recorded nothing selects nothing, not a row that an earlier run recorded; and a run that XMAT stopped
        // selects nothing although an earlier run of the same window was one that the user cancelled.
        [Fact]
        public Task InspectSession_NeverSelectsAnEarlierRunsRow_OrTakesAnEarlierRunsCancelForThisRunsOwn() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(request =>
                request.Target is "/hang" or "/n/2" ? new LoopbackResponse(Hang: true) : LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ICaptureDeviceContext other = AddTab(model, "Other");
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<CheckBox>(window, "InspectSessionBox").IsChecked = true;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/first";
                await window.SendAsync();

                ICaptureDeviceContext replays = model.GetOrAddReplayContext();
                ProxyConnectionModel earlier = Assert.Single(ControllerOf(replays).ProxyConnections);
                Assert.Same(replays, model.SelectedDeviceContext);
                Assert.Same(earlier, ControllerOf(replays).SelectedConnectionModel);

                // The user looks elsewhere, then cancels a send before it is answered: it recorded nothing.
                model.SelectedDeviceContext = other;
                ControllerOf(replays).SelectedConnectionModel = null;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/hang";
                Task sending = window.SendAsync();
                await WaitUntilAsync(() => server.Requests.Count == 2);
                window.CancelSend();
                await sending.WaitAsync(Patience);

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_CANCELLED_NOT_RECORDED", replays.DeviceName), Status(window));
                Assert.Same(earlier, Assert.Single(ControllerOf(replays).ProxyConnections));
                Assert.Same(other, model.SelectedDeviceContext);
                Assert.Null(ControllerOf(replays).SelectedConnectionModel);

                // The next run records a row, and XMAT stops it as when its main window closes: no selection either.
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/n/#";
                window.ChooseSequence = () => "1-2";
                sending = window.SendAsync();
                await WaitUntilAsync(() => server.Requests.Count == 4);
                Task<bool> closing = model.CancelCaptureDataOperationAsync();
                await sending.WaitAsync(Patience);

                Assert.True(await closing.WaitAsync(Patience));
                Assert.Equal(
                    new[] { "/first", "/n/1" }, ControllerOf(replays).ProxyConnections.Select(row => row.Path));
                Assert.Same(other, model.SelectedDeviceContext);
                Assert.Null(ControllerOf(replays).SelectedConnectionModel);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Copy as cURL copies one request as it is, so it is unavailable, with the reason, for a URL with '#' and while
        // redirects are followed.
        [Fact]
        public Task CopyAsCurl_IsUnavailable_ForANumberedUrlOrFollowedRedirects_AndSaysWhy() => _application.RunAsync(() =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                var copy = Find<Button>(window, "CopyCurlButton");
                var url = Find<TextBox>(window, "UrlBox");
                var follow = Find<CheckBox>(window, "FollowRedirectsBox");
                Assert.True(ToolTipService.GetShowOnDisabled(copy));

                url.Text = "http://127.0.0.1:9/plain";
                Assert.True(copy.IsEnabled);
                Assert.Null(copy.ToolTip);

                url.Text = "http://127.0.0.1:9/item/#";
                Assert.False(copy.IsEnabled);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_CURL_NUMBERED_URL"), copy.ToolTip);

                url.Text = "http://127.0.0.1:9/plain";
                follow.IsChecked = true;
                Assert.False(copy.IsEnabled);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_CURL_REDIRECTS"), copy.ToolTip);

                follow.IsChecked = false;
                Assert.True(copy.IsEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

        // The range dialog starts at 1 to 1, keeps the numbers as typed, so a leading zero pads, says how many requests a
        // range sends and accepts only a range that a sequence can have.
        [Fact]
        public void RangeDialog_StartsAtOneToOne_KeepsTheNumbersAsTyped_AndAcceptsOnlyAValidRange()
        {
            _application.Run(_ =>
            {
                var dialog = new SequenceRangeWindow();
                try
                {
                    var start = (TextBox)dialog.FindName("StartBox");
                    var end = (TextBox)dialog.FindName("EndBox");
                    var ok = (Button)dialog.FindName("OkButton");
                    var count = (TextBlock)dialog.FindName("CountText");
                    Assert.Equal(("1", "1", "1-1"), (start.Text, end.Text, dialog.Range));
                    Assert.True(ok.IsEnabled);
                    Assert.Equal(Localization.GetLocalizedString("SEQUENCE_COUNT", 1), count.Text);

                    start.Text = "08";
                    end.Text = " 11 ";
                    Assert.Equal("08-11", dialog.Range);
                    Assert.True(ok.IsEnabled);
                    Assert.Equal(Localization.GetLocalizedString("SEQUENCE_COUNT", 4), count.Text);

                    end.Text = "7";
                    Assert.False(ok.IsEnabled);
                    Assert.Equal(Localization.GetLocalizedString("SEQUENCE_INVALID"), count.Text);

                    start.Text = "1";
                    end.Text = "10001";
                    Assert.False(ok.IsEnabled);
                    end.Text = "10000";
                    Assert.True(ok.IsEnabled);
                }
                finally
                {
                    dialog.Close();
                }
            });
        }

        // A drag carries an opaque id in XMAT's own format only, never the request's headers or body, and lends one
        // replayable request: the only selected row, past the system's drag distance.
        [Fact]
        public void DraggingARow_LendsOneReplayableRequest_ThroughAnOpaqueId()
        {
            _application.Run(window =>
            {
                var post = Capture(9, "/secret", "Authorization: Bearer canary-credential\r\n", "canary-body"u8.ToArray(), "POST");
                var connect = new ProxyConnectionModel(2, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "example.com:443",
                    "CONNECT", "example.com:443", "CONNECT example.com:443 HTTP/1.1\r\n", [], "HTTP/1.1 200 OK\r\n", [],
                    "127.0.0.1");
                var notKept = Capture(
                    9, "/not-kept", RequestReplayDraft.RequestBodyNotKeptHeader + ": 5\r\n", method: "POST", number: 3);
                var get = Capture(9, "/get", number: 4);
                CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), post, connect, notKept, get);
                HostListUiTests.ShowCaptureView(window, view);
                var grid = (DataGrid)view.FindName("ConnectionList");

                Select(grid, post);
                ComposerSessionDragToken drag = view.BeginSessionDrag(post);
                IDataObject data;
                try
                {
                    Assert.NotNull(drag);
                    data = drag.Data;
                    Assert.Equal(new[] { ComposerSessionDrag.Format }, data.GetFormats(true));
                    string id = Assert.IsType<string>(data.GetData(ComposerSessionDrag.Format));
                    Assert.DoesNotContain("canary", id);
                    Assert.DoesNotContain("secret", id);
                    Assert.True(ComposerSessionDrag.IsLending(data));
                }
                finally
                {
                    drag?.Dispose();
                }

                Assert.False(ComposerSessionDrag.IsLending(data));
                Assert.Null(ComposerSessionDrag.Take(data));

                Select(grid, connect);
                Assert.Null(view.BeginSessionDrag(connect));
                Select(grid, notKept);
                Assert.Null(view.BeginSessionDrag(notKept));
                grid.SelectedItems.Add(post);
                Assert.Null(view.BeginSessionDrag(post));
                Select(grid, post);
                Assert.Null(view.BeginSessionDrag(get));
                Assert.Null(view.BeginSessionDrag(null));
                Select(grid, get);
                using (ComposerSessionDragToken single = view.BeginSessionDrag(get))
                {
                    Assert.NotNull(single);
                }

                Assert.False(CaptureConnectionView.IsPastDragThreshold(new Vector(
                    SystemParameters.MinimumHorizontalDragDistance - 1, SystemParameters.MinimumVerticalDragDistance - 1)));
                Assert.True(CaptureConnectionView.IsPastDragThreshold(
                    new Vector(SystemParameters.MinimumHorizontalDragDistance, 0)));
                Assert.True(CaptureConnectionView.IsPastDragThreshold(
                    new Vector(0, -SystemParameters.MinimumVerticalDragDistance)));
            });
        }

        // Dropping loads the copy made when the drag began, once and only while the drag lasts, without asking when nothing
        // was edited; it clears the previous response, and the row it came from stays as it is.
        [Fact]
        public Task DroppingADraggedRequest_LoadsTheCopyMadeWhenTheDragBegan_OnceAndOnlyWhileTheDragLasts() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("response"));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), Recording(model));
            try
            {
                ProxyConnectionModel first = Capture(
                    server.Port, "/first", "X-Source: first\r\n", "first body"u8.ToArray(), "POST", number: 4);
                ProxyConnectionModel second = Capture(server.Port, "/second", "X-Source: second\r\n", number: 5);
                string secondBefore = Describe(second);
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(first, SourceTab))
                {
                    // A running capture updates the row after the drag began; the drag keeps the request as it was.
                    first.UpdateFromDataRecord(new Record(new Dictionary<string, string>
                    {
                        [WebServiceCaptureMethod.FieldKey_RequestPath] = "/changed",
                        [WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] =
                            $"POST /changed HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nX-Source: changed\r\n",
                    }));
                    Assert.True(window.CanAcceptSession(drag.Data));
                    Assert.True(window.DropSession(drag.Data));
                    Assert.Equal(
                        Localization.GetLocalizedString("COMPOSER_DROPPED", SourceTab, 4L), Status(window));

                    Assert.False(window.CanAcceptSession(drag.Data));
                    Assert.False(window.DropSession(drag.Data));
                }

                Assert.Empty(questions);
                Assert.Equal(
                    ("POST", server.Origin + "/first", "first body"),
                    (Find<TextBox>(window, "MethodBox").Text, Url(window), Find<TextBox>(window, "BodyBox").Text));
                Assert.Contains("X-Source: first", Find<TextBox>(window, "HeadersBox").Text);

                await window.SendAsync();

                Assert.Single(questions);
                Assert.Equal(("/first", "first body"), (server.Requests[^1].Target, Encoding.UTF8.GetString(server.Requests[^1].Body)));
                Assert.Equal("Source tab #4, from the composer", Assert.Single(Replays(model).ProxyConnections).ReplaySource);
                Assert.NotNull(window.LastResult);

                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(second, SourceTab))
                {
                    Assert.True(window.DropSession(drag.Data));
                }

                Assert.Single(questions);
                Assert.Equal(server.Origin + "/second", Url(window));
                Assert.Null(window.LastResult);
                Assert.Null(Find<RequestResponseView>(window, "ResponseView").BodyBytes);

                ComposerSessionDragToken ended = ComposerSessionDrag.Begin(first, SourceTab);
                ended.Dispose();
                Assert.False(window.CanAcceptSession(ended.Data));
                Assert.False(window.DropSession(ended.Data));
                Assert.Equal(server.Origin + "/second", Url(window));
                Assert.Equal(secondBefore, Describe(second));
                Assert.Equal("/changed", first.Path);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Only a request dragged from XMAT is taken: text, links, files and ids that no drag of this XMAT lends are refused
        // without being read, opened or sent.
        [Fact]
        public Task DroppingTextLinksFilesOrAForeignId_ChangesNothing() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                string file = Path.Join(directory, "request.txt");
                File.WriteAllText(file, $"GET {server.Origin}/from-file HTTP/1.1\r\n\r\n");
                var link = new DataObject();
                link.SetData(DataFormats.UnicodeText, server.Origin + "/from-link");
                link.SetData("UniformResourceLocatorW", server.Origin + "/from-link");
                IDataObject[] drops =
                {
                    new DataObject(DataFormats.UnicodeText, $"GET {server.Origin}/from-text HTTP/1.1"),
                    link,
                    new DataObject(DataFormats.FileDrop, new[] { file }),
                    new DataObject(ComposerSessionDrag.Format, Guid.NewGuid().ToString("N")),
                };

                foreach (IDataObject data in drops)
                {
                    Assert.False(window.CanAcceptSession(data));
                    Assert.False(window.DropSession(data));
                    Assert.Equal(Localization.GetLocalizedString("COMPOSER_DROP_REFUSED"), Status(window));
                }

                Assert.Equal(string.Empty, Url(window));
                Assert.Equal(string.Empty, Find<TextBox>(window, "BodyBox").Text);
                Assert.Equal(0, server.Connections);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // A request that the user edited is replaced only once the user agrees. While the window asks about a send or sends,
        // nothing is dropped, and the drag still lends its request.
        [Fact]
        public Task DroppingOnAnEditedRequest_AsksFirst_AndNothingIsDroppedWhileTheWindowAsksOrSends() => _application.RunAsync(async () =>
        {
            var held = new TaskCompletionSource<LoopbackResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new LoopbackHttpServer(_ => held.Task);
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            bool agree = false;
            Action whileAsking = null;
            var window = RequestComposerWindow.ForNewRequest(client, message =>
            {
                questions.Add(message);
                whileAsking?.Invoke();
                return agree;
            });
            try
            {
                ProxyConnectionModel row = Capture(server.Port, "/dropped", number: 12);
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/typed";
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(row, SourceTab))
                {
                    Assert.False(window.DropSession(drag.Data));
                }

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_REPLACE_EDITED", SourceTab, 12L), Assert.Single(questions));
                Assert.Equal(server.Origin + "/typed", Url(window));

                agree = true;
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(row, SourceTab))
                {
                    Assert.True(window.DropSession(drag.Data));
                }

                Assert.Equal(2, questions.Count);
                Assert.Equal(server.Origin + "/dropped", Url(window));

                Task sending = window.SendAsync();
                await WaitUntilAsync(() => server.Requests.Count == 1);
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(Capture(server.Port, "/late", number: 13), SourceTab))
                {
                    Assert.False(window.CanAcceptSession(drag.Data));
                    Assert.False(window.DropSession(drag.Data));
                    Assert.Equal(Localization.GetLocalizedString("COMPOSER_DROP_BUSY"), Status(window));
                    Assert.True(ComposerSessionDrag.IsLending(drag.Data));
                }

                held.SetResult(LoopbackResponse.Ok());
                await sending.WaitAsync(Patience);
                Assert.Equal(server.Origin + "/dropped", Url(window));

                Find<TextBox>(window, "MethodBox").Text = "POST";
                agree = false;
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(Capture(server.Port, "/asking", number: 14), SourceTab))
                {
                    whileAsking = () => Assert.False(window.DropSession(drag.Data));
                    await window.SendAsync();
                    whileAsking = null;
                    Assert.True(ComposerSessionDrag.IsLending(drag.Data));
                }

                Assert.Equal(3, questions.Count);
                Assert.Single(server.Requests);
                Assert.Equal(server.Origin + "/dropped", Url(window));
            }
            finally
            {
                held.TrySetResult(LoopbackResponse.Ok());
                window.Close();
            }
        });

        // The batch adapter passes every send to the recorder as it ends and reports the count, but keeps only the last
        // exchange, with the outcome of each send.
        [Fact]
        public async Task BatchAdapter_PassesEverySendToTheRecorder_AndKeepsOnlyTheLastExchange()
        {
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok(request.Target));
            using var client = new RequestReplayClient();
            Assert.True(ReplaySequence.TryParse("1-3", out ReplaySequence sequence));
            ReplayPlanPreview plan = client.Preview(
                ReplaySource.FromTemplate(new RequestReplayDraft { Url = server.Origin + "/n/#" }, sequence), 0).Value;
            var recorder = new RecordingSink();
            var reported = new List<int>();

            RequestReplayBatchResult sent = await client.SendPlanAsync(
                plan, 0, confirmed: false, CancellationToken.None, recorder, new Reported(reported));

            Assert.True(sent.Result.Succeeded);
            Assert.Equal(new[] { "/n/1", "/n/2", "/n/3" }, recorder.Sends.Select(send => send.Exchange.RequestUri.PathAndQuery));
            Assert.Equal(new[] { 1, 2, 3 }, reported);
            Assert.Equal(3, sent.Sends);
            Assert.Same(recorder.Sends[^1].Exchange, sent.LastExchange);
            Assert.Same(recorder.Sends[^1].Result, sent.LastItem);
            Assert.Equal(new long?[] { 1, 2, 3 }, sent.Result.Value.Items.Select(item => item.Sequence));
        }

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        private static string Url(Window window) => Find<TextBox>(window, "UrlBox").Text;

        private static string[] Targets(LoopbackHttpServer server) =>
            server.Requests.Select(request => request.Target).ToArray();

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < Patience, "The condition did not hold in time.");
                await Task.Delay(10);
            }
        }

        // A captured request to the loopback port.
        private static ProxyConnectionModel Capture(
            int port,
            string path,
            string headers = "",
            byte[] requestBody = null,
            string method = "GET",
            long number = 7) =>
            new(number, "200", DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{port}", method, path,
                $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{headers}", requestBody ?? [],
                "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");

        private static string Describe(ProxyConnectionModel row) => string.Join(
            "|",
            row.RecordRowId,
            row.RequestNumber,
            row.Method,
            row.Path,
            row.RequestLineAndHeaders,
            Convert.ToBase64String(row.RequestBody),
            row.ResponseLineAndHeaders,
            Convert.ToBase64String(row.ResponseBody));

        // Records into the model's Replays tab, on this test's UI thread.
        private static ReplayRecordingTarget Recording(CaptureAppModel model)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(model, action => dispatcher.Invoke(action));
        }

        private static WebServiceDeviceCaptureController ControllerOf(ICaptureDeviceContext context) =>
            (WebServiceDeviceCaptureController)context.CaptureController;

        private static WebServiceDeviceCaptureController Replays(CaptureAppModel model) =>
            ControllerOf(model.GetOrAddReplayContext());

        // A read-only tab with these rows, as a capture loaded from a file adds one, whatever its name.
        private static ICaptureDeviceContext AddTab(CaptureAppModel model, string name, params ProxyConnectionModel[] rows)
        {
            var context = new CaptureDeviceContextModel(
                DeviceType.Unknown, name, CaptureType.WebProxy, true, null, hostListPolicy: model.HostListPolicy);
            foreach (ProxyConnectionModel row in rows)
            {
                ControllerOf(context).ProxyConnections.Add(row);
            }

            model.CaptureDeviceContexts.Add(context);
            return context;
        }

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }

        private static void Select(DataGrid grid, ProxyConnectionModel row)
        {
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(row);
        }

        private static string FixtureDirectory()
        {
            string directory = Path.Join(AppContext.BaseDirectory, "ComposerFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        // A send as ReplayOperation passes it to its sink.
        private static ReplayItemResult Sent() =>
            new(1, null, null, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null);

        private static ReplayExchange Answered() => new()
        {
            Outcome = ReplayOutcome.Completed,
            Request = new ReplayMessage(
                "GET / HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", "127.0.0.1:9") }, null, null),
            Response = new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()),
            RequestUri = new Uri("http://127.0.0.1:9/"),
            StatusCode = 200,
            Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        // A capture's row as a running capture updates it, with only the given fields.
        private sealed class Record(Dictionary<string, string> values) : IDataRecord
        {
            public int RowId => 99;

            public long Int(string fieldName) => 0;

            public string Str(string fieldName) => values.TryGetValue(fieldName, out string value) ? value : null;
        }

        private sealed class RecordingSink : IReplayExchangeSink
        {
            internal List<(ReplayItemResult Result, ReplayExchange Exchange)> Sends { get; } = new();

            public void Add(ReplayItemResult result, ReplayExchange exchange) => Sends.Add((result, exchange));
        }

        // Reports synchronously, in the order the sends end.
        private sealed class Reported(List<int> values) : IProgress<int>
        {
            public void Report(int value) => values.Add(value);
        }
    }
}
