// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The Replay menu and the origin that a row of the Replays tab keeps (ReplayProvenance.Origin): Reissue restores
    // it, so a row that would go to another origin than the one that its request was captured or saved for, or first sent
    // to, is sent only after one question, which also counts the unsafe methods, and with each permission from its own
    // count; the new rows keep that origin. Requests go to loopback servers only, and each test records into its own
    // application model.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ReplayMenuProvenanceTests
    {
        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        public ReplayMenuProvenanceTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ReplayMenuProvenanceTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // A row recorded from A and sent to B asks before it goes to B again, and declining sends nothing; accepting sends
        // it to B, and the new row still keeps A.
        [Fact]
        public Task Reissue_OfARowRecordedFromAAndSentToB_AsksFirst_AndTheNewRowKeepsA() => _application.RunAsync(async () =>
        {
            await using var a = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var origin = new Uri(a.Origin + "/items");
            ProxyConnectionModel row = ReplaysRow(b.Port, "/items", origin);
            var questions = new List<string>();
            CaptureConnectionView view = View(model, Answer(questions, false), row);
            try
            {
                Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { row })));

                Assert.Equal(new[] { Words("REPLAY_CONFIRM_ORIGIN", 1L, 1L) }, questions);
                Assert.Empty(b.Requests);

                view.ConfirmReplay = Answer(questions, true);
                ReplayRunResult run = await view.ReissueFromMenuAsync(new ReissueRequest(new[] { row }));

                Assert.True(run.Result.Succeeded);
                Assert.Equal(2, questions.Count);
                Assert.Equal("/items", Assert.Single(b.Requests).Target);
                Assert.Empty(a.Requests);
                ProxyConnectionModel reissued = Assert.Single(Replays(model).ProxyConnections);
                Assert.Equal((ReplaySourceKind.Reissued, origin), (reissued.Replay.Kind, reissued.Replay.Origin));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // Without the permission, the operation refuses such a row before anything is sent, also with every other
        // permission: the menu's count only decides whether to ask, and the operation's own check is the guard.
        [Fact]
        public Task ReissueAsync_WithoutTheOriginPermission_IsRefused_AndSendsNothing() => _application.RunAsync(async () =>
        {
            await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ProxyConnectionModel row = ReplaysRow(b.Port, "/items", new Uri("http://captured.example/items"));
            try
            {
                ReplayRunResult run = await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { row }), "Source tab"),
                    allowUnsafeMethods: true,
                    Recording(model));

                Assert.Equal(OperationOutcome.InvalidInput, run.Result.Outcome);
                Assert.Equal("replay.policy.origin-change", Assert.Single(run.Result.Diagnostics).Code);
                Assert.Empty(b.Requests);
                Assert.Empty(Replays(model).ProxyConnections);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // A row that goes to the origin that it keeps, whatever path that origin was given with, asks nothing for a safe
        // method and is sent.
        [Fact]
        public Task Reissue_OfARowThatGoesToItsOwnOrigin_AsksNothing() => _application.RunAsync(async () =>
        {
            await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var origin = new Uri(b.Origin + "/elsewhere?page=2");
            ProxyConnectionModel row = ReplaysRow(b.Port, "/items", origin);
            CaptureConnectionView view = View(model, NoQuestion, row);
            try
            {
                ReplaySnapshot snapshot = ReplaySnapshot.Of(new ReissueRequest(new[] { row }), "Source tab");
                Assert.Equal(new[] { origin }, snapshot.CapturedOrigins);
                Assert.Equal(0, snapshot.OriginChangeSends);

                ReplayRunResult run = await view.ReissueFromMenuAsync(new ReissueRequest(new[] { row }));

                Assert.True(run.Result.Succeeded);
                Assert.Equal("/items", Assert.Single(b.Requests).Target);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // Rows that change server data and rows that go to another origin are asked about in one question, with both
        // counts, repeats included, and both permissions are given; declining sends nothing.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public Task Reissue_AsksOneQuestionForUnsafeMethodsAndOriginChanges_AndGivesBothPermissions(int repeat) =>
            _application.RunAsync(async () =>
            {
                await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
                var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                var origin = new Uri("http://captured.example/");
                ProxyConnectionModel[] rows =
                {
                    ReplaysRow(b.Port, "/post", origin, "POST"),
                    ReplaysRow(b.Port, "/get", origin),
                    ReplaysRow(b.Port, "/head", origin, "HEAD"),
                };
                var questions = new List<string>();
                CaptureConnectionView view = View(model, Answer(questions, false), rows);
                var request = new ReissueRequest(rows, Repeat: repeat);
                string asked = repeat == 1
                    ? Words("REPLAY_CONFIRM_UNSAFE_ORIGIN", 3L, 1L, 3L)
                    : Words("REPLAY_CONFIRM_UNSAFE_ORIGIN_REPEATED", 6L, 3, 2, 2L, 6L);
                try
                {
                    Assert.Null(await view.ReissueFromMenuAsync(request));

                    Assert.Equal(new[] { asked }, questions);
                    Assert.Empty(b.Requests);

                    view.ConfirmReplay = Answer(questions, true);
                    ReplayRunResult run = await view.ReissueFromMenuAsync(request);

                    Assert.True(run.Result.Succeeded);
                    Assert.Equal(new[] { asked, asked }, questions);
                    Assert.Equal(3 * repeat, b.Requests.Count);
                    Assert.All(Replays(model).ProxyConnections, recorded => Assert.Equal(origin, recorded.Replay.Origin));
                }
                finally
                {
                    await RemoveAllAsync(model);
                }
            });

        // Reissue, Reissue unconditionally, Reissue and verify and Reissue several times ask the same question for the same
        // rows: their options change what is sent and compared, never the origin check.
        [Fact]
        public Task EveryReissueItem_AsksTheSameForTheSameRows() => _application.RunAsync(async () =>
        {
            await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ProxyConnectionModel row = ReplaysRow(b.Port, "/items", new Uri("https://captured.example/items"));
            var questions = new List<string>();
            CaptureConnectionView view = View(model, Answer(questions, false), row);
            try
            {
                foreach (ReissueRequest request in new[]
                {
                    new ReissueRequest(new[] { row }),
                    new ReissueRequest(new[] { row }, Unconditional: true),
                    new ReissueRequest(new[] { row }, Verify: true),
                    new ReissueRequest(new[] { row }, Repeat: 5),
                })
                {
                    Assert.Null(await view.ReissueFromMenuAsync(request));
                }

                string once = Words("REPLAY_CONFIRM_ORIGIN", 1L, 1L);
                Assert.Equal(
                    new[] { once, once, once, Words("REPLAY_CONFIRM_ORIGIN_REPEATED", 5L, 1, 5, 5L) },
                    questions);
                Assert.Empty(b.Requests);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // Rows of other tabs keep no origin: the run restores none and asks only about the methods that can change server
        // data, as before.
        [Fact]
        public void RowsOfOtherTabs_RestoreNoOrigin_AndAskOnlyAboutUnsafeMethods() => _application.Run(_ =>
        {
            ProxyConnectionModel[] rows = { Row("http", "127.0.0.1:9", "/read"), Row("http", "127.0.0.1:9", "/write", "POST") };

            ReplaySnapshot snapshot = ReplaySnapshot.Of(new ReissueRequest(rows), "Source tab");

            Assert.Null(snapshot.CapturedOrigins);
            Assert.Equal(0, snapshot.OriginChangeSends);
            Assert.Equal(Words("REPLAY_CONFIRM_UNSAFE", 2L, 1L), CaptureConnectionView.ConfirmationMessage(snapshot));
        });

        // The menu's count of origin changes agrees with the operation's guard, row by row, whatever the URL's form (a path
        // at the row's authority, or an absolute URL), its scheme, its host's case and its port: it counts exactly the sends
        // whose ChangesOrigin the operation's Preview reports, repeats included.
        [Fact]
        public void OriginChangeSends_AgreesWithTheOperationsGuard_ForAMixOfRows()
        {
            ProxyConnectionModel[] rows =
            {
                Kept(Row("http", "127.0.0.1:8080", "/same"), "http://127.0.0.1:8080/other"),
                Kept(Row("http", "127.0.0.1:8080", "/port"), "http://127.0.0.1:8081/port"),
                Kept(Row("http", "127.0.0.1:8080", "/scheme"), "https://127.0.0.1:8080/scheme"),
                Kept(Row("http", "Example.COM", "/case"), "http://example.com/"),
                Kept(Row("https", "example.net:443", "/default-port"), "https://example.net/"),
                Kept(Row("http", "127.0.0.1:8080", "http://other.example:8080/absolute"), "http://127.0.0.1:8080/"),
                Kept(Row("http", "127.0.0.1:8080", "http://127.0.0.1:8080/absolute"), "http://127.0.0.1:8080/"),
                Row("http", "127.0.0.1:8080", "/captured"),
            };

            ReplaySnapshot snapshot = ReplaySnapshot.Of(new ReissueRequest(rows, Repeat: 2), "Source tab");

            var operation = new ReplayOperation();
            long guarded = 0;
            for (int index = 0; index < rows.Length; index++)
            {
                RequestReplayDraft draft = RequestReplayDraft.FromCapture(rows[index]);
                Uri origin = snapshot.CapturedOrigins[index];
                OperationResult<ReplayPlanPreview> preview = operation.Preview(
                    ReplaySource.FromDraft(origin == null ? draft : draft.WithCapturedOrigin(origin)));
                Assert.True(preview.Succeeded, rows[index].Path);
                guarded += preview.Value.ChangesOrigin ? 2 : 0;
            }

            Assert.Equal(6, guarded);
            Assert.Equal(guarded, snapshot.OriginChangeSends);
        }

        // A row whose absolute target is an IPv6 address with a zone: the menu counts its origin change from the
        // destination that the operation makes the row's URL from, zone and all (ReplayDestination.ReadCaptureDestination),
        // as the draft's ChangesOrigin does; so a row restored for that same address and zone counts none, and one restored
        // for another zone counts one. Reissue refuses both before it asks (RefusesCaptureDestinations), so the count is
        // never shown for them.
        [Fact]
        public void OriginChangeSends_KeepTheZoneOfAnAbsoluteTarget_AsTheDraftDoes()
        {
            const string Target = "http://[fe80::1%25Ethernet]:8081/scoped";
            ProxyConnectionModel[] rows =
            {
                Kept(Row("http", "127.0.0.1:8080", Target), "http://[fe80::1%25Ethernet]:8081/"),
                Kept(Row("http", "127.0.0.1:8080", Target), "http://[fe80::1%25Other]:8081/"),
            };

            ReplaySnapshot snapshot = ReplaySnapshot.Of(new ReissueRequest(rows), "Source tab");

            Assert.Equal(
                new[] { false, true },
                rows.Select(row => RequestReplayDraft.FromCapture(row).WithCapturedOrigin(row.Replay.Origin).ChangesOrigin));
            Assert.Equal(1, snapshot.OriginChangeSends);
        }

        // Before its question, Reissue reads only each row's destination, as far as the shared decision does, never its
        // whole target: rows whose targets are megabytes of non-ASCII and escaped text, at the row's authority or absolute,
        // cost no more to count (ReplaySnapshot.Of) or to check (CheckCaptureDestinations) than their scheme and authority,
        // and count as the operation counts them; a row whose destination exceeds the size limit counts as none, since the
        // check refuses it before the question. Measured on this thread around each step alone, after the rows are made and
        // once both steps have run on small rows.
        [Fact]
        public void BeforeItsQuestion_ReissueReadsOnlyEachRowsDestination_NotItsWholeTarget()
        {
            const string Origin = "http://127.0.0.1:8080/";
            const int Bound = 1024 * 1024;
            string tail = new string('\u00e9', 512 * 1024) + string.Concat(Enumerable.Repeat("%C3%A9", 128 * 1024));
            ProxyConnectionModel[] Rows(string text) => new[]
            {
                Kept(Row("http", "127.0.0.1:8080", "/" + text), Origin),
                Kept(Row("http", "127.0.0.1:8080", "http://other.example:8080/" + text), Origin),
                Kept(Row("http", "127.0.0.1:8080", "http://127.0.0.1:8080/?" + text), Origin),
                Kept(Row("http", "127.0.0.1:8080", "http://" + new string('a', 9000) + "/" + text), Origin),
            };
            ReplaySnapshot small = ReplaySnapshot.Of(new ReissueRequest(Rows("x")), "Source tab");
            Assert.NotNull(ReplayOperation.CheckCaptureDestinations(small.Captures));
            var request = new ReissueRequest(Rows(tail));

            long before = GC.GetAllocatedBytesForCurrentThread();
            ReplaySnapshot snapshot = ReplaySnapshot.Of(request, "Source tab");
            long counted = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            OperationDiagnostic check = ReplayOperation.CheckCaptureDestinations(snapshot.Captures);
            long checkedOnly = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(1, small.OriginChangeSends);
            Assert.Equal(1, snapshot.OriginChangeSends);
            Assert.Equal(ReplayOperation.RequestMalformedCode, check.Code);
            Assert.True(counted < Bound, $"Counting allocated {counted} bytes.");
            Assert.True(checkedOnly < Bound, $"Checking allocated {checkedOnly} bytes.");
        }

        // A request file: a row sent from a file opened for A, after its URL was changed to B, keeps A, the origin that
        // the file was saved for, and Reissue asks before sending it to B again, in the operation's words; declining sends
        // nothing. A row of a file sent to its own origin asks nothing.
        [Fact]
        public Task ARowOfARequestFile_KeepsTheFilesOrigin_AndReissueAsksBeforeAnotherOrigin() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            await using var a = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var moved = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), Recording(model));
            var kept = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                string elsewhere = Path.Join(directory, "elsewhere.xmat-request.json");
                File.WriteAllText(elsewhere, RequestFileJson(a.Origin + "/from-file"));
                moved.ChooseRequestFile = () => elsewhere;
                await moved.OpenRequestAsync();
                Find<TextBox>(moved, "UrlBox").Text = b.Origin + "/from-file";
                await moved.SendAsync();

                Assert.Equal(new[] { Words("COMPOSER_CONFIRM_MESSAGE", "GET", b.Origin + "/from-file") }, questions);
                ProxyConnectionModel sent = Assert.Single(Replays(model).ProxyConnections);
                Assert.Equal(
                    (ReplaySourceKind.RequestFile, new Uri(a.Origin + "/from-file")), (sent.Replay.Kind, sent.Replay.Origin));

                var asked = new List<string>();
                CaptureConnectionView view = View(model, Answer(asked, false), sent);
                Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { sent })));

                string question = Assert.Single(asked);
                Assert.Equal(Words("REPLAY_CONFIRM_ORIGIN", 1L, 1L), question);
                Assert.Contains("captured or saved for, or first sent to", question);
                Assert.Single(b.Requests);

                string own = Path.Join(directory, "own.xmat-request.json");
                File.WriteAllText(own, RequestFileJson(b.Origin + "/own"));
                kept.ChooseRequestFile = () => own;
                await kept.OpenRequestAsync();
                await kept.SendAsync();
                ProxyConnectionModel ownRow = Replays(model).ProxyConnections[^1];
                Assert.Equal(new Uri(b.Origin + "/own"), ownRow.Replay.Origin);
                view.ConfirmReplay = NoQuestion;
                ReplayRunResult run = await view.ReissueFromMenuAsync(new ReissueRequest(new[] { ownRow }));

                Assert.True(run.Result.Succeeded);
                Assert.Equal(new[] { "/from-file", "/own", "/own" }, b.Requests.Select(request => request.Target));
                Assert.Empty(a.Requests);
            }
            finally
            {
                moved.Close();
                kept.Close();
                await RemoveAllAsync(model);
                Directory.Delete(directory, recursive: true);
            }
        });

        private static string Words(string key, params object[] args) => Localization.GetLocalizedString(key, args);

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        private static T Find<T>(System.Windows.Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        // A row of another tab, as a capture records it, without a Host line: the URL is the row's own.
        private static ProxyConnectionModel Row(string scheme, string hostAndPort, string path, string method = "GET") =>
            new(7, "200", DateTime.UtcNow, TimeSpan.Zero, scheme, hostAndPort, method, path,
                $"{method} {path} HTTP/1.1\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");

        // The row as a row of the Replays tab that keeps origin.
        private static ProxyConnectionModel Kept(ProxyConnectionModel row, string origin)
        {
            row.Replay = new ReplayProvenance(ReplaySourceKind.Edited, "Source tab", row.RequestNumber, Origin: new Uri(origin));
            return row;
        }

        // A row of the Replays tab that sends to the loopback port and keeps origin, as one that a send from the composer
        // recorded.
        private static ProxyConnectionModel ReplaysRow(int port, string path, Uri origin, string method = "GET")
        {
            var row = new ProxyConnectionModel(
                7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{port}", method, path,
                $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n", method == "POST" ? "x"u8.ToArray() : [],
                "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
            row.Replay = new ReplayProvenance(ReplaySourceKind.Edited, "Source tab", 7, Origin: origin);
            return row;
        }

        // A view of these rows whose Replay menu records into model's Replays tab and asks confirm; it tells nothing.
        private static CaptureConnectionView View(
            CaptureAppModel model, Func<string, bool> confirm, params ProxyConnectionModel[] rows)
        {
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), rows);
            view.ReplayTarget = Recording(model);
            view.ConfirmReplay = confirm;
            view.ShowReplayMessage = (_, _) => { };
            return view;
        }

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

        // A content request file for a GET of url, as XMAT saves one.
        private static string RequestFileJson(string url) =>
            "{ \"schema\": \"xmat-request/1\", \"profile\": \"content\", \"method\": \"GET\", \"url\": \"" + url + "\", " +
            "\"httpVersion\": \"auto\", \"headers\": [[\"X-From\", \"file\"]], \"body\": \"\", \"omitted\": [] }";

        private static string FixtureDirectory()
        {
            string directory = Path.Join(AppContext.BaseDirectory, "ReplayMenuFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
