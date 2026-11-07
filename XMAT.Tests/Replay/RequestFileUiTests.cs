// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations.Replay;
using XMAT.Operations.Storage;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The composer's request files (Slice I1): Open request, the parts that a file left out and the buttons that supply
    // them, and Save request as, with its question about captured content. Requests go to loopback servers only, files to
    // folders under the test output, and each test records into its own application model.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class RequestFileUiTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private static readonly string[] SupplyButtons =
        {
            "CompleteMethodButton", "CompleteUrlButton", "CompleteHeadersButton", "CompleteNoHeadersButton",
            "CompleteBodyButton", "CompleteEmptyBodyButton",
        };

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        public RequestFileUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static RequestFileUiTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // A template shows what it left out, with a button for each way to supply it. Until then nothing is sent, Copy as
        // cURL is unavailable and the content cannot be saved, whatever the editors show: typing supplies nothing.
        [Fact]
        public Task OpeningATemplate_ShowsWhatItLeftOut_AndNothingIsSentCopiedOrSavedWithContent() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            var copied = new List<string>();
            var published = new List<string>();
            window.CopyText = copied.Add;
            window.PublishRequestFile = (target, _, _) =>
            {
                published.Add(target);
                return PrivateFilePublication.Published;
            };
            try
            {
                string path = Path.Join(directory, "orders.xmat-request.json");
                File.WriteAllText(path, Template("POST"));
                window.ChooseRequestFile = () => path;

                await window.OpenRequestAsync();

                string left = Parts("COMPOSER_PART_URL", "COMPOSER_PART_HEADERS", "COMPOSER_PART_BODY");
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_OPENED_INCOMPLETE", "orders.xmat-request.json", left),
                    Status(window));
                Assert.Equal(("POST", "", "", ""), Editors(window));
                Assert.Equal(Visibility.Visible, Find<Border>(window, "OmissionsPanel").Visibility);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_OMITTED", left), Find<TextBlock>(window, "OmissionsText").Text);
                Assert.Equal(
                    new[] { false, true, true, true, true, true },
                    SupplyButtons.Select(name => Find<Button>(window, name).Visibility == Visibility.Visible));

                string incomplete = Localization.GetLocalizedString(
                    "COMPOSER_ERROR", Localization.GetLocalizedString("COMPOSER_INCOMPLETE", left));
                await window.SendAsync();
                Assert.Equal(incomplete, Status(window));

                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/orders";
                Find<TextBox>(window, "HeadersBox").Text = "X-Test: typed";
                Find<TextBox>(window, "BodyBox").Text = "typed";
                await window.SendAsync();
                Assert.Equal(incomplete, Status(window));

                var copy = Find<Button>(window, "CopyCurlButton");
                var script = Find<Button>(window, "SaveCurlButton");
                string notExported = Localization.GetLocalizedString("COMPOSER_CURL_INCOMPLETE", left);
                window.ChooseCurlScriptPath = () => throw new Xunit.Sdk.XunitException("Nothing is asked: the export is not available.");
                Assert.False(copy.IsEnabled);
                Assert.Equal(notExported, copy.ToolTip);
                Assert.False(script.IsEnabled);
                Assert.Equal(notExported, script.ToolTip);
                await window.CopyAsCurlAsync();
                Assert.Equal(notExported, Status(window));
                await window.SaveCurlScriptAsync();
                Assert.Equal(notExported, Status(window));

                var questions = new List<RequestSaveQuestion>();
                window.ChooseSavePath = () => Path.Join(directory, "content.xmat-request.json");
                window.ChooseSaveContent = question =>
                {
                    questions.Add(question);
                    return new RequestSaveChoice(RequestFileProfile.Content);
                };
                await window.SaveRequestAsync();
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVE_INCOMPLETE", left), Status(window));
                Assert.Equal(new RequestOmissions(false, true, true, true), Assert.Single(questions).Pending);

                Assert.Empty(copied);
                Assert.Empty(published);
                Assert.Equal(0, server.Connections);
                Assert.Equal(new[] { "orders.xmat-request.json" }, Directory.GetFiles(directory).Select(Path.GetFileName));
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // Each part is supplied by its own button, as the editors show it then; Clear body supplies nothing. The body is made
        // into bytes off the UI thread. Once every part is supplied, the request is copied and sent as supplied, and its body
        // stays text.
        [Fact]
        public Task EachPart_IsSuppliedByItsButtonOnly_AndThenSentAsSupplied() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("done"));
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true));
            var copied = new List<string>();
            var decodedOnUiThread = new List<bool>();
            window.CopyText = copied.Add;
            window.BodyBytesOf = (draft, token) =>
            {
                decodedOnUiThread.Add(window.Dispatcher.CheckAccess());
                return RequestComposerWindow.BodyBytesFor(draft, token);
            };
            try
            {
                string path = Path.Join(directory, "blank.xmat-request.json");
                File.WriteAllText(path, Template(null));
                window.ChooseRequestFile = () => path;
                await window.OpenRequestAsync();
                Find<TextBox>(window, "MethodBox").Text = "PATCH";
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/items/7";
                Find<TextBox>(window, "HeadersBox").Text = "X-Test: supplied";
                Find<TextBox>(window, "BodyBox").Text = "cleared";
                window.ClearBody();
                Find<TextBox>(window, "BodyBox").Text = "supplied body";
                string all = Parts("COMPOSER_PART_METHOD", "COMPOSER_PART_URL", "COMPOSER_PART_HEADERS", "COMPOSER_PART_BODY");
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_OMITTED", all), Find<TextBlock>(window, "OmissionsText").Text);

                Click(window, "CompleteMethodButton");

                Assert.Equal(
                    Supplied("COMPOSER_PART_METHOD") + " " + Localization.GetLocalizedString(
                        "COMPOSER_STILL_OMITTED", Parts("COMPOSER_PART_URL", "COMPOSER_PART_HEADERS", "COMPOSER_PART_BODY")),
                    Status(window));
                Assert.Equal(Visibility.Collapsed, Find<Button>(window, "CompleteMethodButton").Visibility);
                Click(window, "CompleteUrlButton");
                Click(window, "CompleteHeadersButton");
                Assert.False(Find<Button>(window, "CopyCurlButton").IsEnabled);
                await window.SendAsync();
                Assert.Equal(0, server.Connections);

                await ClickAsync(window, "CompleteBodyButton");

                Assert.Equal(new[] { false }, decodedOnUiThread);
                Assert.Equal(
                    Supplied("COMPOSER_PART_BODY") + " " + Localization.GetLocalizedString("COMPOSER_ALL_SUPPLIED"),
                    Status(window));
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "OmissionsPanel").Visibility);
                Assert.Equal(("PATCH", server.Origin + "/items/7", "X-Test: supplied", "supplied body"), Editors(window));
                Assert.False(Find<CheckBox>(window, "Base64Box").IsChecked == true);
                Assert.True(Find<Button>(window, "CopyCurlButton").IsEnabled);
                await window.CopyAsCurlAsync();
                Assert.Contains(server.Origin + "/items/7", Assert.Single(copied));

                await window.SendAsync();

                LoopbackRequest sent = Assert.Single(server.Requests);
                Assert.Equal(
                    ("PATCH", "/items/7", "supplied", "supplied body"),
                    (sent.Method, sent.Target, sent.Header("X-Test"), Encoding.UTF8.GetString(sent.Body)));
                Assert.Single(questions);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // No headers and Empty body supply nothing, explicitly: what the editors held, an uploaded file included, is not
        // sent. Upload File alone does not supply the body.
        [Fact]
        public Task NoHeadersAndEmptyBody_SupplyNothing_InPlaceOfWhatTheEditorsHeld() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, _ => true);
            try
            {
                string path = Path.Join(directory, "post.xmat-request.json");
                string upload = Path.Join(directory, "upload.bin");
                File.WriteAllText(path, Template("POST"));
                File.WriteAllBytes(upload, new byte[] { 0, 1, 2, 255 });
                window.ChooseRequestFile = () => path;
                window.ChooseBodyFile = () => upload;
                await window.OpenRequestAsync();
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/empty";
                Find<TextBox>(window, "HeadersBox").Text = "X-Typed: never sent\r\nnot a header";

                await window.UploadBodyAsync();

                Assert.Equal(Visibility.Visible, Find<TextBlock>(window, "BodyFileText").Visibility);
                Assert.Equal(
                    Localization.GetLocalizedString(
                        "COMPOSER_OMITTED", Parts("COMPOSER_PART_URL", "COMPOSER_PART_HEADERS", "COMPOSER_PART_BODY")),
                    Find<TextBlock>(window, "OmissionsText").Text);

                Click(window, "CompleteUrlButton");
                Click(window, "CompleteNoHeadersButton");
                Assert.Equal("", Find<TextBox>(window, "HeadersBox").Text);
                await ClickAsync(window, "CompleteEmptyBodyButton");

                Assert.Equal(
                    Supplied("COMPOSER_PART_BODY") + " " + Localization.GetLocalizedString("COMPOSER_ALL_SUPPLIED"),
                    Status(window));
                Assert.Equal(Visibility.Collapsed, Find<TextBlock>(window, "BodyFileText").Visibility);
                Assert.Equal(("POST", server.Origin + "/empty", "", ""), Editors(window));

                await window.SendAsync();

                LoopbackRequest sent = Assert.Single(server.Requests);
                Assert.Equal(("POST", "/empty"), (sent.Method, sent.Target));
                Assert.Null(sent.Header("X-Typed"));
                Assert.Empty(sent.Body);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // A part that is not valid is not supplied, and the status says why; a body in Base64 is supplied as its bytes and
        // stays Base64.
        [Fact]
        public Task SupplyButtons_RefuseAPartThatIsNotValid() => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "not-read.xmat-request.json");
            window.ReadRequestFile = (_, _) => Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(Template(null))));
            try
            {
                await window.OpenRequestAsync();
                string notSupplied = Localization.GetLocalizedString("COMPOSER_SUPPLY_FAILED", "");

                foreach (string method in new[] { "", "CONNECT", "GET POST", new string('M', 65) })
                {
                    Find<TextBox>(window, "MethodBox").Text = method;
                    Click(window, "CompleteMethodButton");
                    Assert.Equal(Localization.GetLocalizedString("COMPOSER_SUPPLY_METHOD_INVALID"), Status(window));
                }

                foreach (string url in new[] { "", "/relative", "ftp://127.0.0.1/file", "http://user:secret@127.0.0.1/" })
                {
                    Find<TextBox>(window, "UrlBox").Text = url;
                    Click(window, "CompleteUrlButton");
                    Assert.Equal(Localization.GetLocalizedString("COMPOSER_SUPPLY_URL_INVALID"), Status(window));
                }

                Find<TextBox>(window, "HeadersBox").Text = "not a header";
                Click(window, "CompleteHeadersButton");
                Assert.StartsWith(notSupplied, Status(window));

                Find<CheckBox>(window, "Base64Box").IsChecked = true;
                Find<TextBox>(window, "BodyBox").Text = "%%%";
                await ClickAsync(window, "CompleteBodyButton");
                Assert.StartsWith(notSupplied, Status(window));
                Assert.Equal(
                    new[] { true, true, true, true, true, true },
                    SupplyButtons.Select(name => Find<Button>(window, name).Visibility == Visibility.Visible));

                Find<TextBox>(window, "BodyBox").Text = "dGV4dA==";
                await ClickAsync(window, "CompleteBodyButton");

                Assert.Equal(
                    Supplied("COMPOSER_PART_BODY") + " " + Localization.GetLocalizedString(
                        "COMPOSER_STILL_OMITTED", Parts("COMPOSER_PART_METHOD", "COMPOSER_PART_URL", "COMPOSER_PART_HEADERS")),
                    Status(window));
                Assert.True(Find<CheckBox>(window, "Base64Box").IsChecked == true);
                Assert.Equal("dGV4dA==", Find<TextBox>(window, "BodyBox").Text);
            }
            finally
            {
                window.Close();
            }
        });

        // Opening a file asks before it replaces an edited request, and a declined file changes nothing. An opened file sets
        // the HTTP version, turns Connect to another endpoint and Follow same-origin redirects off, and is known by its name
        // only: in the status, and in the Replays tab, which records its sends as coming from it.
        [Fact]
        public Task OpeningAFile_AsksBeforeReplacingEdits_ResetsTheTransport_AndKeepsOnlyTheFileName() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("opened"));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            bool replace = false;
            var window = RequestComposerWindow.ForNewRequest(
                client,
                message =>
                {
                    questions.Add(message);
                    return replace;
                },
                Recording(model));
            try
            {
                string path = Path.Join(directory, "saved.xmat-request.json");
                File.WriteAllText(
                    path,
                    RequestFileJson("content", "GET", server.Origin + "/from-file?page=2", "1.1", "[[\"X-From\", \"file\"]]", ""));
                window.ChooseRequestFile = () => path;
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/edited";
                Find<ComboBox>(window, "HttpVersionBox").SelectedIndex = 2;
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = "127.0.0.2:9443";
                Find<CheckBox>(window, "FollowRedirectsBox").IsChecked = true;
                Find<ComboBox>(window, "RedirectLimitBox").SelectedItem = 3;

                await window.OpenRequestAsync();

                // The question's English wording is pinned here, once; elsewhere it is read from en-us.
                Assert.Equal(
                    "Replace the current request with saved.xmat-request.json? Edits to the request and its transport " +
                        "options will be lost.",
                    Assert.Single(questions));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_OPEN_CANCELLED"), Status(window));
                Assert.Equal(("GET", "http://127.0.0.1:9/edited", "", ""), Editors(window));
                Assert.Equal(2, Find<ComboBox>(window, "HttpVersionBox").SelectedIndex);
                Assert.True(Find<CheckBox>(window, "ConnectToBox").IsChecked == true);

                replace = true;
                await window.OpenRequestAsync();

                Assert.Equal(2, questions.Count);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_OPENED", "saved.xmat-request.json"), Status(window));
                Assert.Equal(("GET", server.Origin + "/from-file?page=2", "X-From: file", ""), Editors(window));
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "OmissionsPanel").Visibility);
                Assert.Equal(
                    ReplayHttpVersion.Http11,
                    Assert.IsType<ComboBoxItem>(Find<ComboBox>(window, "HttpVersionBox").SelectedItem).Tag);
                Assert.False(Find<CheckBox>(window, "ConnectToBox").IsChecked == true);
                Assert.Equal("", Find<TextBox>(window, "ConnectToText").Text);
                Assert.False(Find<CheckBox>(window, "FollowRedirectsBox").IsChecked == true);
                Assert.Equal(ReplayOptions.MaxRedirectLimit, Find<ComboBox>(window, "RedirectLimitBox").SelectedItem);

                await window.SendAsync();

                Assert.Equal(2, questions.Count);
                LoopbackRequest sent = Assert.Single(server.Requests);
                Assert.Equal(("/from-file?page=2", "file", "HTTP/1.1"), (sent.Target, sent.Header("X-From"), sent.Version));
                var replays = (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;
                ProxyConnectionModel row = Assert.Single(replays.ProxyConnections);
                Assert.Equal(ReplaySourceKind.RequestFile, row.Replay.Kind);
                Assert.Equal(
                    Localization.GetLocalizedString("REPLAY_SOURCE_REQUEST_FILE", "saved.xmat-request.json"), row.ReplaySource);
                Assert.DoesNotContain(directory, row.ReplaySource);
                Assert.DoesNotContain(directory, Status(window));
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
                Directory.Delete(directory, recursive: true);
            }
        });

        // Supplying a part that a request file left out is an edit, even as the empty part that the editors already showed,
        // and so is changing a transport option, which opening a file resets: choosing any other HTTP version, turning on
        // Connect to another endpoint or Follow same-origin redirects, or changing the endpoint or the most redirects, also
        // while the option is off. Opening another file asks first, and declining keeps everything as it was; accepting
        // resets the transport options. A file just opened is not edited: opening one again asks nothing.
        [Theory]
        [InlineData("1.1", "1.1", "headers")]
        [InlineData("1.1", "1.1", "body")]
        [InlineData("auto", "1.1", null)]
        [InlineData("auto", "2", null)]
        [InlineData("1.1", "auto", null)]
        [InlineData("1.1", "2", null)]
        [InlineData("2", "auto", null)]
        [InlineData("2", "1.1", null)]
        [InlineData("1.1", "1.1", "connect")]
        [InlineData("1.1", "1.1", "endpoint")]
        [InlineData("1.1", "1.1", "follow")]
        [InlineData("1.1", "1.1", "limit")]
        public Task SupplyingAPartOrChangingATransportOption_IsAnEdit_ThatOpeningAFileAsksAbout(
            string opened, string chosen, string edit) => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            bool replace = false;
            var window = RequestComposerWindow.ForNewRequest(
                client,
                message =>
                {
                    questions.Add(message);
                    return replace;
                });
            try
            {
                // The other file has another HTTP version than either, so that opening it changes the version too.
                string third = new[] { "auto", "1.1", "2" }.First(version => version != opened && version != chosen);
                string template = Path.Join(directory, "template.xmat-request.json");
                string other = Path.Join(directory, "other.xmat-request.json");
                File.WriteAllText(template, RequestFileJson("template", "POST", null, opened, null, null));
                File.WriteAllText(other, RequestFileJson("content", "GET", "http://127.0.0.1:9/other", third, "[]", ""));
                window.ChooseRequestFile = () => template;
                await window.OpenRequestAsync();
                Assert.Equal((VersionOf(opened), false, "", false, ReplayOptions.MaxRedirectLimit), Transport(window));
                ChooseVersion(window, chosen);
                switch (edit)
                {
                    case "headers":
                        Click(window, "CompleteNoHeadersButton");
                        break;
                    case "body":
                        await ClickAsync(window, "CompleteEmptyBodyButton");
                        break;
                    case not null:
                        ChangeTransport(window, edit);
                        break;
                }

                // Only this edit differs from the file just opened; the editors show what they showed.
                var edited = (VersionOf(chosen), edit == "connect", edit == "endpoint" ? "127.0.0.2:9443" : "",
                    edit == "follow", edit == "limit" ? 3 : ReplayOptions.MaxRedirectLimit);
                string omitted = Localization.GetLocalizedString("COMPOSER_OMITTED", edit switch
                {
                    "headers" => Parts("COMPOSER_PART_URL", "COMPOSER_PART_BODY"),
                    "body" => Parts("COMPOSER_PART_URL", "COMPOSER_PART_HEADERS"),
                    _ => Parts("COMPOSER_PART_URL", "COMPOSER_PART_HEADERS", "COMPOSER_PART_BODY"),
                });
                Assert.Equal(edited, Transport(window));
                Assert.Equal(("POST", "", "", ""), Editors(window));
                Assert.False(Find<CheckBox>(window, "Base64Box").IsChecked == true);
                Assert.Equal(omitted, Find<TextBlock>(window, "OmissionsText").Text);
                window.ChooseRequestFile = () => other;

                await window.OpenRequestAsync();

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_REPLACE_EDITED_FILE", "other.xmat-request.json"),
                    Assert.Single(questions));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_OPEN_CANCELLED"), Status(window));
                Assert.Equal(("POST", "", "", ""), Editors(window));
                Assert.Equal(omitted, Find<TextBlock>(window, "OmissionsText").Text);
                Assert.Equal(edited, Transport(window));

                replace = true;
                await window.OpenRequestAsync();

                Assert.Equal(2, questions.Count);
                Assert.Equal(("GET", "http://127.0.0.1:9/other", "", ""), Editors(window));
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "OmissionsPanel").Visibility);
                Assert.Equal((VersionOf(third), false, "", false, ReplayOptions.MaxRedirectLimit), Transport(window));

                await window.OpenRequestAsync();

                Assert.Equal(2, questions.Count);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_OPENED", "other.xmat-request.json"), Status(window));
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // A drop keeps the transport options as they are, so changing them asks nothing there, and they stay as chosen. They
        // stay edits that opening a file asks about, since it resets them: declining keeps them, and the answer is not a
        // permission to send, which the send still asks for. A part that a request file left out and that was supplied, even
        // as the empty part that the editors already showed, is an edit that a drop loses: dropping asks first, and declining
        // keeps it supplied.
        [Fact]
        public Task DroppingARequest_KeepsTheTransportWithoutAsking_ButAsksBeforeReplacingASuppliedPart() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            bool replace = false;
            var window = RequestComposerWindow.ForNewRequest(
                client,
                message =>
                {
                    questions.Add(message);
                    return replace;
                });
            var capture = new ProxyConnectionModel(12, "200", DateTime.UtcNow, TimeSpan.Zero, "https", "127.0.0.1:9", "GET",
                "/dropped", "GET /dropped HTTP/1.1\r\nHost: 127.0.0.1:9\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
            try
            {
                string path = Path.Join(directory, "template.xmat-request.json");
                File.WriteAllText(path, Template("POST"));
                window.ChooseRequestFile = () => path;
                ChooseVersion(window, "1.1");
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = "127.0.0.2:9443";
                Find<CheckBox>(window, "FollowRedirectsBox").IsChecked = true;
                Find<ComboBox>(window, "RedirectLimitBox").SelectedItem = 3;
                var chosen = (ReplayHttpVersion.Http11, true, "127.0.0.2:9443", true, 3);
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(capture, "Source tab"))
                {
                    Assert.True(window.DropSession(drag.Data));
                }

                Assert.Empty(questions);
                Assert.Equal("https://127.0.0.1:9/dropped", Find<TextBox>(window, "UrlBox").Text);
                Assert.Equal(chosen, Transport(window));

                await window.OpenRequestAsync();

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_REPLACE_EDITED_FILE", "template.xmat-request.json"),
                    Assert.Single(questions));
                Assert.Equal("https://127.0.0.1:9/dropped", Find<TextBox>(window, "UrlBox").Text);
                Assert.Equal(chosen, Transport(window));

                await window.SendAsync();

                Assert.Equal(2, questions.Count);
                Assert.Contains(
                    Localization.GetLocalizedString("COMPOSER_CONFIRM_CONNECT_TO", "127.0.0.2:9443", "https://127.0.0.1:9"),
                    questions[1]);

                replace = true;
                await window.OpenRequestAsync();

                Assert.Equal(3, questions.Count);
                Assert.Equal((ReplayHttpVersion.Auto, false, "", false, ReplayOptions.MaxRedirectLimit), Transport(window));

                replace = false;
                Click(window, "CompleteNoHeadersButton");
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(capture, "Source tab"))
                {
                    Assert.False(window.DropSession(drag.Data));
                }

                Assert.Equal(4, questions.Count);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_REPLACE_EDITED", "Source tab", 12L), questions[3]);
                Assert.Equal(("POST", "", "", ""), Editors(window));
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_OMITTED", Parts("COMPOSER_PART_URL", "COMPOSER_PART_BODY")),
                    Find<TextBlock>(window, "OmissionsText").Text);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // A drop keeps each transport option as it is: changing any one of them alone asks nothing there, and it stays as
        // chosen. It stays an edit that opening a file asks about, since opening resets it, and declining keeps it.
        [Theory]
        [InlineData("version")]
        [InlineData("connect")]
        [InlineData("endpoint")]
        [InlineData("follow")]
        [InlineData("limit")]
        public Task DroppingARequest_KeepsEachTransportOption_AsAnEditThatOpeningAFileAsksAbout(string option) => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, false));
            var capture = new ProxyConnectionModel(12, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "GET",
                "/dropped", "GET /dropped HTTP/1.1\r\nHost: 127.0.0.1:9\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
            window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "other.xmat-request.json");
            window.ReadRequestFile = (_, _) =>
                Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(Template("GET"))));
            try
            {
                ChangeTransport(window, option);
                var changed = (option == "version" ? ReplayHttpVersion.Http11 : ReplayHttpVersion.Auto, option == "connect",
                    option == "endpoint" ? "127.0.0.2:9443" : "", option == "follow",
                    option == "limit" ? 3 : ReplayOptions.MaxRedirectLimit);
                Assert.Equal(changed, Transport(window));
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(capture, "Source tab"))
                {
                    Assert.True(window.DropSession(drag.Data));
                }

                Assert.Empty(questions);
                Assert.Equal("http://127.0.0.1:9/dropped", Find<TextBox>(window, "UrlBox").Text);
                Assert.Equal(changed, Transport(window));

                await window.OpenRequestAsync();

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_REPLACE_EDITED_FILE", "other.xmat-request.json"),
                    Assert.Single(questions));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_OPEN_CANCELLED"), Status(window));
                Assert.Equal("http://127.0.0.1:9/dropped", Find<TextBox>(window, "UrlBox").Text);
                Assert.Equal(changed, Transport(window));
            }
            finally
            {
                window.Close();
            }
        });

        // A file that is not opened says why, and the request in the editors stays as it was; no file chosen says nothing.
        [Theory]
        [InlineData("missing", "COMPOSER_OPEN_NOT_FOUND")]
        [InlineData("too large", "COMPOSER_OPEN_TOO_LARGE")]
        [InlineData("not JSON", "COMPOSER_OPEN_INVALID")]
        [InlineData("another schema", "COMPOSER_OPEN_UNSUPPORTED")]
        [InlineData("unreadable", "COMPOSER_OPEN_UNREADABLE")]
        [InlineData("unknown", "COMPOSER_OPEN_FAILED")]
        [InlineData("none chosen", null)]
        public Task AFileThatIsNotOpened_SaysWhy_AndChangesNothing(string kind, string key) => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            try
            {
                string path = Path.Join(directory, "request.xmat-request.json");
                switch (kind)
                {
                    case "too large":
                        using (var file = new FileStream(path, FileMode.CreateNew))
                        {
                            file.SetLength(RequestFileReader.MaxFileBytes + 1);
                        }

                        break;
                    case "not JSON":
                        File.WriteAllText(path, "{ not json");
                        break;
                    case "another schema":
                        File.WriteAllText(path, Template("GET").Replace("xmat-request/1", "xmat-request/2"));
                        break;
                    case "unreadable":
                        window.ReadRequestFile = (_, _) =>
                            Task.FromResult(RequestFileReadResult.Failed(RequestFileCodes.Unreadable));
                        break;
                    case "unknown":
                        window.ReadRequestFile = (_, _) => Task.FromResult(RequestFileReadResult.Failed("replay.request-file.new"));
                        break;
                }

                window.ChooseRequestFile = () => kind == "none chosen" ? null : path;
                Find<TextBox>(window, "MethodBox").Text = "PUT";
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/kept";
                Find<TextBox>(window, "BodyBox").Text = "kept";
                string status = Status(window);

                await window.OpenRequestAsync();

                Assert.Equal(key == null ? status : Localization.GetLocalizedString(key), Status(window));
                Assert.Equal(("PUT", "http://127.0.0.1:9/kept", "", "kept"), Editors(window));
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "OmissionsPanel").Visibility);
                Assert.True(Find<Button>(window, "OpenRequestButton").IsEnabled);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // A file is read, and a request file published, off the UI thread, while the window is busy; Cancel request stops
        // the read and closing the window stops the save, and neither changes the request.
        [Fact]
        public Task ReadingAndSaving_RunOffTheUiThread_AndStopWhenCancelledOrClosed() => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            bool closed = false;
            try
            {
                var reading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "never-read.xmat-request.json");
                window.ReadRequestFile = async (_, token) =>
                {
                    reading.TrySetResult(window.Dispatcher.CheckAccess());
                    await Task.Delay(Timeout.Infinite, token);
                    return null;
                };
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/kept";

                Task opening = window.OpenRequestAsync();

                Assert.False(await reading.Task.WaitAsync(Patience));
                Assert.False(Find<Grid>(window, "RequestEditor").IsEnabled);
                Assert.False(Find<Button>(window, "OpenRequestButton").IsEnabled);
                Assert.False(Find<Button>(window, "SaveRequestButton").IsEnabled);
                Assert.True(Find<Button>(window, "CancelSendButton").IsEnabled);
                window.CancelSend();
                await opening.WaitAsync(Patience);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_OPEN_CANCELLED"), Status(window));
                Assert.Equal("http://127.0.0.1:9/kept", Find<TextBox>(window, "UrlBox").Text);
                Assert.True(Find<Button>(window, "SaveRequestButton").IsEnabled);

                var publishing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool stopped = false;
                window.ChooseSavePath = () => Path.Join(AppContext.BaseDirectory, "never-written.xmat-request.json");
                window.ChooseSaveContent = _ => new RequestSaveChoice(RequestFileProfile.Template);
                window.PublishRequestFile = (_, _, token) =>
                {
                    publishing.TrySetResult(window.Dispatcher.CheckAccess());
                    stopped = token.WaitHandle.WaitOne(Patience);
                    return PrivateFilePublication.Cancelled;
                };

                Task saving = window.SaveRequestAsync();

                Assert.False(await publishing.Task.WaitAsync(Patience));
                Assert.False(Find<Button>(window, "SendButton").IsEnabled);
                window.Close();
                closed = true;
                await saving.WaitAsync(Patience);
                Assert.True(stopped);
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }
            }
        });

        // Checking a request before saving it, which can read a body of 64 MiB, runs off the UI thread, with the real checks,
        // while the window is busy: a valid request is recognized and saved, an invalid one refused before anything is
        // asked. Closing the window stops the check, and nothing is asked or saved.
        [Fact]
        public Task CheckingARequestBeforeSaving_RunsOffTheUiThread_AndClosingStopsIt() => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            bool closed = false;
            try
            {
                var checkedOnUiThread = new List<bool>();
                var questions = new List<RequestSaveQuestion>();
                window.InspectRequest = (draft, token) =>
                {
                    checkedOnUiThread.Add(window.Dispatcher.CheckAccess());
                    return RequestComposerWindow.InspectForSaving(draft, token);
                };
                window.ChooseSavePath = () => Path.Join(AppContext.BaseDirectory, "checked.xmat-request.json");
                window.ChooseSaveContent = question =>
                {
                    questions.Add(question);
                    return new RequestSaveChoice(RequestFileProfile.Template);
                };
                window.PublishRequestFile = (_, _, _) => PrivateFilePublication.Published;
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/checked?access_token=canary-token";
                Find<TextBox>(window, "HeadersBox").Text = "Authorization: Bearer canary-credential";

                await window.SaveRequestAsync();

                Assert.Equal(new[] { false }, checkedOnUiThread);
                RequestSaveQuestion asked = Assert.Single(questions);
                Assert.Equal(new[] { "Authorization" }, asked.Recognized.HeaderNames);
                Assert.Equal(new[] { "access_token" }, asked.Recognized.QueryParameterNames);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_SAVED_TEMPLATE", "checked.xmat-request.json"), Status(window));

                Find<TextBox>(window, "MethodBox").Text = "NOT A METHOD";
                await window.SaveRequestAsync();

                Assert.Equal(new[] { false, false }, checkedOnUiThread);
                Assert.StartsWith(Localization.GetLocalizedString("COMPOSER_SAVE_INVALID", ""), Status(window));
                Assert.Single(questions);

                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool stopped = false;
                Find<TextBox>(window, "MethodBox").Text = "GET";
                window.ChooseSavePath = () => throw new Xunit.Sdk.XunitException("A closed window asks nothing.");
                window.InspectRequest = (_, token) =>
                {
                    started.TrySetResult(window.Dispatcher.CheckAccess());
                    stopped = token.WaitHandle.WaitOne(Patience);
                    token.ThrowIfCancellationRequested();
                    return null;
                };

                Task saving = window.SaveRequestAsync();

                Assert.False(await started.Task.WaitAsync(Patience));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVE_CHECKING"), Status(window));
                Assert.False(Find<Grid>(window, "RequestEditor").IsEnabled);
                Assert.True(Find<Button>(window, "CancelSendButton").IsEnabled);
                window.Close();
                closed = true;
                await saving.WaitAsync(Patience);
                Assert.True(stopped);
                Assert.Single(questions);
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }
            }
        });

        // Use this body makes the editor's text into bytes off the UI thread while the window is busy. Cancel request stops
        // it and supplies nothing, and so does closing the window.
        [Theory]
        [InlineData("cancel")]
        [InlineData("close")]
        public Task SupplyingABody_RunsOffTheUiThread_AndStopsWhenCancelledOrClosed(string stop) => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            bool closed = false;
            try
            {
                window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "not-read.xmat-request.json");
                window.ReadRequestFile = (_, _) =>
                    Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(Template("POST"))));
                await window.OpenRequestAsync();
                Find<TextBox>(window, "BodyBox").Text = "never supplied";
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool stopped = false;
                window.BodyBytesOf = (_, token) =>
                {
                    started.TrySetResult(window.Dispatcher.CheckAccess());
                    stopped = token.WaitHandle.WaitOne(Patience);
                    token.ThrowIfCancellationRequested();
                    return null;
                };

                Task supplying = window.SupplyBodyAsync(false);

                Assert.False(await started.Task.WaitAsync(Patience));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SUPPLYING_BODY"), Status(window));
                Assert.False(Find<Border>(window, "OmissionsPanel").IsEnabled);
                Assert.False(Find<Button>(window, "SaveRequestButton").IsEnabled);
                if (stop == "close")
                {
                    window.Close();
                    closed = true;
                }
                else
                {
                    window.CancelSend();
                }

                await supplying.WaitAsync(Patience);
                Assert.True(stopped);
                if (!closed)
                {
                    Assert.Equal(Localization.GetLocalizedString("COMPOSER_SUPPLY_CANCELLED"), Status(window));
                    Assert.Equal(Visibility.Visible, Find<Button>(window, "CompleteBodyButton").Visibility);
                    Assert.True(Find<Border>(window, "OmissionsPanel").IsEnabled);
                }
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }
            }
        });

        // Cancel request can come after the work off the UI thread last checked for it, which then still returns what it
        // read, made or checked. Such a result is not used, and nothing is asked: reading a request file opens nothing, an
        // upload leaves the body as it was, Use this body supplies nothing, and checking a request before saving it saves
        // nothing. The status says that it was cancelled.
        [Theory]
        [InlineData("open", "COMPOSER_OPEN_CANCELLED")]
        [InlineData("upload", "COMPOSER_UPLOAD_CANCELLED")]
        [InlineData("supply", "COMPOSER_SUPPLY_CANCELLED")]
        [InlineData("save", "COMPOSER_SAVE_CANCELLED")]
        public Task AResultThatArrivesAfterCancel_IsNotUsed_AndNothingIsAsked(string work, string key) => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            try
            {
                if (work == "supply")
                {
                    window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "not-read.xmat-request.json");
                    window.ReadRequestFile = (_, _) =>
                        Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(Template("POST"))));
                    await window.OpenRequestAsync();
                }

                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/kept";
                Find<TextBox>(window, "BodyBox").Text = "kept";
                var editors = Editors(window);
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool late = false;
                window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "late.xmat-request.json");
                window.ReadRequestFile = async (_, token) =>
                {
                    started.TrySetResult();
                    await UntilCancelledAsync(token);
                    late = true;
                    return RequestFileReader.Read(Encoding.UTF8.GetBytes(
                        RequestFileJson("content", "PUT", "http://127.0.0.1:9/late", "auto", "[]", "bGF0ZQ==")));
                };
                window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "late.bin");
                window.ReadBodyFile = async (_, token) =>
                {
                    started.TrySetResult();
                    await UntilCancelledAsync(token);
                    late = true;
                    return new ReplayBodyRead(new byte[] { 0, 1, 2 }, null);
                };
                window.BodyBytesOf = (_, token) =>
                {
                    started.TrySetResult();
                    late = token.WaitHandle.WaitOne(Patience);
                    return new byte[] { 0, 1, 2 };
                };
                window.InspectRequest = (_, token) =>
                {
                    started.TrySetResult();
                    late = token.WaitHandle.WaitOne(Patience);
                    return new RequestFileRecognized(Array.Empty<string>(), Array.Empty<string>(), 4);
                };
                window.ChooseSavePath = () => throw new Xunit.Sdk.XunitException("Nothing is asked after Cancel request.");
                window.ChooseSaveContent = _ => throw new Xunit.Sdk.XunitException("Nothing is asked after Cancel request.");
                window.PublishRequestFile = (_, _, _) => throw new Xunit.Sdk.XunitException("Nothing is saved after Cancel request.");

                Task working = work switch
                {
                    "open" => window.OpenRequestAsync(),
                    "upload" => window.UploadBodyAsync(),
                    "supply" => window.SupplyBodyAsync(false),
                    _ => window.SaveRequestAsync(),
                };

                await started.Task.WaitAsync(Patience);
                Assert.True(Find<Button>(window, "CancelSendButton").IsEnabled);
                window.CancelSend();
                await working.WaitAsync(Patience);

                Assert.True(late);
                Assert.Equal(Localization.GetLocalizedString(key), Status(window));
                Assert.Equal(editors, Editors(window));
                Assert.Equal(Visibility.Visible, Find<TextBox>(window, "BodyBox").Visibility);
                Assert.Equal(
                    work == "supply" ? Visibility.Visible : Visibility.Collapsed,
                    Find<Button>(window, "CompleteBodyButton").Visibility);
                Assert.True(Find<Button>(window, "OpenRequestButton").IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        // A body from a request file is shown in the editor up to 64 KiB. A larger one, text or binary, is not: the editor
        // would hold its text or Base64, made on the UI thread. The composer keeps its exact bytes, shows the file's name and
        // the body's size, sends and saves exactly those bytes, and does not copy it as cURL. Upload File replaces it, and
        // Clear body brings back the editor.
        [Theory]
        [InlineData("text")]
        [InlineData("binary")]
        public Task ABodyLargerThan64KiB_IsKeptAsItsBytes_ShownByItsSize_AndSentAndSavedExactly(string kind) => _application.RunAsync(async () =>
        {
            const int MostShown = 64 * 1024;
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true));
            var copied = new List<string>();
            var saved = new List<RequestFileContent>();
            window.CopyText = copied.Add;
            window.ChooseSavePath = () => Path.Join(AppContext.BaseDirectory, "saved-large.xmat-request.json");
            window.ChooseSaveContent = _ => new RequestSaveChoice(RequestFileProfile.Content);
            window.PublishRequestFile = (_, write, _) =>
            {
                using var stream = new MemoryStream();
                write(stream);
                saved.Add(RequestFileReader.Read(stream.ToArray()).Content);
                return PrivateFilePublication.Published;
            };
            try
            {
                byte[] Bytes(int length) => kind == "text"
                    ? Encoding.ASCII.GetBytes(new string('a', length))
                    : Enumerable.Range(0, length).Select(index => (byte)index).ToArray();
                byte[] shown = Bytes(MostShown);
                byte[] large = Bytes(MostShown + 1);
                byte[] inFile = shown;
                window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "large.xmat-request.json");
                window.ReadRequestFile = (_, _) => Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(
                    RequestFileJson("content", "POST", server.Origin + "/large", "auto", "[]", Convert.ToBase64String(inFile)))));
                var body = Find<TextBox>(window, "BodyBox");
                var bodyFile = Find<TextBlock>(window, "BodyFileText");
                var base64 = Find<CheckBox>(window, "Base64Box");
                var copy = Find<Button>(window, "CopyCurlButton");

                await window.OpenRequestAsync();

                Assert.Equal(Visibility.Visible, body.Visibility);
                Assert.Equal(kind == "text" ? Encoding.ASCII.GetString(shown) : Convert.ToBase64String(shown), body.Text);
                Assert.Equal(Visibility.Collapsed, bodyFile.Visibility);
                Assert.True(copy.IsEnabled);

                inFile = large;
                await window.OpenRequestAsync();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_OPENED", "large.xmat-request.json"), Status(window));
                Assert.Equal(Visibility.Collapsed, body.Visibility);
                Assert.Equal("", body.Text);
                Assert.Equal(Visibility.Visible, bodyFile.Visibility);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_BODY_REQUEST_FILE", "large.xmat-request.json", large.LongLength),
                    bodyFile.Text);
                Assert.True(base64.IsChecked == true);
                Assert.False(base64.IsEnabled);
                string notCopied = Localization.GetLocalizedString("COMPOSER_COPY_CURL_LARGE_BODY");
                Assert.False(copy.IsEnabled);
                Assert.Equal(notCopied, copy.ToolTip);
                Assert.True(Find<Button>(window, "SaveCurlButton").IsEnabled);
                await window.CopyAsCurlAsync();
                Assert.Equal(notCopied, Status(window));
                Assert.Empty(copied);

                await window.SendAsync();
                await window.SaveRequestAsync();

                Assert.Equal(large, Assert.Single(server.Requests).Body);
                Assert.Equal(large, Assert.Single(saved).Body);
                Assert.Single(questions);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_SAVED_CONTENT", "saved-large.xmat-request.json"), Status(window));
                Assert.Equal(Visibility.Collapsed, body.Visibility);

                window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "upload.bin");
                window.ReadBodyFile = (_, _) => Task.FromResult(new ReplayBodyRead(large, null));
                await window.UploadBodyAsync();

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_BODY_FILE", "upload.bin", large.LongLength), bodyFile.Text);
                Assert.False(copy.IsEnabled);

                window.ClearBody();

                Assert.Equal(Visibility.Visible, body.Visibility);
                Assert.Equal("", body.Text);
                Assert.True(copy.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        // A file's name can hold characters that hide or reorder text, such as a right-to-left override: the composer shows
        // them as \uXXXX wherever it names the file, as in its status.
        [Fact]
        public Task AFileName_IsShownWithItsHidingCharactersEscaped() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            try
            {
                string path = Path.Join(directory, "report\u202Enosj.xmat-request.json");
                File.WriteAllText(path, Template("GET"));
                window.ChooseRequestFile = () => path;

                await window.OpenRequestAsync();

                Assert.Equal(
                    Localization.GetLocalizedString(
                        "COMPOSER_OPENED_INCOMPLETE",
                        @"report\u202Enosj.xmat-request.json",
                        Parts("COMPOSER_PART_URL", "COMPOSER_PART_HEADERS", "COMPOSER_PART_BODY")),
                    Status(window));
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // Save request as saves a template unless the user includes the captured content, and asks about the content for
        // every save, with the names that XMAT recognizes as sensitive, never their values. A template holds nothing that
        // was captured; a content file holds the whole request. Both are private files under their own names.
        [Fact]
        public Task SavingIsATemplateByDefault_AndTheContentIsAskedAboutEverySave() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            var questions = new List<RequestSaveQuestion>();
            var answers = new Queue<RequestSaveChoice>(new[]
            {
                new RequestSaveChoice(RequestFileProfile.Template), new RequestSaveChoice(RequestFileProfile.Content),
            });
            window.ChooseSaveContent = question =>
            {
                questions.Add(question);
                return answers.Dequeue();
            };
            try
            {
                const string url = "http://127.0.0.1:9/orders?access_token=canary-token&page=2";
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = url;
                Find<TextBox>(window, "HeadersBox").Text = "Authorization: Bearer canary-credential\r\nX-Trace: canary-trace";
                Find<TextBox>(window, "BodyBox").Text = "canary-body";
                string template = Path.Join(directory, "template.xmat-request.json");
                string content = Path.Join(directory, "content.xmat-request.json");
                window.ChooseSavePath = () => template;

                await window.SaveRequestAsync();

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_SAVED_TEMPLATE", "template.xmat-request.json"), Status(window));
                byte[] saved = File.ReadAllBytes(template);
                Assert.DoesNotContain("canary", Encoding.UTF8.GetString(saved));
                Assert.DoesNotContain("127.0.0.1", Encoding.UTF8.GetString(saved));
                RequestFileContent shape = RequestFileReader.Read(saved).Content;
                Assert.Equal((RequestFileProfile.Template, "POST"), (shape.Profile, shape.Method));
                Assert.Null(shape.Url);
                Assert.Null(shape.Headers);
                Assert.Null(shape.Body);
                Assert.Equal(new RequestOmissions(false, true, true, true), shape.Omitted);
                RequestSaveQuestion asked = Assert.Single(questions);
                Assert.Equal(RequestOmissions.None, asked.Pending);
                Assert.Null(asked.ChangedOrigin);
                Assert.Equal(new[] { "Authorization" }, asked.Recognized.HeaderNames);
                Assert.Equal(new[] { "access_token" }, asked.Recognized.QueryParameterNames);
                Assert.Equal(11, asked.Recognized.BodyBytesNotInspected);

                window.ChooseSavePath = () => content;
                await window.SaveRequestAsync();

                Assert.Equal(2, questions.Count);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_SAVED_CONTENT", "content.xmat-request.json"), Status(window));
                RequestFileContent whole = RequestFileReader.Read(File.ReadAllBytes(content)).Content;
                Assert.Equal(
                    (RequestFileProfile.Content, "POST", url, "canary-body"),
                    (whole.Profile, whole.Method, whole.Url, Encoding.UTF8.GetString(whole.Body)));
                Assert.Equal(
                    new[] { "Authorization: Bearer canary-credential", "X-Trace: canary-trace" },
                    whole.Headers.Select(header => header.Key + ": " + header.Value));
                Assert.Equal(
                    new[] { "content.xmat-request.json", "template.xmat-request.json" },
                    Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
                Assert.Equal(
                    ("POST", url, "Authorization: Bearer canary-credential\r\nX-Trace: canary-trace", "canary-body"),
                    Editors(window));
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // The question starts at a template every time; it lists recognized names once each and the body's size, never a
        // value. The content of a request with parts still left out cannot be included, and content for another origin
        // needs its own permission, which leaving the content out takes back.
        [Fact]
        public Task TheSaveQuestion_StartsAtATemplate_AndTheContentForAnotherOriginNeedsItsOwnPermission() => _application.RunAsync(() =>
        {
            var recognized = new RequestFileRecognized(new[] { "Authorization", "Cookie", "authorization" }, new[] { "access_token" }, 2048);
            var dialog = new SaveRequestWindow(new RequestSaveQuestion(RequestOmissions.None, recognized, "https://other.example:8443"));
            var incomplete = new SaveRequestWindow(
                new RequestSaveQuestion(new RequestOmissions(false, true, false, false), new RequestFileRecognized([], [], 0)));
            try
            {
                var content = Find<CheckBox>(dialog, "IncludeContentBox");
                var origin = Find<CheckBox>(dialog, "AllowOriginBox");
                var save = Find<Button>(dialog, "SaveButton");
                Assert.False(content.IsChecked == true);
                Assert.True(content.IsEnabled);
                Assert.False(origin.IsEnabled);
                Assert.True(save.IsEnabled);
                Assert.Equal(new RequestSaveChoice(RequestFileProfile.Template), dialog.Choice);
                Assert.Equal(
                    Localization.GetLocalizedString("SAVE_REQUEST_SENSITIVE_NAMES"), Find<TextBlock>(dialog, "RecognizedText").Text);
                Assert.Equal(
                    new[]
                    {
                        Localization.GetLocalizedString("SAVE_REQUEST_NAME_HEADER", "Authorization"),
                        Localization.GetLocalizedString("SAVE_REQUEST_NAME_HEADER", "Cookie"),
                        Localization.GetLocalizedString("SAVE_REQUEST_NAME_PARAMETER", "access_token"),
                    },
                    Find<ItemsControl>(dialog, "NamesList").Items.Cast<string>());
                Assert.Equal(Visibility.Collapsed, Find<TextBlock>(dialog, "MoreNamesText").Visibility);
                Assert.Equal(
                    Localization.GetLocalizedString("SAVE_REQUEST_BODY_NOT_INSPECTED", 2048L),
                    Find<TextBlock>(dialog, "BodyNotInspectedText").Text);
                Assert.Equal(
                    Localization.GetLocalizedString("SAVE_REQUEST_ORIGIN", "https://other.example:8443"),
                    Find<TextBlock>(dialog, "OriginText").Text);

                content.IsChecked = true;
                Assert.True(origin.IsEnabled);
                Assert.False(save.IsEnabled);
                Assert.Null(dialog.Choice);

                origin.IsChecked = true;
                Assert.True(save.IsEnabled);
                Assert.Equal(new RequestSaveChoice(RequestFileProfile.Content, OriginChangeAllowed: true), dialog.Choice);

                content.IsChecked = false;
                Assert.False(origin.IsChecked == true);
                Assert.False(origin.IsEnabled);
                Assert.Equal(new RequestSaveChoice(RequestFileProfile.Template), dialog.Choice);

                Assert.False(Find<CheckBox>(incomplete, "IncludeContentBox").IsEnabled);
                Assert.Equal(Visibility.Visible, Find<TextBlock>(incomplete, "PendingText").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<StackPanel>(incomplete, "OriginPanel").Visibility);
                Assert.Equal(
                    Localization.GetLocalizedString("SAVE_REQUEST_SENSITIVE_NONE"),
                    Find<TextBlock>(incomplete, "RecognizedText").Text);
                Assert.Equal(Visibility.Collapsed, Find<ScrollViewer>(incomplete, "NamesScroll").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<TextBlock>(incomplete, "BodyNotInspectedText").Visibility);
                Assert.Equal(new RequestSaveChoice(RequestFileProfile.Template), incomplete.Choice);
            }
            finally
            {
                dialog.Close();
                incomplete.Close();
            }

            return Task.CompletedTask;
        });

        // A parameter's name is whatever its URL decodes to, so the question shows each recognized name on its own line, with
        // every control, separator and format character, such as a line break or a bidirectional control, as \uXXXX, and cut
        // at a fixed length: no name can break, hide or reorder the question. At most 20 names are listed, in an area of
        // bounded height that scrolls, then how many more there are. A changed origin is shown the same way.
        [Fact]
        public Task TheSaveQuestion_ShowsHostileNamesEscaped_CutShort_AndBounded() => _application.RunAsync(() =>
        {
            string[] parameters = new[] { "to\r\nken", "a\u2028b", "x\u202Ey", "\u2066z\u2069\u200E\u200F", new string('t', 10_000) }
                .Concat(Enumerable.Range(1, 100).Select(number => "token" + number))
                .ToArray();
            var recognized = new RequestFileRecognized(new[] { "Authorization" }, parameters, 0);
            var dialog = new SaveRequestWindow(new RequestSaveQuestion(RequestOmissions.None, recognized, "https://o.example\u202E:443"));
            try
            {
                string[] shown = Find<ItemsControl>(dialog, "NamesList").Items.Cast<string>().ToArray();
                string Parameter(string name) => Localization.GetLocalizedString("SAVE_REQUEST_NAME_PARAMETER", name);
                Assert.Equal(SaveRequestWindow.MaxShownNames, shown.Length);
                Assert.Equal(
                    new[]
                    {
                        Localization.GetLocalizedString("SAVE_REQUEST_NAME_HEADER", "Authorization"),
                        Parameter(@"to\u000D\u000Aken"),
                        Parameter(@"a\u2028b"),
                        Parameter(@"x\u202Ey"),
                        Parameter(@"\u2066z\u2069\u200E\u200F"),
                        Parameter(new string('t', SaveRequestWindow.MaxShownNameChars) + "\u2026"),
                        Parameter("token1"),
                    },
                    shown.Take(7));
                Assert.All(shown, line => Assert.DoesNotContain(line, character => char.GetUnicodeCategory(character) is
                    UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator));
                Assert.Equal(Visibility.Visible, Find<TextBlock>(dialog, "MoreNamesText").Visibility);
                Assert.Equal(
                    Localization.GetLocalizedString("SAVE_REQUEST_SENSITIVE_MORE", 1 + parameters.Length - SaveRequestWindow.MaxShownNames),
                    Find<TextBlock>(dialog, "MoreNamesText").Text);
                var scroll = Find<ScrollViewer>(dialog, "NamesScroll");
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.True(double.IsFinite(scroll.MaxHeight));
                Assert.Same(scroll, Find<ItemsControl>(dialog, "NamesList").Parent);
                Assert.Equal(
                    Localization.GetLocalizedString("SAVE_REQUEST_ORIGIN", @"https://o.example\u202E:443"),
                    Find<TextBlock>(dialog, "OriginText").Text);
            }
            finally
            {
                dialog.Close();
            }

            return Task.CompletedTask;
        });

        // A content save for another origin than the request was captured for needs the permission for that origin, which
        // the question names; a template, which keeps no URL, does not.
        [Fact]
        public Task ContentForAnotherOrigin_IsSavedOnlyWithItsOwnPermission() => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var capture = new ProxyConnectionModel(7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "GET", "/data",
                "GET /data HTTP/1.1\r\nHost: 127.0.0.1:9\r\nAuthorization: Bearer canary-credential\r\n", [],
                "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
            var window = new RequestComposerWindow(capture, client, NoQuestion);
            var questions = new List<RequestSaveQuestion>();
            var answers = new Queue<RequestSaveChoice>(new[]
            {
                new RequestSaveChoice(RequestFileProfile.Content),
                new RequestSaveChoice(RequestFileProfile.Template),
                new RequestSaveChoice(RequestFileProfile.Content, OriginChangeAllowed: true),
            });
            var written = new List<RequestFileContent>();
            window.ChooseSavePath = () => Path.Join(AppContext.BaseDirectory, "moved.xmat-request.json");
            window.ChooseSaveContent = question =>
            {
                questions.Add(question);
                return answers.Dequeue();
            };
            window.PublishRequestFile = (_, write, _) =>
            {
                using var stream = new MemoryStream();
                write(stream);
                written.Add(RequestFileReader.Read(stream.ToArray()).Content);
                return PrivateFilePublication.Published;
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.2:9/data";

                await window.SaveRequestAsync();

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_SAVE_ORIGIN_REFUSED", "http://127.0.0.2:9"), Status(window));
                Assert.Equal("http://127.0.0.2:9", Assert.Single(questions).ChangedOrigin);
                Assert.Empty(written);

                await window.SaveRequestAsync();
                await window.SaveRequestAsync();

                Assert.Equal(3, questions.Count);
                Assert.All(questions, question => Assert.Equal("http://127.0.0.2:9", question.ChangedOrigin));
                Assert.Equal(
                    new[] { (RequestFileProfile.Template, (string)null), (RequestFileProfile.Content, "http://127.0.0.2:9/data") },
                    written.Select(file => (file.Profile, file.Url)));
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_SAVED_CONTENT", "moved.xmat-request.json"), Status(window));
            }
            finally
            {
                window.Close();
            }
        });

        // The origin that saving the content needs a permission for is named as the composer shows origins, bounded and
        // escaped, by the check of the save, in the question and in the status that refuses the content without it: a long
        // host is cut short, and a zero-width character that the host keeps is shown as \uXXXX. The file saved with the
        // permission keeps the whole URL, exactly as typed, a bidirectional control in its query included.
        [Theory]
        [InlineData("long")]
        [InlineData("unseen")]
        public Task AnotherOrigin_IsNamedBoundedAndEscaped_AndTheFileKeepsTheUrlWhole(
            string kind) => _application.RunAsync(async () =>
        {
            string host = kind == "long" ? string.Join(".", Enumerable.Repeat(new string('a', 60), 6)) + ".test" : "x\u200By.test";
            string origin = "https://" + host;
            string url = origin + "/data?q=\u202Ez";
            string shown = UntrustedText.Show(origin, 300);
            using var client = new RequestReplayClient();
            var capture = new ProxyConnectionModel(7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "GET", "/data",
                "GET /data HTTP/1.1\r\nHost: 127.0.0.1:9\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
            var window = new RequestComposerWindow(capture, client, NoQuestion);
            var questions = new List<RequestSaveQuestion>();
            var answers = new Queue<RequestSaveChoice>(new[]
            {
                new RequestSaveChoice(RequestFileProfile.Content),
                new RequestSaveChoice(RequestFileProfile.Content, OriginChangeAllowed: true),
            });
            var written = new List<RequestFileContent>();
            window.ChooseSavePath = () => Path.Join(AppContext.BaseDirectory, "moved.xmat-request.json");
            window.ChooseSaveContent = question =>
            {
                questions.Add(question);
                return answers.Dequeue();
            };
            window.PublishRequestFile = (_, write, _) =>
            {
                using var stream = new MemoryStream();
                write(stream);
                written.Add(RequestFileReader.Read(stream.ToArray()).Content);
                return PrivateFilePublication.Published;
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = url;

                await window.SaveRequestAsync();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVE_ORIGIN_REFUSED", shown), Status(window));
                Assert.DoesNotContain(origin, Status(window), StringComparison.Ordinal);
                Assert.Empty(written);

                await window.SaveRequestAsync();

                Assert.Equal(new[] { shown, shown }, questions.Select(question => question.ChangedOrigin));
                Assert.Equal((RequestFileProfile.Content, url), (Assert.Single(written).Profile, written[0].Url));
            }
            finally
            {
                window.Close();
            }
        });

        // Every outcome of a save is worded, by the file's name only, and none changes the request in the editors.
        [Theory]
        [InlineData("Exists", "COMPOSER_SAVE_EXISTS")]
        [InlineData("Unsupported", "COMPOSER_SAVE_UNSUPPORTED")]
        [InlineData("Cancelled", "COMPOSER_SAVE_CANCELLED")]
        [InlineData("Failed", "COMPOSER_SAVE_FAILED")]
        [InlineData("CleanupFailed", "COMPOSER_SAVE_CLEANUP_FAILED")]
        public Task EverySaveOutcome_IsWorded_AndLeavesTheRequestAsItWas(string outcome, string key) => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            try
            {
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/kept";
                Find<TextBox>(window, "BodyBox").Text = "kept";
                window.ChooseSavePath = () => Path.Join(AppContext.BaseDirectory, "worded.xmat-request.json");
                window.ChooseSaveContent = _ => new RequestSaveChoice(RequestFileProfile.Template);
                window.PublishRequestFile = (_, _, _) => Enum.Parse<PrivateFilePublication>(outcome);

                await window.SaveRequestAsync();

                Assert.Equal(Localization.GetLocalizedString(key, "worded.xmat-request.json"), Status(window));
                Assert.Equal(("GET", "http://127.0.0.1:9/kept", "", "kept"), Editors(window));
                Assert.True(Find<Button>(window, "SaveRequestButton").IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        // A name that is taken is never replaced: the file stays as it was, and nothing else is left behind.
        [Fact]
        public Task SavingAsAFileThatExists_ReplacesNothing() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            try
            {
                string path = Path.Join(directory, "taken.xmat-request.json");
                File.WriteAllText(path, "keep me");
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/";
                window.ChooseSavePath = () => path;
                window.ChooseSaveContent = _ => new RequestSaveChoice(RequestFileProfile.Template);

                await window.SaveRequestAsync();

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_SAVE_EXISTS", "taken.xmat-request.json"), Status(window));
                Assert.Equal("keep me", File.ReadAllText(path));
                Assert.Equal(new[] { "taken.xmat-request.json" }, Directory.GetFiles(directory).Select(Path.GetFileName));
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // A request file as XMAT writes one; a part that is null is left out, and listed in "omitted".
        private static string RequestFileJson(
            string profile, string method, string url, string httpVersion, string headers, string body)
        {
            var omitted = new List<string>();
            if (method == null)
                omitted.Add("\"method\"");
            if (url == null)
                omitted.Add("\"url\"");
            if (headers == null)
                omitted.Add("\"headers\"");
            if (body == null)
                omitted.Add("\"body\"");
            return "{\r\n" +
                "  \"schema\": \"xmat-request/1\",\r\n" +
                "  \"profile\": \"" + profile + "\",\r\n" +
                "  \"method\": " + Quoted(method) + ",\r\n" +
                "  \"url\": " + Quoted(url) + ",\r\n" +
                "  \"httpVersion\": \"" + httpVersion + "\",\r\n" +
                "  \"headers\": " + (headers ?? "null") + ",\r\n" +
                "  \"body\": " + Quoted(body) + ",\r\n" +
                "  \"omitted\": [" + string.Join(", ", omitted) + "]\r\n" +
                "}\r\n";
        }

        // A template that keeps only this method, or none, and leaves out the URL, the headers and the body.
        private static string Template(string method) => RequestFileJson("template", method, null, "auto", null, null);

        private static string Quoted(string value) => value == null ? "null" : "\"" + value + "\"";

        // The parts, as the composer names them, in order.
        private static string Parts(params string[] keys) =>
            string.Join(", ", keys.Select(key => Localization.GetLocalizedString(key)));

        private static string Supplied(string part) =>
            Localization.GetLocalizedString("COMPOSER_SUPPLIED", Localization.GetLocalizedString(part));

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        // Selects a button as the user does: one that the window shows and enables.
        private static void Click(Window window, string name)
        {
            var button = Find<Button>(window, name);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        // Selects a button, then waits until the window has finished what the button started, off the UI thread too.
        private static async Task ClickAsync(Window window, string name)
        {
            Click(window, name);
            var clock = Stopwatch.StartNew();
            while (!Find<Button>(window, "OpenRequestButton").IsEnabled)
            {
                Assert.True(clock.Elapsed < Patience, "The window did not finish in time.");
                await Task.Delay(10);
            }
        }

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        // Completes, without throwing, once the token is cancelled, which must happen within Patience: work that still returns
        // normally after Cancel request.
        private static async Task UntilCancelledAsync(CancellationToken token)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult()))
            {
                await cancelled.Task.WaitAsync(Patience);
            }
        }

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        // The method, URL, headers and body as the editors show them.
        private static (string, string, string, string) Editors(Window window) => (
            Find<TextBox>(window, "MethodBox").Text,
            Find<TextBox>(window, "UrlBox").Text,
            Find<TextBox>(window, "HeadersBox").Text,
            Find<TextBox>(window, "BodyBox").Text);

        // The transport options as the composer shows them: the HTTP version, Connect to another endpoint and its endpoint,
        // and Follow same-origin redirects and the most redirects.
        private static (ReplayHttpVersion, bool, string, bool, int) Transport(Window window) => (
            Assert.IsType<ReplayHttpVersion>(
                Assert.IsType<ComboBoxItem>(Find<ComboBox>(window, "HttpVersionBox").SelectedItem).Tag),
            Find<CheckBox>(window, "ConnectToBox").IsChecked == true,
            Find<TextBox>(window, "ConnectToText").Text,
            Find<CheckBox>(window, "FollowRedirectsBox").IsChecked == true,
            Assert.IsType<int>(Find<ComboBox>(window, "RedirectLimitBox").SelectedItem));

        // Chooses an HTTP version, as a request file spells it.
        private static void ChooseVersion(Window window, string spelled)
        {
            var box = Find<ComboBox>(window, "HttpVersionBox");
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(item => VersionOf(spelled).Equals(item.Tag));
        }

        // Changes one transport option alone, as the user does: chooses HTTP/1.1 only, turns on Connect to another endpoint
        // or Follow same-origin redirects, or changes the endpoint or the most redirects, turning the option on to do so and
        // off again.
        private static void ChangeTransport(Window window, string option)
        {
            var connectTo = Find<CheckBox>(window, "ConnectToBox");
            var follow = Find<CheckBox>(window, "FollowRedirectsBox");
            switch (option)
            {
                case "version":
                    ChooseVersion(window, "1.1");
                    break;
                case "connect":
                    connectTo.IsChecked = true;
                    break;
                case "endpoint":
                    connectTo.IsChecked = true;
                    Find<TextBox>(window, "ConnectToText").Text = "127.0.0.2:9443";
                    connectTo.IsChecked = false;
                    break;
                case "follow":
                    follow.IsChecked = true;
                    break;
                case "limit":
                    follow.IsChecked = true;
                    Find<ComboBox>(window, "RedirectLimitBox").SelectedItem = 3;
                    follow.IsChecked = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(option));
            }
        }

        // An HTTP version as a request file spells it.
        private static ReplayHttpVersion VersionOf(string spelled) => spelled switch
        {
            "auto" => ReplayHttpVersion.Auto,
            "1.1" => ReplayHttpVersion.Http11,
            "2" => ReplayHttpVersion.Http2,
            _ => throw new ArgumentOutOfRangeException(nameof(spelled)),
        };

        // Records into the model's Replays tab, on this test's UI thread.
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

        private static string FixtureDirectory()
        {
            string directory = Path.Join(AppContext.BaseDirectory, "RequestFileFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
