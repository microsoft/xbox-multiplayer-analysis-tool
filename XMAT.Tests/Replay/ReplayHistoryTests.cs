// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The composer's history (ReplayHistory): the rows that this session's Replays tab recorded, by weak reference only,
    // checked by reference each time it is read, revoked by Clear and eviction, and bounded in what it shows. Each test
    // records into its own application model, never the application's, and sends only to loopback servers.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ReplayHistoryTests
    {
        private const string SourceTab = "Source tab";

        private readonly CaptureUiApplicationFixture _application;

        public ReplayHistoryTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ReplayHistoryTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // H1: every row that the recorder commits is an entry, whatever it comes from and however it went: a composed
        // request, a reissued one, one that got no response and one whose body the tab did not keep. Newest first, in the
        // order recorded; only Load needs the body.
        [Fact]
        public Task History_AdmitsEveryRowThatTheRecorderCommits_NewestFirst() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                ProxyConnectionModel composed = Assert.Single(Record(model, ReplaySourceKind.Composed, Answered("/composed")));
                ProxyConnectionModel reissued = Assert.Single(Record(model, ReplaySourceKind.Reissued, Answered("/reissued")));
                ProxyConnectionModel failed = Assert.Single(Record(model, ReplaySourceKind.Edited, Unanswered("/failed")));
                ProxyConnectionModel notKept = Assert.Single(Record(
                    model, ReplaySourceKind.Composed, 4, Answered("/not-kept", "0123456789"u8.ToArray())));

                IReadOnlyList<ReplayHistoryPreview> previews = model.ReplayHistory.Snapshot();

                Assert.Equal(4, model.ReplayHistory.Count);
                Assert.Equal(new[] { notKept, failed, reissued, composed }, previews.Select(Resolve(model)));
                Assert.Equal(new[] { "/not-kept", "/failed", "/reissued", "/composed" }, Paths(previews));
                Assert.Equal(new long[] { 4, 3, 2, 1 }, previews.Select(preview => preview.RequestNumber));
                Assert.Equal(new[] { false, true, true, true }, previews.Select(preview => preview.CanLoad));
                Assert.Equal(new[] { "200", "", "200", "200" }, previews.Select(preview => preview.StatusPreview));
                Assert.All(previews, preview => Assert.Equal(CaptureAppModel.ReplayContextName, preview.TabNamePreview));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H1: a send that the tab did not take makes no entry: one that finds its tab closed, and one that the UI thread no
        // longer takes, as when XMAT exits.
        [Fact]
        public Task History_AdmitsNoSendThatWasNotRecorded() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                using (var cancellation = new CancellationTokenSource())
                using (ReplayRecording recording = Target(model).TryBegin(cancellation, _ => ReplayProvenance.Composed))
                {
                    model.CaptureDeviceContexts.Remove(recording.Destination);
                    recording.Recorder.Add(Sent(1), Answered("/closed"));
                    Assert.Equal((0, 1), (recording.Recorder.Recorded, recording.Recorder.NotRecorded));
                }

                ICaptureDeviceContext replays = model.GetOrAddReplayContext();
                var recorder = new ReplayRecorder(
                    (WebServiceDeviceCaptureController)replays.CaptureController,
                    null,
                    CaptureConnectionView.UiDispatch(StoppedDispatcher()),
                    _ => ReplayProvenance.Composed,
                    recorded: row => model.ReplayHistory.Add(replays, row));
                recorder.Add(Sent(1), Answered("/exiting"));

                Assert.Equal((0, 1), (recorder.Recorded, recorder.NotRecorded));
                Assert.Equal(0, model.ReplayHistory.Count);
                Assert.Empty(model.ReplayHistory.Snapshot());
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H1: a cancelled run keeps the entries of the rows that it recorded, also when the cancel comes as a row is recorded;
        // a run cancelled before it sends records no row and makes no entry.
        [Fact]
        public Task History_KeepsTheEntriesOfTheRowsThatACancelledRunRecorded() => _application.RunAsync(async () =>
        {
            var held = new TaskCompletionSource<LoopbackResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new LoopbackHttpServer(request =>
                request.Target == "/three" ? held.Task : Task.FromResult(LoopbackResponse.Ok(request.Target)));
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ProxyConnectionModel[] captures =
            {
                Capture(1, server.Port, "/one"), Capture(2, server.Port, "/two"), Capture(3, server.Port, "/three"),
            };
            try
            {
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    await RunAsync(model, captures, cancellation);
                }

                Assert.Equal(0, model.ReplayHistory.Count);
                Assert.Empty(server.Requests);

                using (var cancellation = new CancellationTokenSource())
                {
                    Task run = RunAsync(model, captures, cancellation);
                    await WaitUntilAsync(() => server.Requests.Any(request => request.Target == "/three"));
                    cancellation.Cancel();
                    await run;
                }

                Assert.Equal(new[] { "/two", "/one" }, Paths(model.ReplayHistory.Snapshot()));

                ProxyConnectionsCollection rows = ReplaysOf(model).ProxyConnections;
                int sent = server.Requests.Count;
                using (var cancellation = new CancellationTokenSource())
                {
                    void CancelOnRow(object sender, NotifyCollectionChangedEventArgs e)
                    {
                        if (e.Action == NotifyCollectionChangedAction.Add)
                        {
                            cancellation.Cancel();
                        }
                    }

                    rows.CollectionChanged += CancelOnRow;
                    try
                    {
                        await RunAsync(model, captures, cancellation);
                    }
                    finally
                    {
                        rows.CollectionChanged -= CancelOnRow;
                    }
                }

                Assert.Equal(new[] { "/one", "/two", "/one" }, Paths(model.ReplayHistory.Snapshot()));
                Assert.Equal(3, rows.Count);
                Assert.Equal(sent + 1, server.Requests.Count);
            }
            finally
            {
                held.TrySetResult(LoopbackResponse.Ok());
                await RemoveAllAsync(model);
            }
        });

        // H1: a view hears of a new entry only once its row is in its tab and the recorder has counted it: what Snapshot
        // gives right then includes the row, which is the recorder's last.
        [Fact]
        public Task History_IsToldOfARowOnlyOnceItIsInItsTabAndCounted() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                using var cancellation = new CancellationTokenSource();
                using ReplayRecording recording = Target(model).TryBegin(cancellation, _ => ReplayProvenance.Composed);
                var seen = new List<(ProxyConnectionModel Newest, ProxyConnectionModel Last, int Recorded, int Live)>();
                using IDisposable subscription = model.ReplayHistory.Observe(() =>
                {
                    IReadOnlyList<ReplayHistoryPreview> live = model.ReplayHistory.Snapshot();
                    seen.Add((Resolve(model)(live[0]), recording.Recorder.LastRecorded, recording.Recorder.Recorded, live.Count));
                });

                recording.Recorder.Add(Sent(1), Answered("/one"));
                recording.Recorder.Add(Sent(2), Answered("/two"));

                ProxyConnectionModel[] rows = ReplaysOf(model).ProxyConnections.ToArray();
                Assert.Equal(new[] { (rows[0], rows[0], 1, 1), (rows[1], rows[1], 2, 2) }, seen);
                Assert.Equal(2, model.ReplayHistory.Count);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H2: the latest 200 recorded rows: the 201st forgets the oldest, whose entry no longer resolves although its row
        // stays in Replays.
        [Fact]
        public Task History_KeepsTheLatest200_AndEvictionRevokesTheOldest() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                Record(model, ReplaySourceKind.Reissued, Enumerable.Range(1, 200).Select(number => Answered($"/{number}")).ToArray());
                ReplayHistoryPreview oldest = model.ReplayHistory.Snapshot()[^1];
                Assert.True(model.ReplayHistory.TryResolve(oldest.Entry, out _, out ProxyConnectionModel first));

                Record(model, ReplaySourceKind.Reissued, Answered("/201"));

                Assert.Equal(200, model.ReplayHistory.Count);
                Assert.Equal(
                    Enumerable.Range(2, 200).Reverse().Select(number => $"/{number}"),
                    Paths(model.ReplayHistory.Snapshot()));
                Assert.False(model.ReplayHistory.TryResolve(oldest.Entry, out _, out _));
                Assert.Contains(first, ReplaysOf(model).ProxyConnections);
                Assert.Equal(201, ReplaysOf(model).ProxyConnections.Count);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H3: an entry lives as long as its row is in its tab, by reference: closing the tab, or the rows leaving it,
        // expires entries, and a new Replays tab with the same name and the same numbers never resolves an earlier entry.
        [Fact]
        public Task History_ExpiresWithItsTabOrRows_ByReference() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                Record(model, ReplaySourceKind.Composed, Answered("/one"), Answered("/two"));
                ICaptureDeviceContext first = model.GetOrAddReplayContext();
                IReadOnlyList<ReplayHistoryPreview> earlier = model.ReplayHistory.Snapshot();

                await model.RemoveDeviceContextAsync(first);

                Assert.Empty(model.ReplayHistory.Snapshot());
                Assert.All(earlier, preview => Assert.False(model.ReplayHistory.TryResolve(preview.Entry, out _, out _)));

                IReadOnlyList<ProxyConnectionModel> rows = Record(model, ReplaySourceKind.Composed, Answered("/one"), Answered("/two"));
                ICaptureDeviceContext second = model.GetOrAddReplayContext();
                IReadOnlyList<ReplayHistoryPreview> later = model.ReplayHistory.Snapshot();

                Assert.NotSame(first, second);
                Assert.Equal(first.DeviceName, second.DeviceName);
                Assert.Equal(
                    earlier.Select(preview => (preview.TabNamePreview, preview.RequestNumber, preview.UrlPreview)),
                    later.Select(preview => (preview.TabNamePreview, preview.RequestNumber, preview.UrlPreview)));
                Assert.All(earlier, preview => Assert.False(model.ReplayHistory.TryResolve(preview.Entry, out _, out _)));
                Assert.Equal(rows.Reverse(), later.Select(Resolve(model)));

                ReplaysOf(model).ProxyConnections.RemoveAll();

                Assert.Contains(second, model.CaptureDeviceContexts);
                Assert.Empty(model.ReplayHistory.Snapshot());
                Assert.All(later, preview => Assert.False(model.ReplayHistory.TryResolve(preview.Entry, out _, out _)));

                // A tab that leaves the application's tabs with its rows still in it, as one removed other than by closing it
                // (CaptureAppModel.GetOrAddReplayContext), expires its entries too.
                Record(model, ReplaySourceKind.Composed, Answered("/kept"));
                ICaptureDeviceContext third = model.GetOrAddReplayContext();
                IReadOnlyList<ReplayHistoryPreview> unlisted = model.ReplayHistory.Snapshot();
                Assert.Single(unlisted);
                model.CaptureDeviceContexts.Remove(third);

                Assert.Single(RowsOf(third));
                Assert.Empty(model.ReplayHistory.Snapshot());
                Assert.False(model.ReplayHistory.TryResolve(unlisted[0].Entry, out _, out _));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H4: the history keeps neither the rows nor their tab alive: once the tab is closed and the test lets go of them,
        // they are collected while the model and its history live on, the history still holding its two entries.
        [Fact]
        public Task History_KeepsNoRowOrTabAlive() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            WeakReference[] recorded = RecordWeakly(model);
            await CloseReplaysAsync(model);

            await CollectAsync(recorded);

            Assert.All(recorded, reference => Assert.False(reference.IsAlive));
            Assert.Equal(2, model.ReplayHistory.Count);
            Assert.Empty(model.ReplayHistory.Snapshot());
        });

        // H5: nothing that the history holds reaches a row, a tab, a collection of them, captured text or a preview, other
        // than through a weak reference or the model that owns it: before a Snapshot, after one, and once a view's
        // subscription is disposed.
        [Fact]
        public Task History_HoldsNothingButWeakReferences() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                Record(model, ReplaySourceKind.Composed, Answered("/one"), Answered("/two"));
                AssertHoldsNoContent(model);

                Assert.Equal(2, model.ReplayHistory.Snapshot().Count);
                AssertHoldsNoContent(model);

                using (model.ReplayHistory.Observe(() => { }))
                {
                    Record(model, ReplaySourceKind.Composed, Answered("/three"));
                }

                AssertHoldsNoContent(model);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H6: Clear forgets every entry, so none resolves, and leaves the Replays tab's rows and bodies as they were.
        [Fact]
        public Task Clear_RevokesEveryEntry_AndKeepsTheRows() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                IReadOnlyList<ProxyConnectionModel> rows = Record(
                    model, ReplaySourceKind.Composed, Answered("/one", "body"u8.ToArray()), Answered("/two"));
                byte[][] bodies = rows.Select(row => row.RequestBody).ToArray();
                IReadOnlyList<ReplayHistoryPreview> shown = model.ReplayHistory.Snapshot();

                model.ReplayHistory.Clear();

                Assert.Equal(0, model.ReplayHistory.Count);
                Assert.Empty(model.ReplayHistory.Snapshot());
                Assert.All(shown, preview => Assert.False(model.ReplayHistory.TryResolve(preview.Entry, out _, out _)));
                Assert.Equal(rows, ReplaysOf(model).ProxyConnections);
                Assert.Equal(bodies, ReplaysOf(model).ProxyConnections.Select(row => row.RequestBody));
                Assert.Equal("body"u8.ToArray(), rows[0].RequestBody);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H7: an entry resolves only while it is still the history's, XMAT is not exiting and its row is still in its tab,
        // checked when asked: Clear revokes what a view still shows, so does a row's expiry after the view was built, and so
        // does shutdown.
        [Fact]
        public Task TryResolve_ChecksMembershipShutdownAndLiveness_WhenAsked() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                Record(model, ReplaySourceKind.Composed, Answered("/one"));
                IReadOnlyList<ReplayHistoryPreview> shown = model.ReplayHistory.Snapshot();
                model.ReplayHistory.Clear();
                Assert.False(model.ReplayHistory.TryResolve(shown[0].Entry, out _, out _));
                Assert.False(model.ReplayHistory.TryResolve(null, out _, out _));
                Assert.Single(ReplaysOf(model).ProxyConnections);

                Record(model, ReplaySourceKind.Composed, Answered("/two"));
                shown = model.ReplayHistory.Snapshot();
                ReplaysOf(model).ProxyConnections.RemoveAll();
                Assert.False(model.ReplayHistory.TryResolve(shown[0].Entry, out _, out _));
                Assert.Equal(0, model.ReplayHistory.Count);

                ProxyConnectionModel recorded = Assert.Single(Record(model, ReplaySourceKind.Composed, Answered("/three")));
                shown = model.ReplayHistory.Snapshot();
                Assert.True(model.ReplayHistory.TryResolve(shown[0].Entry, out ICaptureDeviceContext tab, out ProxyConnectionModel row));
                Assert.Same(model.GetOrAddReplayContext(), tab);
                Assert.Same(recorded, row);

                model.BeginShutdown();

                Assert.False(model.ReplayHistory.TryResolve(shown[0].Entry, out tab, out row));
                Assert.Null(tab);
                Assert.Null(row);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H8: Show in Replays selects the very row in its own tab, as Inspect session does; it selects nothing for a copy of
        // the row, a tab that is not the application's, or once XMAT is exiting.
        [Fact]
        public Task TrySelect_SelectsTheRowByReference_AndRefusesWhatIsGone() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                IReadOnlyList<ProxyConnectionModel> rows = Record(model, ReplaySourceKind.Composed, Answered("/one"), Answered("/two"));
                ICaptureDeviceContext replays = model.GetOrAddReplayContext();
                ReplayRecordingTarget target = Target(model);
                var unlisted = new CaptureDeviceContextModel(
                    DeviceType.Unknown, "Unlisted", CaptureType.WebProxy, true, null, hostListPolicy: model.HostListPolicy);

                Assert.Same(model.ReplayHistory, target.History);
                Assert.True(target.TrySelect(replays, rows[0]));
                Assert.Same(replays, model.SelectedDeviceContext);
                Assert.Same(rows[0], ReplaysOf(model).SelectedConnectionModel);

                model.SelectedDeviceContext = null;
                Assert.False(target.TrySelect(replays, new ProxyConnectionModel(rows[1])));
                Assert.False(target.TrySelect(unlisted, rows[1]));
                Assert.Null(model.SelectedDeviceContext);
                Assert.Same(rows[0], ReplaysOf(model).SelectedConnectionModel);

                model.BeginShutdown();
                Assert.False(target.TrySelect(replays, rows[1]));
                Assert.Null(model.SelectedDeviceContext);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H9: every preview is escaped and cut to its bound, its ellipsis included, however long or odd the row's text.
        [Fact]
        public Task Snapshot_BoundsEveryPreview() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                ICaptureDeviceContext tab = AddTab(model, new string('T', 300));
                var row = new ProxyConnectionModel(
                    7, new string('5', 40), DateTime.UtcNow, TimeSpan.Zero, "http", "example.test", new string('M', 100),
                    "/" + new string('p', 600), "GET / HTTP/1.1\r\n\r\n", [], "HTTP/1.1 200 OK\r\n\r\n", [], "127.0.0.1");
                var odd = new ProxyConnectionModel(
                    8, "2\u202E0", DateTime.UtcNow, TimeSpan.Zero, "http", "example.test", "GE\u0001T", "/a\u0007b",
                    "GET / HTTP/1.1\r\n\r\n", [], "HTTP/1.1 200 OK\r\n\r\n", [], "127.0.0.1");
                foreach (ProxyConnectionModel added in new[] { row, odd })
                {
                    RowsOf(tab).Add(added);
                    model.ReplayHistory.Add(tab, added);
                }

                IReadOnlyList<ReplayHistoryPreview> previews = model.ReplayHistory.Snapshot();
                ReplayHistoryPreview shown = previews[1];

                Assert.Equal(new string('T', ReplayHistory.MaxTabNamePreviewChars - 1) + "\u2026", shown.TabNamePreview);
                Assert.Equal(new string('M', ReplayHistory.MaxMethodPreviewChars - 1) + "\u2026", shown.MethodPreview);
                Assert.Equal(new string('5', ReplayHistory.MaxStatusPreviewChars - 1) + "\u2026", shown.StatusPreview);
                Assert.Equal(ReplayHistory.MaxUrlPreviewChars, shown.UrlPreview.Length);
                Assert.StartsWith("http://example.test/ppp", shown.UrlPreview);
                Assert.EndsWith("p\u2026", shown.UrlPreview);
                Assert.Equal(
                    ("GE\\u0001T", "http://example.test/a\\u0007b", "2\\u202E0"),
                    (previews[0].MethodPreview, previews[0].UrlPreview, previews[0].StatusPreview));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H9: a Snapshot costs what it shows, not what its rows hold: rows whose path, request line or host has 16 Mi
        // characters, read cold, allocate under 1 MiB, and their header dictionaries are not made.
        [Fact]
        public Task Snapshot_AllocatesWhatItShows_NotWhatItsRowsHold() => _application.RunAsync(async () =>
        {
            var warm = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                ICaptureDeviceContext warmTab = AddTab(warm, "Warm");
                foreach (ProxyConnectionModel small in new[]
                {
                    Row("/w", "warm.test", "GET /w HTTP/1.1\r\nX: 1\r\n"),
                    Row("/w2", new string('h', 300), "GET /w2 HTTP/1.1\r\n\r\n"),
                })
                {
                    RowsOf(warmTab).Add(small);
                    warm.ReplayHistory.Add(warmTab, small);
                }

                Assert.Equal(2, warm.ReplayHistory.Snapshot().Count);

                string huge = new('a', 16 * 1024 * 1024);
                ProxyConnectionModel longPath = Row("/" + huge, "example.test", "GET /short HTTP/1.1\r\nHost: example.test\r\n");
                ProxyConnectionModel longRequestLine = Row("/short", "example.test", "GET /" + huge + " HTTP/1.1\r\nHost: example.test\r\n");
                ProxyConnectionModel longHost = Row("/short", huge, "GET /short HTTP/1.1\r\n\r\n");
                ICaptureDeviceContext tab = AddTab(model, "Huge");
                foreach (ProxyConnectionModel row in new[] { longPath, longRequestLine, longHost })
                {
                    RowsOf(tab).Add(row);
                    model.ReplayHistory.Add(tab, row);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                IReadOnlyList<ReplayHistoryPreview> previews = model.ReplayHistory.Snapshot();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.Equal(3, previews.Count);
                Assert.True(allocated < 1024 * 1024, $"A Snapshot of the huge rows allocated {allocated} bytes.");
                Assert.All(new[] { longPath, longRequestLine, longHost }, row => Assert.False(row.HeaderCollectionsMaterialized));
                Assert.All(previews, preview => Assert.True(preview.UrlPreview.Length <= ReplayHistory.MaxUrlPreviewChars));
            }
            finally
            {
                await RemoveAllAsync(model);
                await RemoveAllAsync(warm);
            }
        });

        // H10: the URL that a preview shows is where Load sends the request: the port only when it is not the default, an
        // IPv6 host in brackets, a path that is a URL as stored, and, for a scheme that no sent URL has, the parts as stored,
        // without any scheme when it is unknown. Where nothing is escaped or cut, a preview of a request that can be sent is
        // the URL of the draft that Load makes.
        [Theory]
        [InlineData("http", "127.0.0.1:8080", "/a?b=c", "http://127.0.0.1:8080/a?b=c", true)]
        [InlineData("http", "example.test:80", "/a", "http://example.test/a", true)]
        [InlineData("https", "example.test:443", "/a", "https://example.test/a", true)]
        [InlineData("http", "[::1]:8080", "/v6", "http://[::1]:8080/v6", true)]
        [InlineData("https", "[2001:db8::1]", "relative", "https://[2001:db8::1]/relative", true)]
        [InlineData("http", "example.test", "HTTP://Other.TEST/stored", "HTTP://Other.TEST/stored", false)]
        [InlineData("ftp", "example.test:21", "/f", "ftp://example.test:21/f", false)]
        [InlineData("ftp", "[::1]:21", "/f", "ftp://[::1]:21/f", false)]
        [InlineData("", "example.test:8080", "/a", "example.test:8080/a", false)]
        [InlineData("", "[::1]:8080", "/v6", "[::1]:8080/v6", false)]
        [InlineData("", "example.test", "*", "example.test/*", false)]
        [InlineData("", "", "/a", "/a", false)]
        [InlineData("", ":8080", "/a", "/a", false)]
        public Task UrlPreview_NamesWhereLoadSends(string scheme, string authority, string path, string expected, bool sameAsLoad) =>
            _application.RunAsync(async () =>
            {
                var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                try
                {
                    ICaptureDeviceContext tab = AddTab(model, "Tab");
                    var row = new ProxyConnectionModel(
                        1, "200", DateTime.UtcNow, TimeSpan.Zero, scheme, authority, "GET", path, "GET / HTTP/1.1\r\n\r\n", [],
                        "HTTP/1.1 200 OK\r\n\r\n", [], "127.0.0.1");
                    RowsOf(tab).Add(row);
                    model.ReplayHistory.Add(tab, row);

                    string shown = Assert.Single(model.ReplayHistory.Snapshot()).UrlPreview;

                    Assert.Equal(expected, shown);
                    if (sameAsLoad)
                    {
                        Assert.Equal(RequestReplayDraft.FromCapture(row).Url, shown);
                    }
                }
                finally
                {
                    await RemoveAllAsync(model);
                }
            });

        // H10: a host too long for any URL that was sent is shown as stored, cut to the bound, without asking Uri.
        [Fact]
        public Task UrlPreview_ShowsAHostTooLongForAUrlAsStored() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                ICaptureDeviceContext tab = AddTab(model, "Tab");
                ProxyConnectionModel row = Row("/a", new string('h', 300) + ":8080", "GET /a HTTP/1.1\r\n\r\n");
                RowsOf(tab).Add(row);
                model.ReplayHistory.Add(tab, row);

                Assert.Equal(
                    "http://" + new string('h', 300) + ":8080/a",
                    Assert.Single(model.ReplayHistory.Snapshot()).UrlPreview);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H11: a view's subscription is told of every change that can change what Snapshot shows: an entry added (the 201st
        // evicting the oldest), Clear, rows leaving a tab that entries name and that tab leaving the application's tabs. Once
        // disposed it is told of nothing; it lets go of the view's callback even while the view still holds it, and nothing
        // that it watched, a tab's rows included, holds it.
        [Fact]
        public Task Observe_ReportsEveryChange_AndLetsGoWhenDisposed() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                Record(model, ReplaySourceKind.Composed, Answered("/one"));
                var changes = new StrongBox<int>();
                var subscription = new IDisposable[1];
                WeakReference callback = ObserveWeakly(model.ReplayHistory, changes, subscription);

                Record(model, ReplaySourceKind.Composed, Answered("/two"));
                Assert.Equal(1, changes.Value);
                Record(model, ReplaySourceKind.Reissued, Enumerable.Range(3, 199).Select(number => Answered($"/{number}")).ToArray());
                Assert.Equal(200, changes.Value);
                Record(model, ReplaySourceKind.Reissued, Answered("/evicting"));
                Assert.Equal(201, changes.Value);
                Assert.Equal(200, model.ReplayHistory.Count);

                model.ReplayHistory.Clear();
                Assert.Equal(202, changes.Value);

                Record(model, ReplaySourceKind.Composed, Answered("/after-clear"));
                Assert.Equal(203, changes.Value);
                ReplaysOf(model).ProxyConnections.RemoveAll();
                Assert.Equal(204, changes.Value);

                // The tab leaves the application's tabs with its rows still in it: only the tab list changes.
                Record(model, ReplaySourceKind.Composed, Answered("/unlisted"));
                Assert.Equal(205, changes.Value);
                model.CaptureDeviceContexts.Remove(model.GetOrAddReplayContext());
                Assert.Equal(206, changes.Value);

                // Disposed while it watches a tab whose rows the history names.
                Record(model, ReplaySourceKind.Composed, Answered("/watched"));
                int disposedAt = changes.Value;
                WeakReference disposed = DisposeWeakly(subscription);
                Record(model, ReplaySourceKind.Composed, Answered("/after-dispose"));
                model.ReplayHistory.Clear();
                ReplaysOf(model).ProxyConnections.RemoveAll();
                model.CaptureDeviceContexts.Remove(model.GetOrAddReplayContext());

                Assert.Equal(disposedAt, changes.Value);
                await CollectAsync(callback);
                Assert.False(callback.IsAlive, "A disposed subscription that its view still holds kept the view's callback.");

                subscription[0] = null;
                await CollectAsync(disposed);
                Assert.False(disposed.IsAlive, "Something that the subscription watched still holds it after Dispose.");
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H11: when a tab's rows are replaced by another collection while a view watches, the view is told and its
        // subscription follows the collection that holds the rows that live entries name: a change to the new collection is
        // reported, one to the old collection is not. Once the last live entry has expired, the view hears of nothing more
        // from that tab's rows, which no longer change what Snapshot shows.
        [Fact]
        public Task Observe_FollowsAReplacedCollection_WhileEntriesNameItsRows() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                Record(model, ReplaySourceKind.Composed, Answered("/one"));
                ICaptureDeviceContext replays = model.GetOrAddReplayContext();
                WebServiceDeviceCaptureController controller = ReplaysOf(model);
                ProxyConnectionsCollection old = controller.ProxyConnections;
                ProxyConnectionModel loaded = Row("/loaded", "example.test", "GET /loaded HTTP/1.1\r\n\r\n");
                ProxyConnectionModel other = Row("/other", "example.test", "GET /other HTTP/1.1\r\n\r\n");
                WebServiceDeviceCaptureController source = LoadedWith(loaded, other);
                // An entry for a row that only the coming collection holds: it is live once that collection is the tab's.
                model.ReplayHistory.Add(replays, loaded);
                var changes = new StrongBox<int>();
                using IDisposable subscription = model.ReplayHistory.Observe(() => changes.Value++);

                controller.ReplaceLoadedCapturesFrom(source);

                Assert.True(changes.Value > 0);
                Assert.Same(loaded, Assert.Single(model.ReplayHistory.Snapshot().Select(Resolve(model))));
                int replaced = changes.Value;
                old.RemoveAll();
                Assert.Equal(replaced, changes.Value);

                controller.ProxyConnections.Remove(loaded);
                Assert.Equal(replaced + 1, changes.Value);
                Assert.Empty(model.ReplayHistory.Snapshot());

                controller.ProxyConnections.Remove(other);
                controller.ProxyConnections.RemoveAll();
                Assert.Equal(replaced + 1, changes.Value);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // H11: a view that still watches lets go of a collection that replaced rows left: the old collection and its rows are
        // collected while the subscription is active.
        [Fact]
        public Task Observe_LetsGoOfAReplacedCollection() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                Record(model, ReplaySourceKind.Composed, Answered("/one"), Answered("/two"));
                var changes = new StrongBox<int>();
                using IDisposable subscription = model.ReplayHistory.Observe(() => changes.Value++);
                WeakReference[] old = ReplaceWeakly(ReplaysOf(model));

                await CollectAsync(old);

                Assert.All(old, reference => Assert.False(reference.IsAlive));
                Assert.True(changes.Value > 0);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // Records one send per exchange in model's Replays tab through the recorder of a GUI run (ReplayRecordingTarget),
        // each with provenance of kind, and returns the rows that the tab gained.
        private static IReadOnlyList<ProxyConnectionModel> Record(
            CaptureAppModel model, ReplaySourceKind kind, params ReplayExchange[] exchanges) =>
            Record(model, kind, ReplayRecorder.DefaultBodyBudgetBytes, exchanges);

        private static IReadOnlyList<ProxyConnectionModel> Record(
            CaptureAppModel model, ReplaySourceKind kind, long bodyBudgetBytes, params ReplayExchange[] exchanges)
        {
            using var cancellation = new CancellationTokenSource();
            using ReplayRecording recording = Target(model, bodyBudgetBytes).TryBegin(
                cancellation, result => new ReplayProvenance(kind, SourceTab, result.RequestNumber));
            Assert.NotNull(recording);
            ProxyConnectionsCollection rows = ((WebServiceDeviceCaptureController)recording.Destination.CaptureController).ProxyConnections;
            int before = rows.Count;
            int number = 0;
            foreach (ReplayExchange exchange in exchanges)
            {
                recording.Recorder.Add(Sent(++number), exchange);
            }

            return rows.Skip(before).ToList();
        }

        // A Replay menu run of captures, recorded in model's Replays tab, until it ends or cancellation stops it.
        private static async Task RunAsync(
            CaptureAppModel model, IReadOnlyList<ProxyConnectionModel> captures, CancellationTokenSource cancellation)
        {
            ReplaySnapshot snapshot = ReplaySnapshot.Of(new ReissueRequest(captures), SourceTab);
            using ReplayRecording recording = Target(model).TryBegin(cancellation, snapshot.ProvenanceOf);
            Assert.NotNull(recording);
            try
            {
                await new ReplayOperation().ExecuteAsync(
                    new ReplayRequest(ReplaySource.FromCaptures(snapshot.Captures), new ReplayOptions(), null, recording.Recorder),
                    OperationContext.None,
                    cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Stopped, as Cancel request stops a run.
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] RecordWeakly(CaptureAppModel model)
        {
            IReadOnlyList<ProxyConnectionModel> rows = Record(model, ReplaySourceKind.Composed, Answered("/one"), Answered("/two"));
            ICaptureDeviceContext tab = model.GetOrAddReplayContext();
            return new[]
            {
                new WeakReference(tab), new WeakReference(tab.CaptureController), new WeakReference(rows[0]),
                new WeakReference(rows[1]),
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task CloseReplaysAsync(CaptureAppModel model) => model.RemoveDeviceContextAsync(model.GetOrAddReplayContext());

        // Observes history with a callback that counts changes, held only by the subscription, which goes in holder; returns
        // a weak reference to the callback.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference ObserveWeakly(ReplayHistory history, StrongBox<int> changes, IDisposable[] holder)
        {
            Action changed = () => changes.Value++;
            holder[0] = history.Observe(changed);
            return new WeakReference(changed);
        }

        // Disposes the subscription in holder, which keeps it, and returns a weak reference to it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference DisposeWeakly(IDisposable[] holder)
        {
            holder[0].Dispose();
            return new WeakReference(holder[0]);
        }

        // Replaces controller's rows with an empty collection, as loading a capture does, and returns weak references to the
        // former collection and its rows.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] ReplaceWeakly(WebServiceDeviceCaptureController controller)
        {
            ProxyConnectionsCollection old = controller.ProxyConnections;
            WeakReference[] references = old.Select(row => new WeakReference(row)).Append(new WeakReference(old)).ToArray();
            controller.ReplaceLoadedCapturesFrom(LoadedWith());
            return references;
        }

        // A loaded tab's controller with these rows, whose rows another controller can take (ReplaceLoadedCapturesFrom).
        private static WebServiceDeviceCaptureController LoadedWith(params ProxyConnectionModel[] rows)
        {
            var loaded = new WebServiceDeviceCaptureController("Loaded", DeviceType.Unknown, true, new ProxyPortPool());
            foreach (ProxyConnectionModel row in rows)
            {
                loaded.ProxyConnections.Add(row);
            }

            return loaded;
        }

        private static void AssertHoldsNoContent(CaptureAppModel model)
        {
            List<object> reached = Reachable(model.ReplayHistory, model);
            Assert.Contains(reached, item => item is ReplayHistoryEntry);
            Assert.DoesNotContain(reached, item =>
                item is ProxyConnectionModel or ICaptureDeviceContext or IDeviceCaptureController or ProxyConnectionsCollection or
                    string or byte[] or ReplayProvenance or ReplayHistoryPreview or Delegate);
        }

        // Every object that root reaches through fields and array elements, not through the boundary, a weak reference or
        // reflection's own objects.
        private static List<object> Reachable(object root, object boundary)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<object>();
            var reached = new List<object>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                object current = pending.Pop();
                if (current == null || ReferenceEquals(current, boundary) || current is MemberInfo or Pointer ||
                    !seen.Add(current))
                {
                    continue;
                }

                reached.Add(current);
                Type type = current.GetType();
                if (type.IsPrimitive || type.IsEnum || current is string || current is WeakReference ||
                    (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(WeakReference<>)))
                {
                    continue;
                }

                if (current is Array array)
                {
                    if (!type.GetElementType().IsPrimitive)
                    {
                        foreach (object item in array)
                        {
                            pending.Push(item);
                        }
                    }

                    continue;
                }

                for (Type declaring = type; declaring != null; declaring = declaring.BaseType)
                {
                    foreach (FieldInfo field in declaring.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!field.FieldType.IsPrimitive && !field.FieldType.IsPointer)
                        {
                            pending.Push(field.GetValue(current));
                        }
                    }
                }
            }

            return reached;
        }

        private static ReplayRecordingTarget Target(CaptureAppModel model, long bodyBudgetBytes = ReplayRecorder.DefaultBodyBudgetBytes)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(model, action => dispatcher.Invoke(action), _ => null, bodyBudgetBytes);
        }

        private static Func<ReplayHistoryPreview, ProxyConnectionModel> Resolve(CaptureAppModel model) =>
            preview => model.ReplayHistory.TryResolve(preview.Entry, out _, out ProxyConnectionModel row) ? row : null;

        private static IEnumerable<string> Paths(IEnumerable<ReplayHistoryPreview> previews) =>
            previews.Select(preview => new Uri(preview.UrlPreview).AbsolutePath);

        private static WebServiceDeviceCaptureController ReplaysOf(CaptureAppModel model) =>
            (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;

        private static ProxyConnectionsCollection RowsOf(ICaptureDeviceContext tab) =>
            ((WebServiceDeviceCaptureController)tab.CaptureController).ProxyConnections;

        // A read-only tab of model's own, which entries can name as they name the Replays tab.
        private static ICaptureDeviceContext AddTab(CaptureAppModel model, string name)
        {
            var tab = new CaptureDeviceContextModel(
                DeviceType.Unknown, name, CaptureType.WebProxy, true, null, hostListPolicy: model.HostListPolicy);
            model.CaptureDeviceContexts.Add(tab);
            return tab;
        }

        private static ProxyConnectionModel Row(string path, string hostAndPort, string requestLineAndHeaders) =>
            new(1, "200", DateTime.UtcNow, TimeSpan.Zero, "http", hostAndPort, "GET", path, requestLineAndHeaders, [],
                "HTTP/1.1 200 OK\r\n\r\n", [], "127.0.0.1");

        private static ProxyConnectionModel Capture(long number, int port, string path) =>
            new(number, "200", DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{port}", "GET", path,
                $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");

        // A send as ReplayOperation passes it to its sink.
        private static ReplayItemResult Sent(int number) =>
            new(number, null, number, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null);

        private static ReplayExchange Answered(string path, byte[] requestBody = null) => new()
        {
            Outcome = ReplayOutcome.Completed,
            Request = new ReplayMessage(
                $"GET {path} HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", "127.0.0.1:9") }, null, requestBody),
            Response = new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()),
            RequestUri = new Uri("http://127.0.0.1:9" + path),
            StatusCode = 200,
            Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        // A send that got no response.
        private static ReplayExchange Unanswered(string path) => new()
        {
            Outcome = ReplayOutcome.Failed,
            Request = new ReplayMessage(
                $"GET {path} HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", "127.0.0.1:9") }, null, null),
            RequestUri = new Uri("http://127.0.0.1:9" + path),
            Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), "The condition did not come true.");
                await Task.Delay(10);
            }
        }

        // Collects until nothing references the targets any more, at most three times, letting finished work leave its
        // threads in between.
        private static async Task CollectAsync(params WeakReference[] references)
        {
            for (int attempt = 0; attempt < 3 && references.Any(reference => reference.IsAlive); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(50);
            }
        }

        // A dispatcher that has shut down, as XMAT's has once it exits.
        private static Dispatcher StoppedDispatcher()
        {
            Dispatcher dispatcher = null;
            using var started = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                started.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            dispatcher.InvokeShutdown();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
            return dispatcher;
        }

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }
    }
}
