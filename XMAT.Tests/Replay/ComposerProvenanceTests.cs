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

namespace XMAT.Tests.Replay
{
    // One provenance for every mode (O4, an explicit change for Parsed mode): every row that the Replays tab records keeps
    // the origin that its send gives, the one that its request was captured or saved for, or first sent to; Load, a drop
    // and Edit and resend restore it, so a request captured for A and sent to B asks again before it goes to B or to C. A
    // row that keeps no origin keeps its own URL's. Requests go to loopback servers only, and each test records into its
    // own application model.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerProvenanceTests
    {
        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        public ComposerProvenanceTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerProvenanceTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // The recorder gives every row the origin that its send gives (ReplayExchange.ProvenanceOrigin), a Parsed or a raw
        // send's, whatever the run's own provenance says; a composed request's send gives the URL that it was first sent to.
        [Fact]
        public Task EveryRecordedRow_KeepsTheOriginThatItsSendGives() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var composer = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                var captured = new Uri("https://captured.example/items");
                var target = new Uri("http://192.0.2.10:8080/");
                ProxyConnectionModel[] rows = Record(
                    Recording(model),
                    Exchange(9, "/parsed", captured),
                    Exchange(9, "/raw", target, raw: true),
                    Exchange(9, "/none"));

                Assert.Equal(new[] { captured, target, null }, rows.Select(row => row.Replay.Origin));
                Assert.All(rows, row => Assert.Equal(ReplaySourceKind.Composed, row.Replay.Kind));

                Find<TextBox>(composer, "UrlBox").Text = server.Origin + "/composed?page=2";
                await composer.SendAsync();

                Assert.Equal(new Uri(server.Origin + "/composed?page=2"), Replays(model).ProxyConnections[^1].Replay.Origin);
            }
            finally
            {
                composer.Close();
                await RemoveAllAsync(model);
            }
        });

        // A request captured for A and sent to B, with consent, keeps A in its row. Load, a drop and Edit and resend of that
        // row restore A, so a send to B asks the origin question again, and so does a send to C; each is sent only with that
        // consent, and its row keeps A in turn.
        [Theory]
        [InlineData("load")]
        [InlineData("drop")]
        [InlineData("edit")]
        public Task ARequestCapturedForAAndSentToB_AsksAgainBeforeBOrC_OnceItsRowIsTakenBack(string how) =>
            _application.RunAsync(async () =>
            {
                await using var a = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
                await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
                await using var c = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
                using var client = new RequestReplayClient();
                var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                var questions = new List<string>();
                var origin = new Uri(a.Origin + "/items");
                var first = new RequestComposerWindow(
                    Capture(a.Port, "/items"), client, Answer(questions, true), Recording(model), "Source tab");
                RequestComposerWindow second = null;
                try
                {
                    Find<TextBox>(first, "UrlBox").Text = b.Origin + "/items";
                    await first.SendAsync();
                    ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                    Assert.Equal(origin, row.Replay.Origin);

                    second = await TakeAsync(how, row, model, client, Answer(questions, true));
                    Assert.Equal(b.Origin + "/items", Url(second));
                    await second.SendAsync();
                    Find<TextBox>(second, "UrlBox").Text = c.Origin + "/items";
                    await second.SendAsync();

                    Assert.Equal(
                        new[]
                        {
                            Words("COMPOSER_CONFIRM_MESSAGE", "GET", b.Origin + "/items"),
                            Words("COMPOSER_CONFIRM_MESSAGE", "GET", b.Origin + "/items"),
                            Words("COMPOSER_CONFIRM_MESSAGE", "GET", c.Origin + "/items"),
                        },
                        questions);
                    Assert.Equal(2, b.Requests.Count);
                    Assert.Single(c.Requests);
                    Assert.Empty(a.Requests);
                    Assert.Equal(3, Replays(model).ProxyConnections.Count);
                    Assert.All(Replays(model).ProxyConnections, recorded => Assert.Equal(origin, recorded.Replay.Origin));
                }
                finally
                {
                    first.Close();
                    second?.Close();
                    await RemoveAllAsync(model);
                }
            });

        // A row that keeps the origin of its own URL, as a composed request's send records it, and a row that keeps none,
        // as a saved and reopened capture's rows: taken back by Load, a drop or Edit and resend, a send to that URL asks
        // nothing about origins, and a send to another origin asks. No origin never erases the row's own.
        [Theory]
        [InlineData("load", true)]
        [InlineData("load", false)]
        [InlineData("drop", true)]
        [InlineData("drop", false)]
        [InlineData("edit", true)]
        [InlineData("edit", false)]
        public Task ARowOfItsOwnOriginOrOfNone_AsksOnlyBeforeAnotherOrigin(string how, bool kept) =>
            _application.RunAsync(async () =>
            {
                await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
                await using var c = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
                using var client = new RequestReplayClient();
                var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                var questions = new List<string>();
                ProxyConnectionModel row = Assert.Single(
                    Record(Recording(model), Exchange(b.Port, "/items", kept ? new Uri(b.Origin + "/items") : null)));
                RequestComposerWindow composer = null;
                try
                {
                    composer = await TakeAsync(how, row, model, client, Answer(questions, true));
                    Assert.Equal(b.Origin + "/items", Url(composer));
                    await composer.SendAsync();
                    Find<TextBox>(composer, "UrlBox").Text = c.Origin + "/items";
                    await composer.SendAsync();

                    Assert.Equal(new[] { Words("COMPOSER_CONFIRM_MESSAGE", "GET", c.Origin + "/items") }, questions);
                    Assert.Single(b.Requests);
                    Assert.Single(c.Requests);
                }
                finally
                {
                    composer?.Close();
                    await RemoveAllAsync(model);
                }
            });

        // Raw mode keeps where a request comes from: captured for A, edited as raw text, given B's target and edited as parsed
        // again, it still asks the origin question before it goes to B.
        [Fact]
        public Task ARequestCapturedForA_TakenThroughRawModeToB_AsksBeforeB() => _application.RunAsync(async () =>
        {
            await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            var composer = new RequestComposerWindow(Capture(9, "/items"), client, Answer(questions, false));
            try
            {
                var versions = Find<ComboBox>(composer, "HttpVersionBox");
                versions.SelectedItem = versions.Items.Cast<ComboBoxItem>().Single(item => ReplayHttpVersion.Http11.Equals(item.Tag));
                await composer.SwitchModeAsync();
                var head = Find<TextBox>(composer, "RawHeadBox");
                Find<TextBox>(composer, "RawTargetBox").Text = b.Origin;
                head.Text = head.Text.Replace("Host: 127.0.0.1:9", "Host: 127.0.0.1:" + b.Port, StringComparison.Ordinal);
                await composer.SwitchModeAsync();
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, composer.Mode);
                Assert.Equal(b.Origin + "/items", Url(composer));
                Assert.Empty(questions);

                await composer.SendAsync();

                Assert.Equal(new[] { Words("COMPOSER_CONFIRM_MESSAGE", "GET", b.Origin + "/items") }, questions);
                Assert.Empty(b.Requests);
            }
            finally
            {
                composer.Close();
            }
        });

        // A composer with the request of a row of the Replays tab, as Load, a drop or Edit and resend gives it.
        private static async Task<RequestComposerWindow> TakeAsync(
            string how, ProxyConnectionModel row, CaptureAppModel model, RequestReplayClient client, Func<string, bool> confirm)
        {
            if (how == "edit")
            {
                return new RequestComposerWindow(row, client, confirm, Recording(model), CaptureAppModel.ReplayContextName);
            }

            RequestComposerWindow composer = RequestComposerWindow.ForNewRequest(client, confirm, Recording(model));
            if (how == "load")
            {
                await composer.LoadFromHistoryAsync(EntryOf(model, row));
            }
            else
            {
                using ComposerSessionDragToken drag = ComposerSessionDrag.Begin(row, CaptureAppModel.ReplayContextName);
                composer.DropSession(drag.Data);
            }

            return composer;
        }

        // The history's entry of row.
        private static ReplayHistoryEntry EntryOf(CaptureAppModel model, ProxyConnectionModel row) =>
            model.ReplayHistory.Snapshot()
                .Single(preview => model.ReplayHistory.TryResolve(preview.Entry, out _, out ProxyConnectionModel found) &&
                    ReferenceEquals(found, row))
                .Entry;

        // Records each send into the target's Replays tab, as a run of the composer would, and returns their rows.
        private static ProxyConnectionModel[] Record(ReplayRecordingTarget target, params ReplayExchange[] exchanges)
        {
            using var cancellation = new CancellationTokenSource();
            using ReplayRecording recording = target.TryBegin(cancellation, _ => ReplayProvenance.Composed);
            Assert.NotNull(recording);
            var rows = new ProxyConnectionModel[exchanges.Length];
            for (int index = 0; index < exchanges.Length; index++)
            {
                ReplayExchange exchange = exchanges[index];
                recording.Recorder.Add(
                    new ReplayItemResult(
                        index + 1, null, null, 1, exchange.Outcome, exchange.StatusCode, TimeSpan.Zero, 5, 0, exchange.Code, null,
                        Raw: exchange.Raw,
                        RequestBytesWritten: exchange.RequestBytesWritten,
                        RequestWrite: exchange.RequestWrite,
                        ResponseFraming: exchange.ResponseFraming),
                    exchange);
                rows[index] = recording.Recorder.LastRecorded;
            }

            return rows;
        }

        // A GET to the loopback port that got its response; a raw one handed every byte to the connection.
        private static ReplayExchange Exchange(int port, string target, Uri provenanceOrigin = null, bool raw = false) => new()
        {
            Outcome = ReplayOutcome.Completed,
            Request = new ReplayMessage(
                $"GET {target} HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", $"127.0.0.1:{port}") }, null, null),
            Response = new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()),
            RequestUri = new Uri($"http://127.0.0.1:{port}{target}"),
            StatusCode = 200,
            Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ProvenanceOrigin = provenanceOrigin,
            Raw = raw,
            RequestWrite = raw ? RawWriteState.Complete : null,
            RequestBytesWritten = raw ? 40 : null,
            ResponseFraming = raw ? RawResponseReader.ContentLengthFraming : null,
        };

        // A captured GET to the loopback port.
        private static ProxyConnectionModel Capture(int port, string path) =>
            new(7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{port}", "GET", path,
                $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");

        private static string Words(string key, params object[] args) => Localization.GetLocalizedString(key, args);

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Url(Window window) => Find<TextBox>(window, "UrlBox").Text;

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
    }
}
