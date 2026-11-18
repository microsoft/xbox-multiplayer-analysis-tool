// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // Raw mode's send and words: each code's text is the core's message, word for word; the
    // write states' sentences and the text of a send that could not be completed say only what XMAT knows, using fixed
    // wording; the notice says what a raw send does, and when it gets no row. A raw send is checked, asked about every
    // time, and sends exactly its prepared bytes, here to a scripted connection (SeamConnection). Every other route that a
    // test's run could take ends at RefusingReplayTransport, refused at once in memory: a Parsed send over the
    // client's invoker or a run's own sender, and a raw send's lookup or connect that the test does not script. The
    // application composer's own client, which no test can give, sends in one test only, to a loopback server that the test
    // owns. So no route of these tests reaches a socket but that owned server's.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerRawSendTests
    {
        // The raw sends' target: a documentation address that no route reaches, since each ends at the scripted connection
        // or at RefusingReplayTransport.
        private const string Target = "http://192.0.2.10:8080";
        private const string Canary = "XMATCANARY-raw-send";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        public ComposerRawSendTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerRawSendTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // The mirror: the en-us text of every code of Raw mode is RawRequest.Message's, word for word, so the texts are
        // tested, not copied by hand; the command line's own code has no GUI text.
        [Fact]
        public void EachCodesText_IsTheCoresMessage_WordForWord()
        {
            string[] codes = typeof(RawCodes)
                .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue())
                .Where(code => code != RawCodes.FileUnreadable)
                .ToArray();

            Assert.NotEmpty(codes);
            Assert.DoesNotContain(codes, code => RawSendWording.KeyOf(code) == null);
            foreach (string code in codes)
            {
                Assert.Equal(RawRequest.Message(code), Localization.GetLocalizedString(RawSendWording.KeyOf(code)));
            }

            Assert.Null(RawSendWording.KeyOf(RawCodes.FileUnreadable));
        }

        // The four write states use fixed sentences, word for word, with the count of what the write
        // calls handed to the connection and the denial of receipt that go with them: none says that the server received
        // anything, and the uncertain state's says neither that XMAT stopped nor that it did not finish.
        [Fact]
        public void TheWriteStatesSentences_AreTheContractsWordForWord()
        {
            Assert.Equal("XMAT wrote nothing of the request.", Words("COMPOSER_RAW_STATE_NONE"));
            Assert.Equal(
                "XMAT stopped after handing part of the request to the connection.", Words("COMPOSER_RAW_STATE_PARTIAL"));
            Assert.Equal(
                "XMAT could not confirm that every prepared byte was handed to the connection.",
                Words("COMPOSER_RAW_STATE_UNCERTAIN"));
            Assert.Equal("XMAT handed every byte to the connection.", Words("COMPOSER_RAW_STATE_COMPLETE"));
            Assert.Equal("The write calls that completed handed it 512 bytes.", Words("COMPOSER_RAW_HANDED", 512L));
            Assert.Equal("That does not prove that the server received them.", Words("COMPOSER_RAW_NO_RECEIPT"));
        }

        // A send that could not be completed (replay.send.failed) is worded for every way that it arises in Raw mode, keeps
        // what an HTTPS server must send, and never says that no response, or no response data, came: interim responses or
        // part of one may have.
        [Fact]
        public void SendFailed_CoversEveryWayItArises_AndNeverSaysThatNoResponseCame()
        {
            string text = Words("COMPOSER_RAW_SEND_FAILED");

            Assert.Contains("could not resolve the target's name, connect to it or set up TLS with it", text);
            Assert.Contains("a write of the request failed and no complete response followed", text);
            Assert.Contains("the connection ended before a complete final status line came", text);
            Assert.EndsWith(
                "An HTTPS server must send every intermediate certificate that this PC does not already have.", text);
            Assert.DoesNotContain("In Raw mode", text, StringComparison.Ordinal);
            Assert.DoesNotContain("no response", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("before a response began", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("before any response", text, StringComparison.OrdinalIgnoreCase);
        }

        // Raw mode's notice, word for word: what a raw send does and does not do, and its recording guarantee with
        // both exceptions, XMAT exiting without closing its window and a Replays tab that cannot take the row, the second
        // reported by the status line only while the window is open, and by the application log otherwise.
        [Fact]
        public void TheNotice_StatesTheRecordingGuaranteeWithBothExceptions()
        {
            Assert.Equal(
                "Raw mode sends one HTTP/1.1 request from this PC on a new connection of its own, directly or to Connect to " +
                    "another endpoint, never through a proxy, with no redirects or retries, and with no automatic cookies or " +
                    "credentials: no client certificate and no Windows authentication. Fields that you write, such as " +
                    "Authorization or Cookie, are part of the prepared bytes. Limit: 30 seconds and a 16 MiB response. A " +
                    "send that begins gets a row in the XMAT Replays tab, which shows a normalized view of it, unless XMAT " +
                    "exits without closing its window, as when Windows ends the session, or the tab cannot take the row. " +
                    "While this window is open, the status line then says so; once it is closed, only the application log " +
                    "notes it, without any of the request's data.",
                Words("COMPOSER_RAW_NOTICE"));
        }

        // Cancel request while Send's check builds the body's bytes previews, asks and sends nothing, records nothing,
        // and says that nothing was sent.
        [Fact]
        public Task CancelWhileTheBodyIsBuilt_SendsNothing() => _application.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var connection = new SeamConnection();
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            int prepared = 0;
            try
            {
                await EnterRawModeAsync(window, "POST", "0123456789");
                window.BodySliceChars = 3;
                window.AfterBodySlice = index =>
                {
                    if (index == 1)
                    {
                        dispatcher.Invoke(window.CancelSend);
                    }
                };
                window.PrepareRaw = (head, body, target, token, limits) =>
                {
                    Interlocked.Increment(ref prepared);
                    return RawRequest.Prepare(head, body, target, token, limits);
                };

                await window.SendAsync();

                Assert.Equal(Words("COMPOSER_RAW_CANCELLED_NOT_BEGUN"), Status(window));
                Assert.Equal(0, Volatile.Read(ref prepared));
                Assert.Empty(connects);
                Assert.Empty(Replays(model).ProxyConnections);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Cancel request once the run has begun but before the raw send begins records nothing and says that nothing was
        // sent (LastItem is null).
        [Fact]
        public Task CancelBeforeTheSendBegins_RecordsNothing() => _application.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var connection = new SeamConnection();
            var connects = new List<string>();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            RequestComposerWindow window = null;
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = transport.Client(setup => setup with
            {
                Connector = (endpoint, _) =>
                {
                    connects.Add(endpoint.ToString());
                    return Task.FromResult<Stream>(connection);
                },
                Hooks = new RawSenderHooks(At: (point, _) =>
                {
                    if (point == RawSendPoint.BeforeBegin)
                    {
                        dispatcher.Invoke(window.CancelSend);
                    }
                }),
            });
            window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model));
            try
            {
                await EnterRawModeAsync(window, "GET", "");

                await window.SendAsync();

                Assert.Equal(Words("COMPOSER_RAW_CANCELLED_NOT_BEGUN"), Status(window));
                Assert.Empty(connects);
                Assert.Empty(Replays(model).ProxyConnections);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A raw send inherits the origin that its request was captured for (FromRaw): captured for A and sent
        // raw to B, even a GET without a body is asked about the risk of another origin.
        [Fact]
        public Task ARawSendToAnotherOriginThanTheCaptures_AsksAboutTheRisk() => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var capture = new ProxyConnectionModel(
                1,
                "200",
                DateTime.UnixEpoch,
                TimeSpan.Zero,
                "http",
                "captured.example",
                "GET",
                "/items",
                "GET /items HTTP/1.1\r\nHost: captured.example\r\n",
                Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\n",
                Array.Empty<byte>(),
                "10.0.0.1");
            var window = new RequestComposerWindow(capture, client, Answer(questions, false), Recording(model));
            try
            {
                await EnterRawModeAsync(window, "GET", "");

                await window.SendAsync();

                string[] paragraphs = Assert.Single(questions).Split("\n\n");
                Assert.Equal(
                    new[] { Words("COMPOSER_CONFIRM_RAW_RISK"), Words("COMPOSER_CONFIRM_RAW_SEND") }, paragraphs[^2..]);
                Assert.Empty(connects);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A composer without a recording target, which only tests make, sends nothing in Raw mode: Send says so, and
        // nothing is prepared, previewed, asked or sent. Every route of its client ends at the transport, so a send on any of
        // them is refused at once and noted there.
        [Fact]
        public Task WithoutARecordingTarget_ARawSendIsRefused_BeforeAnythingIsPrepared() => _application.RunAsync(async () =>
        {
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = transport.Client();
            int operations = 0;
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            int prepared = 0;
            try
            {
                await EnterRawModeAsync(window, "GET", "");
                client.OperationFactory = () =>
                {
                    Interlocked.Increment(ref operations);
                    return transport.Operation();
                };
                window.PrepareRaw = (head, body, target, token, limits) =>
                {
                    Interlocked.Increment(ref prepared);
                    return RawRequest.Prepare(head, body, target, token, limits);
                };

                await window.SendAsync();

                Assert.Empty(transport.Attempts);
                Assert.False(window.HasRecordingTarget);
                Assert.Equal(Words("COMPOSER_RAW_NOT_RECORDED"), Status(window));
                Assert.Equal((0, 0), (Volatile.Read(ref prepared), Volatile.Read(ref operations)));
            }
            finally
            {
                window.Close();
            }
        });

        // The question is always asked, and its paragraphs describe the prepared image: the prepared
        // bytes, the target origin, the request line, the head with the line breaks typed as LF that are sent as CRLF, and
        // the body with its framing, then, for a POST, the risk, then whether to send; never the head itself. Yes sends
        // exactly the prepared bytes, the head then the body, by the raw route alone, which the transport's empty notes show,
        // and the Replays tab records the send with its write state.
        [Fact]
        public Task TheQuestion_IsAlwaysAsked_AndYesSendsExactlyThePreparedBytes() => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            connection.AfterWrite = _ => connection.Respond("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), Recording(model));
            var images = new List<RawRequestImage>();
            window.PrepareRaw = (head, body, target, token, limits) =>
            {
                RawImageResult result = RawRequest.Prepare(head, body, target, token, limits);
                images.Add(result.Image);
                return result;
            };
            try
            {
                await EnterRawModeAsync(window, "POST", "hello");
                Find<TextBox>(window, "RawHeadBox").Text =
                    "POST /items?id=1 HTTP/1.1\nHost: 192.0.2.10:8080\nContent-Length: 5";

                await window.SendAsync();

                Assert.Empty(transport.Attempts);
                RawRequestImage image = Assert.Single(images);
                Assert.Equal(
                    string.Join(
                        "\n\n",
                        Words(
                            "COMPOSER_CONFIRM_RAW",
                            image.Length,
                            Target,
                            "POST /items?id=1 HTTP/1.1",
                            Words("COMPOSER_CONFIRM_RAW_HEAD_LF", image.HeadLength, image.FieldCount, 2),
                            Words("COMPOSER_CONFIRM_RAW_LENGTH", 5L)),
                        Words("COMPOSER_CONFIRM_RAW_RISK"),
                        Words("COMPOSER_CONFIRM_RAW_SEND")),
                    Assert.Single(questions));
                Assert.DoesNotContain("Host: 192.0.2.10:8080", questions[0], StringComparison.Ordinal);
                Assert.DoesNotContain(image.Sha256, questions[0], StringComparison.OrdinalIgnoreCase);
                Assert.Equal("192.0.2.10:8080", Assert.Single(connects));
                Assert.Equal(image.CopyHead().Concat(image.CopyWireBody()).ToArray(), connection.Written.ToArray());
                Assert.StartsWith("HTTP 200 | ", Status(window), StringComparison.Ordinal);
                Assert.DoesNotContain(Words("COMPOSER_RAW_STATE_COMPLETE"), Status(window), StringComparison.Ordinal);
                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                Assert.Equal((true, RawWriteState.Complete), (row.SentRaw, row.RawRequestWrite));
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A GET with a body is asked about as an unsafe request: the risk, then the body paragraph, which names
        // its method; and No sends and records nothing.
        [Fact]
        public Task AGetWithABody_IsAskedAboutItsBody_AndNoSendsNothing() => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            connection.AfterWrite = _ => connection.Respond("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, false), Recording(model));
            try
            {
                await EnterRawModeAsync(window, "GET", "hello");

                await window.SendAsync();

                string[] paragraphs = Assert.Single(questions).Split("\n\n");
                Assert.Equal(
                    new[]
                    {
                        Words("COMPOSER_CONFIRM_RAW_RISK"),
                        Words("COMPOSER_CONFIRM_RAW_BODY", "GET"),
                        Words("COMPOSER_CONFIRM_RAW_SEND"),
                    },
                    paragraphs[^3..]);
                Assert.Empty(connects);
                Assert.Empty(Replays(model).ProxyConnections);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Cancel request while XMAT asks sends nothing, even when the answer is Yes, and says so.
        [Fact]
        public Task CancelWhileAsked_SendsAndRecordsNothing() => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            connection.AfterWrite = _ => connection.Respond("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            RequestComposerWindow window = null;
            int asked = 0;
            window = RequestComposerWindow.ForNewRequest(
                client,
                _ =>
                {
                    asked++;
                    window.CancelSend();
                    return true;
                },
                Recording(model));
            try
            {
                await EnterRawModeAsync(window, "GET", "");

                await window.SendAsync();

                Assert.Equal(1, asked);
                Assert.Equal(Words("COMPOSER_RAW_CANCELLED_NOT_BEGUN"), Status(window));
                Assert.Empty(connects);
                Assert.Empty(Replays(model).ProxyConnections);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Each refusal of a raw send's check, in its own words, before anything is asked or sent: a target
        // origin that is none, an endpoint that is none, Connect to another endpoint for an http target, a request that
        // Prepare refuses, and a body that is not valid Base64.
        [Theory]
        [InlineData("origin", "COMPOSER_RAW_ORIGIN")]
        [InlineData("endpoint", "COMPOSER_CONNECT_TO_INVALID")]
        [InlineData("connect-to-http", "COMPOSER_RAW_CONNECT_TO_HTTPS")]
        [InlineData("head-with-body", "COMPOSER_RAW_BODY_NOT_ALLOWED")]
        [InlineData("base64", "COMPOSER_RAW_BODY_BASE64")]
        public Task ARefusedRawSend_AsksAndSendsNothing(string refusal, string key) => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                await EnterRawModeAsync(window, "GET", "");
                switch (refusal)
                {
                    case "origin":
                        Find<TextBox>(window, "RawTargetBox").Text = "ftp://192.0.2.10";
                        break;
                    case "endpoint":
                        Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                        Find<TextBox>(window, "ConnectToText").Text = "not an endpoint";
                        break;
                    case "connect-to-http":
                        Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                        Find<TextBox>(window, "ConnectToText").Text = "127.0.0.2:8080";
                        break;
                    case "head-with-body":
                        Find<TextBox>(window, "RawHeadBox").Text =
                            "HEAD /items HTTP/1.1\r\nHost: 192.0.2.10:8080\r\nContent-Length: 5";
                        Find<TextBox>(window, "BodyBox").Text = "hello";
                        break;
                    default:
                        Find<CheckBox>(window, "Base64Box").IsChecked = true;
                        Find<TextBox>(window, "BodyBox").Text = "@@@@";
                        break;
                }

                await window.SendAsync();

                Assert.StartsWith(Words("COMPOSER_ERROR", Words(key)), Status(window), StringComparison.Ordinal);
                Assert.Empty(connects);
                Assert.Empty(Replays(model).ProxyConnections);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The rule of the three Base64 refusals, word for word: besides its alphabet and padding, a Base64 body may
        // hold spaces, tabs and line breaks. A body with tabs before, within, between and after its groups is taken by every
        // path that would refuse it in those words: Edit as raw text and Edit as parsed, Fix Content-Length, which sets its
        // decoded length, and Send, which sends its decoded bytes.
        [Fact]
        public Task ABase64BodyWithTabs_IsTakenByEveryPathThatWouldRefuseIt() => _application.RunAsync(async () =>
        {
            const string Rule =
                "the body is not valid Base64, which holds only A-Z, a-z, 0-9, + and /, in whole groups of four, with " +
                "at most two = at its end, besides spaces, tabs and line breaks";
            Assert.Equal("Content-Length is unchanged: " + Rule + ".", Words("COMPOSER_RAW_FIX_BODY_BASE64"));
            Assert.Equal("Not converted: " + Rule + ". Correct it first.", Words("COMPOSER_CONVERT_BODY_BASE64"));
            Assert.Equal("T" + Rule.Substring(1) + "; nothing was sent.", Words("COMPOSER_RAW_BODY_BASE64"));
            var connection = new SeamConnection();
            connection.AfterWrite = _ => connection.Respond("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model));
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = Target + "/items";
                Find<TextBox>(window, "HeadersBox").Text = "";
                Find<CheckBox>(window, "Base64Box").IsChecked = true;
                Find<TextBox>(window, "BodyBox").Text = "\tQU\tJD\tRA==\t";

                await window.SwitchModeAsync();
                Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                await window.SwitchModeAsync();
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                await window.SwitchModeAsync();
                Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                var head = Find<TextBox>(window, "RawHeadBox");
                head.Text = "POST /items HTTP/1.1\nHost: 192.0.2.10:8080\nContent-Length: 1";
                await window.FixContentLengthAsync();
                Assert.Equal(Words("COMPOSER_RAW_FIXED_LENGTH", 4L), Status(window));
                Assert.Equal("POST /items HTTP/1.1\nHost: 192.0.2.10:8080\nContent-Length: 4", head.Text);

                await window.SendAsync();

                Assert.StartsWith("HTTP 200 | ", Status(window), StringComparison.Ordinal);
                Assert.Equal("192.0.2.10:8080", Assert.Single(connects));
                Assert.EndsWith(
                    "Content-Length: 4\r\n\r\nABCD",
                    System.Text.Encoding.ASCII.GetString(connection.Written.ToArray()),
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A send that could not connect is worded as one that could not be completed, then that nothing of the request was
        // written, and its row keeps that write state; the failure's own message never shows.
        [Fact]
        public Task AConnectorThatFails_IsWordedAsSendFailed_WithNothingWritten() => _application.RunAsync(async () =>
        {
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = transport.Client(setup => setup with
            {
                Connector = (_, _) => Task.FromException<Stream>(new IOException(Canary)),
            });
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model));
            try
            {
                await EnterRawModeAsync(window, "GET", "");

                await window.SendAsync();

                string status = Status(window);
                Assert.StartsWith(
                    Words("COMPOSER_ERROR", Words("COMPOSER_RAW_SEND_FAILED")) + " " + Words("COMPOSER_RAW_STATE_NONE"),
                    status,
                    StringComparison.Ordinal);
                Assert.False(status.Contains(Canary, StringComparison.Ordinal), "status");
                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                Assert.Equal((true, RawWriteState.None), (row.SentRaw, row.RawRequestWrite));
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The control of the routes that no test here scripts, through the clients that the tests use: a send in
        // Parsed mode on purpose, directly or through a run's own sender to Connect to another endpoint, and a raw send whose
        // connect no test scripts each end at RefusingReplayTransport, which notes it and refuses it at once, in memory. The
        // send ends well within Patience and says that it could not be completed, and the scripted connection sees nothing.
        // A regression to the wrong route ends the same way, so the tests fail on the transport's notes at once,
        // never by waiting for a connection's timeout.
        [Theory]
        [InlineData("parsed")]
        [InlineData("own-sender")]
        [InlineData("raw")]
        public Task ARouteThatNoTestScripts_EndsAtTheTransport_AtOnce(string route) => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client =
                route == "raw" ? transport.Client() : Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model));
            string url = route == "own-sender" ? "https://192.0.2.10:8443/items" : Target + "/items?id=1";
            try
            {
                if (route == "raw")
                {
                    await EnterRawModeAsync(window, "GET", "");
                }
                else
                {
                    Find<TextBox>(window, "UrlBox").Text = url;
                    if (route == "own-sender")
                    {
                        Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                        Find<TextBox>(window, "ConnectToText").Text = "127.0.0.2:8443";
                    }
                }

                await window.SendAsync().WaitAsync(Patience);

                Assert.Equal(
                    route switch
                    {
                        "parsed" => new[] { "GET " + new Uri(url) },
                        "own-sender" => new[] { "own 127.0.0.2:8443", "GET " + new Uri(url) },
                        _ => new[] { "connect 192.0.2.10:8080" },
                    },
                    transport.Attempts);
                Assert.Empty(connects);
                Assert.Equal(0, connection.WritesStarted);
                Assert.StartsWith(
                    Words(
                        "COMPOSER_ERROR",
                        route == "raw" ? Words("COMPOSER_RAW_SEND_FAILED") : RefusingReplayTransport.Refusal),
                    Status(window),
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Cancel request once the request was written, while XMAT reads the response, cancels the send, which keeps its row
        // with its write state, and the status says what the cancellation means, then that every byte was handed to the
        // connection, which does not prove that the server received them; so do the row's note and the
        // recorder's last item.
        [Fact]
        public Task CancelDuringTheRead_KeepsTheRow_AndSaysWhatWasWritten() => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.AfterWrite = _ => written.TrySetResult();
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model));
            ReplayRecorder recorder = null;
            window.RunEnded = run => recorder = run;
            try
            {
                await EnterRawModeAsync(window, "GET", "");

                Task sending = window.SendAsync();
                await written.Task.WaitAsync(Patience);
                await connection.ReadWaiting.Task.WaitAsync(Patience);
                window.CancelSend();
                await sending.WaitAsync(Patience);

                string sentence = Words("COMPOSER_RAW_STATE_COMPLETE") + " " + Words("COMPOSER_RAW_NO_RECEIPT");
                Assert.StartsWith(
                    Words("COMPOSER_RAW_SEND_CANCELLED") + " " + sentence, Status(window), StringComparison.Ordinal);
                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                Assert.Equal((true, RawWriteState.Complete), (row.SentRaw, row.RawRequestWrite));
                Assert.Equal(
                    string.Join(
                        "\n",
                        Words("REPLAY_NOTE_RAW", sentence),
                        RawRequest.Message(RawCodes.SendCancelled),
                        Words("REPLAY_NOTE_HTTP_VERSION_NO_RESPONSE", Words("COMPOSER_HTTP_VERSION_11"))),
                    row.ReplayNote);
                Assert.Equal(
                    (true, RawCodes.SendCancelled, RawWriteState.Complete),
                    (recorder.LastItem.Raw, recorder.LastItem.Code, recorder.LastItem.RequestWrite));
                Assert.Same(row, recorder.LastRecorded);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A response that ended when the server closed the connection says, after its status, that XMAT cannot tell whether
        // it is complete; a complete write adds no sentence.
        [Fact]
        public Task ACloseDelimitedResponse_SaysThatItMayBeIncomplete() => _application.RunAsync(async () =>
        {
            var connection = new SeamConnection();
            connection.AfterWrite = _ =>
            {
                connection.Respond("HTTP/1.1 200 OK\r\n\r\nok");
                connection.End();
            };
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, Recording(model));
            try
            {
                await EnterRawModeAsync(window, "GET", "");

                await window.SendAsync();

                string status = Status(window);
                Assert.StartsWith("HTTP 200 | ", status, StringComparison.Ordinal);
                Assert.Contains(" ms | 2 bytes " + Words("COMPOSER_RAW_CLOSE_DELIMITED"), status, StringComparison.Ordinal);
                Assert.DoesNotContain(Words("COMPOSER_RAW_STATE_COMPLETE"), status, StringComparison.Ordinal);
                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                Assert.Equal((true, RawWriteState.Complete), (row.SentRaw, row.RawRequestWrite));
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // One Yes grants exactly what the plan uses (ComposerOptions): the raw consent covers that image and
        // target only, and each other permission follows its own condition; an unconfirmed plan, and a confirmed plan that
        // is not raw, get no raw consent.
        [Fact]
        public void TheRawConsent_CoversOnlyTheConfirmedImage_AndEachPermissionFollowsItsOwnCondition()
        {
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = transport.Client();
            Assert.True(ReplayRawTarget.TryParse(Target, out ReplayRawTarget target));
            RawRequestImage withBody = Prepare("GET /a HTTP/1.1\r\nHost: 192.0.2.10:8080\r\nContent-Length: 2", "hi", target);
            RawRequestImage withoutBody = Prepare("GET /a HTTP/1.1\r\nHost: 192.0.2.10:8080", "", target);
            ReplayPlanPreview unsafePlan = client.PreviewRaw(withBody, null, CaptureTextSource.Literal, null).Value;
            ReplayPlanPreview safePlan =
                client.PreviewRaw(withoutBody, new Uri("http://captured.example/"), CaptureTextSource.Literal, null).Value;
            ReplayPlanPreview parsedPlan =
                client.Preview(ReplaySource.FromDraft(new RequestReplayDraft { Method = "POST", Url = Target + "/a" }), 0).Value;

            ReplayOptions unsafeOptions = RequestReplayClient.ComposerOptions(0, unsafePlan.Transport, unsafePlan);
            ReplayOptions safeOptions = RequestReplayClient.ComposerOptions(0, safePlan.Transport, safePlan);

            Assert.True(unsafeOptions.AllowRaw.Covers(withBody));
            Assert.False(unsafeOptions.AllowRaw.Covers(withoutBody));
            Assert.Equal(
                (true, false, false),
                (unsafeOptions.AllowUnsafeMethods, unsafeOptions.AllowOriginChange, unsafeOptions.AllowConnectTo));
            Assert.True(safeOptions.AllowRaw.Covers(withoutBody));
            Assert.Equal(
                (false, true, false),
                (safeOptions.AllowUnsafeMethods, safeOptions.AllowOriginChange, safeOptions.AllowConnectTo));
            Assert.Null(RequestReplayClient.ComposerOptions(0, unsafePlan.Transport).AllowRaw);
            Assert.Null(RequestReplayClient.ComposerOptions(0, parsedPlan.Transport, parsedPlan).AllowRaw);
        }

        // A raw plan sent without the user's Yes is refused before anything connects (replay.policy.raw): only a confirmed
        // plan gets its consent (SendPlanAsync).
        [Fact]
        public async Task AnUnconfirmedRawPlan_IsRefused_AndConnectsNowhere()
        {
            var connection = new SeamConnection();
            connection.AfterWrite = _ => connection.Respond("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = Scripted(transport, connection, connects);
            Assert.True(ReplayRawTarget.TryParse(Target, out ReplayRawTarget target));
            ReplayPlanPreview plan = client.PreviewRaw(
                Prepare("GET /a HTTP/1.1\r\nHost: 192.0.2.10:8080", "", target), null, CaptureTextSource.Literal, null).Value;

            RequestReplayBatchResult sent = await client.SendPlanAsync(plan, 0, confirmed: false, CancellationToken.None);

            Assert.False(sent.Result.Succeeded);
            Assert.Equal(RawCodes.PolicyRaw, sent.Result.Diagnostics[0].Code);
            Assert.Empty(connects);
        }

        // Why the operation refused a raw run, as the status says it (RunRefusalText): a Raw mode code in its fixed text,
        // Connect to another endpoint's codes in Raw mode's words, stored text from an unmarked capture in Edit and resend's
        // words, never the command line's flag, and any other code in the operation's own words.
        [Theory]
        [InlineData(RawCodes.PolicyRaw, "COMPOSER_RAW_POLICY")]
        [InlineData(RawCodes.Changed, "COMPOSER_RAW_CHANGED")]
        [InlineData(ReplayOperation.ConnectToHttpsCode, "COMPOSER_RAW_CONNECT_TO_HTTPS")]
        [InlineData(ReplayOperation.ConnectToDeniedCode, "COMPOSER_CONNECT_TO_DENIED")]
        [InlineData(ReplayOperation.StoredTextAmbiguousCode, "REPLAY_CAPTURE_TEXT_AMBIGUOUS")]
        [InlineData("replay.policy.origin-change", null)]
        public void ARawRunsRefusal_IsWordedByItsCode(string code, string key)
        {
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = transport.Client();
            Assert.True(ReplayRawTarget.TryParse(Target, out ReplayRawTarget target));
            ReplayPlanPreview plan = client.PreviewRaw(
                Prepare("GET / HTTP/1.1\r\nHost: 192.0.2.10:8080", "", target), null, CaptureTextSource.Literal, null).Value;
            var diagnostic = new OperationDiagnostic(code, OperationDiagnosticSeverity.Error, "the operation's own words");

            Assert.Equal(
                Words("COMPOSER_ERROR", key == null ? "the operation's own words" : Words(key)),
                RequestComposerWindow.RunRefusalText(diagnostic, plan));
        }

        // Compose's composer: OpenForApplication opens exactly one composer, which records its sends,
        // shown and owned by its argument, and returns it; without an owner it opens nothing.
        [Fact]
        public void OpenForApplication_OpensOneShownComposer_OwnedByItsArgument() => _application.Run(_ =>
        {
            var host = new Window
            {
                Width = 400,
                Height = 300,
                Left = -12000,
                Top = -12000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            host.Show();
            RequestComposerWindow composer = null;
            try
            {
                Window[] before = Application.Current.Windows.Cast<Window>().ToArray();
                Assert.Throws<ArgumentNullException>(() => RequestComposerWindow.OpenForApplication(null));
                Assert.Equal(before, Application.Current.Windows.Cast<Window>());

                composer = RequestComposerWindow.OpenForApplication(host);

                Assert.Same(composer, Assert.Single(Application.Current.Windows.Cast<Window>().Except(before)));
                Assert.True(composer.IsVisible);
                Assert.Same(host, composer.Owner);
                Assert.Same(composer, Assert.Single(host.OwnedWindows.Cast<Window>()));
                Assert.True(composer.HasRecordingTarget);
                Assert.Same(CaptureAppModel.AppModel.ReplayHistory, composer.RecordingTarget.History);
            }
            finally
            {
                composer?.Close();
                host.Close();
            }
        });

        // Compose's composer records where the application does: a raw send from the composer that
        // OpenForApplication opens adds exactly one row to the Replays tab of the application's own model, as a send that
        // XMAT wrote whole, and the history that every composer shows finds that row in that tab. The test then takes the
        // tab, or only its row when the tab was there before, out of the application's model again. The composer's client is
        // its own, so its Parsed route has no transport of the test's: the send's target is a loopback server
        // that the test owns, which a send by any route but the scripted one would reach, and which none reaches.
        [Fact]
        public Task OpenForApplication_RecordsARawSend_InTheApplicationsReplaysTab() => _application.RunAsync(async () =>
        {
            CaptureAppModel application = CaptureAppModel.AppModel;
            Assert.True(application.CanModifyCaptureData);
            await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(503, "Not the scripted route"));
            var connection = new SeamConnection();
            connection.AfterWrite = _ => connection.Respond("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
            var connects = new List<string>();
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient scripted = Scripted(transport, connection, connects);
            ICaptureDeviceContext[] contextsBefore = application.CaptureDeviceContexts.ToArray();
            ICaptureDeviceContext tab = application.GetOrAddReplayContext();
            bool added = !contextsBefore.Contains(tab);
            var replays = (WebServiceDeviceCaptureController)tab.CaptureController;
            ProxyConnectionModel[] rowsBefore = replays.ProxyConnections.ToArray();
            var host = new Window
            {
                Width = 400,
                Height = 300,
                Left = -12000,
                Top = -12000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            host.Show();
            RequestComposerWindow composer = null;
            var questions = new List<string>();
            try
            {
                composer = RequestComposerWindow.OpenForApplication(host);
                composer.Client.OperationFactory = scripted.OperationFactory;
                composer.ShowQuestion = (message, _, _, _) =>
                {
                    questions.Add(message);
                    return MessageBoxResult.Yes;
                };
                await EnterRawModeAsync(composer, "GET", "", server.Origin);

                await composer.SendAsync();

                Assert.Equal(0, server.Connections);
                Assert.Empty(transport.Attempts);
                Assert.Single(questions);
                Assert.Equal("127.0.0.1:" + server.Port, Assert.Single(connects));
                Assert.StartsWith("HTTP 200 | ", Status(composer), StringComparison.Ordinal);
                ProxyConnectionModel row = Assert.Single(replays.ProxyConnections.Except(rowsBefore));
                Assert.Equal((true, RawWriteState.Complete), (row.SentRaw, row.RawRequestWrite));
                ReplayHistoryPreview newest = application.ReplayHistory.Snapshot()[0];
                Assert.True(application.ReplayHistory.TryResolve(
                    newest.Entry, out ICaptureDeviceContext recordedTab, out ProxyConnectionModel recorded));
                Assert.Same(tab, recordedTab);
                Assert.Same(row, recorded);
                Assert.True(application.CanModifyCaptureData);
            }
            finally
            {
                composer?.Close();
                host.Close();
                if (added)
                {
                    await application.RemoveDeviceContextAsync(tab);
                }
                else
                {
                    foreach (ProxyConnectionModel ours in replays.ProxyConnections.Except(rowsBefore).ToList())
                    {
                        replays.ProxyConnections.Remove(ours);
                    }
                }
            }
        });

        // Enters Raw mode as the user does, from a request to origin's /items?id=1 (the scripted target's by default) without
        // headers, which loses nothing.
        private static async Task EnterRawModeAsync(
            RequestComposerWindow window, string method, string body, string origin = Target)
        {
            Find<TextBox>(window, "MethodBox").Text = method;
            Find<TextBox>(window, "UrlBox").Text = origin + "/items?id=1";
            Find<TextBox>(window, "HeadersBox").Text = "";
            Find<TextBox>(window, "BodyBox").Text = body;
            await window.SwitchModeAsync();
            Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
        }

        // A client over transport whose raw sends go to the scripted connection, noting each endpoint that they connect to;
        // every other route of its runs ends at transport, refused at once.
        private static RequestReplayClient Scripted(
            RefusingReplayTransport transport, SeamConnection connection, List<string> connects) =>
            transport.Client(setup => setup with
            {
                Connector = (endpoint, _) =>
                {
                    lock (connects)
                    {
                        connects.Add(endpoint.ToString());
                    }

                    return Task.FromResult<Stream>(connection);
                },
            });

        private static RawRequestImage Prepare(string head, string body, ReplayRawTarget target) =>
            RawRequest.Prepare(head, System.Text.Encoding.ASCII.GetBytes(body), target, CancellationToken.None).Image;

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        // Records into the model's Replays tab, on this test's UI thread.
        private static ReplayRecordingTarget Recording(CaptureAppModel model)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(model, action => dispatcher.Invoke(action));
        }

        private static WebServiceDeviceCaptureController Replays(CaptureAppModel model) =>
            (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }

        private static string Words(string key, params object[] args) =>
            args.Length == 0 ? Localization.GetLocalizedString(key) : Localization.GetLocalizedString(key, args);
    }
}
