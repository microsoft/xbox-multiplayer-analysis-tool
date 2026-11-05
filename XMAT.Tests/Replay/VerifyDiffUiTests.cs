// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text;
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
    // View verification differences (Slice H): the Replays tab keeps each verified send's verdict with its row and, within
    // its budget, the differences that came with that very verdict; the window shows the record of exactly the row that it
    // was opened for, sends nothing, words every code, says when differences were not kept or are unavailable, and lets go
    // of the record once the row leaves its tab. Requests go to loopback servers only, and each test records into its own
    // application model.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class VerifyDiffUiTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        // Enough arguments for the placeholders of any string.
        private static readonly object[] AnyArguments = { 1, 2, 3, 4, 5, 6, 7 };

        private static readonly VerifyDiffSide CapturedSide =
            new(VerifyBodyBasis.Decoded, VerifyFraming.Removed, VerifyDecodeEnd.Complete, 12, 12, "utf-8");

        private static readonly VerifyDiffSide ReplayedSide =
            new(VerifyBodyBasis.Plain, VerifyFraming.None, VerifyDecodeEnd.NotApplicable, 20, 20, "utf-8");

        private readonly CaptureUiApplicationFixture _application;

        public VerifyDiffUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static VerifyDiffUiTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // Every limitation, JSON explanation and JSON unavailability code, and every value of each enum of the differences,
        // has en-us words of its own.
        [Fact]
        public void EveryCodeAndValueOfTheDifferences_HasWordsOfItsOwn()
        {
            AssertWorded(Codes(typeof(VerifyDiffLimitation)), VerifyDiffWording.LimitationKey);
            AssertWorded(Codes(typeof(VerifyJsonExplanation)), VerifyDiffWording.ExplanationKey);
            AssertWorded(Codes(typeof(VerifyJsonUnavailable)), VerifyDiffWording.JsonUnavailableKey);
            AssertWorded(Enum.GetValues<VerifyDiffState>(), VerifyDiffWording.Key);
            AssertWorded(Enum.GetValues<VerifyBodyBasis>(), VerifyDiffWording.Key);
            AssertWorded(Enum.GetValues<VerifyFraming>(), VerifyDiffWording.Key);
            AssertWorded(Enum.GetValues<VerifyDecodeEnd>(), VerifyDiffWording.Key);
            AssertWorded(Enum.GetValues<VerifyLineKind>(), VerifyDiffWording.Key);
            AssertWorded(Enum.GetValues<VerifyJsonChangeKind>(), VerifyDiffWording.Key);
            AssertWorded(Enum.GetValues<VerifyEquivalence>(), VerifyDiffWording.TextKey);
            AssertWorded(Enum.GetValues<VerifyEquivalence>(), VerifyDiffWording.JsonKey);
        }

        // The tab keeps differences while they fit its budget (16 MiB by default) counted as their serialized size, the
        // rows that it already has included, and the rest are not kept; every row keeps the verdict that its send got, and
        // its note says whether it matched and when its differences were not kept. Clearing the tab makes room again.
        [Fact]
        public void TheTab_KeepsDifferencesWithinItsBudget_EarlierRunsIncluded_AndEveryVerdict()
        {
            var controller = new WebServiceDeviceCaptureController(
                CaptureAppModel.ReplayContextName, DeviceType.Unknown, true, new ProxyPortPool());
            VerifyDiff kept = TextDiff("first", 3000);
            VerifyDiff tooLarge = Diff(new ReplayVerification(new[] { ReplayVerifier.StatusDifference }), null, 2001);
            VerifyDiff fitsExactly = Diff(new ReplayVerification(Array.Empty<string>()), Equal(), 2000);
            ReplayRecorder first = Recorder(controller, budget: 5000);
            Add(first, kept);
            Add(first, tooLarge);
            Add(first, fitsExactly);

            ProxyConnectionModel[] rows = controller.ProxyConnections.ToArray();
            Assert.Equal(new[] { kept, null, fitsExactly }, rows.Select(row => row.Verification.Diff));
            Assert.Same(kept.Verification, rows[0].Verification.Verdict);
            Assert.Same(tooLarge.Verification, rows[1].Verification.Verdict);
            Assert.Same(fitsExactly.Verification, rows[2].Verification.Verdict);
            Assert.Equal(new int?[] { null, 2001, null }, rows.Select(row => row.Verification.NotKeptBytes));
            Assert.All(rows, row => Assert.True(row.IsVerified));
            Assert.Equal((5000L, 1), (first.RetainedVerifyDiffBytes, first.OmittedVerifyDiffs));
            Assert.Equal(
                Words("REPLAY_NOTE_VERIFY_DIFFERED", Words("REPLAY_DIFFERENCE_STATUS")) + " " +
                    Words("REPLAY_NOTE_VERIFY_DIFF_NOT_KEPT", 2001, 0L),
                rows[1].ReplayNote);
            Assert.Equal(Words("REPLAY_NOTE_VERIFY_MATCHED"), rows[2].ReplayNote);

            // A later run counts the differences that the tab keeps: one byte more does not fit.
            ReplayRecorder second = Recorder(controller, budget: 5000);
            Assert.Equal(5000L, second.RetainedVerifyDiffBytes);
            Add(second, TextDiff("second", 1));
            Assert.Null(controller.ProxyConnections[3].Verification.Diff);
            Assert.Equal(1, second.OmittedVerifyDiffs);

            controller.ProxyConnections.RemoveAll();
            ReplayRecorder third = Recorder(controller, budget: 5000);
            Assert.Equal(0L, third.RetainedVerifyDiffBytes);
            VerifyDiff afterClearing = TextDiff("third", 5000);
            Add(third, afterClearing);
            Assert.Same(afterClearing, Assert.Single(controller.ProxyConnections).Verification.Diff);
        }

        // A row keeps only differences that came with its own send's verdict; it keeps its verdict without differences
        // when they came without them, or with another verdict's; and a send that was not verified has no verification,
        // whatever its exchange carries. None of them uses the budget.
        [Fact]
        public void ARow_KeepsOnlyTheDifferencesOfItsOwnVerdict()
        {
            var controller = new WebServiceDeviceCaptureController(
                CaptureAppModel.ReplayContextName, DeviceType.Unknown, true, new ProxyPortPool());
            ReplayRecorder recorder = Recorder(controller, budget: 5000);
            var verdict = new ReplayVerification(new[] { ReplayVerifier.BodyDifference });
            ReplayExchange another = Answered();
            another.SetVerifyDiff(TextDiff("another verdict's", 100));
            ReplayExchange unverified = Answered();
            unverified.SetVerifyDiff(TextDiff("unverified", 100));

            recorder.Add(Sent(verdict), another);
            recorder.Add(Sent(verdict), Answered());
            recorder.Add(Sent(null), unverified);

            ProxyConnectionModel[] rows = controller.ProxyConnections.ToArray();
            Assert.All(rows.Take(2), row =>
            {
                Assert.Same(verdict, row.Verification.Verdict);
                Assert.Equal(((VerifyDiff)null, (int?)null), (row.Verification.Diff, row.Verification.NotKeptBytes));
            });
            Assert.Null(rows[2].Verification);
            Assert.False(rows[2].IsVerified);
            Assert.Null(rows[2].ReplayNote);
            Assert.Equal((0L, 0), (recorder.RetainedVerifyDiffBytes, recorder.OmittedVerifyDiffs));
        }

        // Reissue and verify asks for the differences of every verified send, and each row keeps those of its own send,
        // with the verdict that the run reports for it; the summary says where to see them. In the Replays tab, the row menu
        // of a row opens that row's differences, also while another row is selected, and the button in the request details
        // the selected row's; opening them sends nothing.
        [Fact]
        public Task ReissueAndVerify_KeepsEachSendsOwnDifferences_AndViewingThemSendsNothing() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(request =>
                LoopbackResponse.Ok(request.Target == "/same" ? "same body" : "replayed line"));
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ProxyConnectionModel same = Capture(1, server.Port, "/same", "same body");
            ProxyConnectionModel changed = Capture(2, server.Port, "/changed", "captured line");
            CaptureConnectionView source = HostListUiTests.CaptureView(new HostListPolicy(), same, changed);
            source.ReplayTarget = Target(model);
            var messages = new List<string>();
            source.ShowReplayMessage = (message, _) => messages.Add(message);
            var opened = new List<VerifyDiffWindow>();
            Window host = null;
            try
            {
                ReplayRunResult run = await source.ReissueFromMenuAsync(new ReissueRequest(new[] { same, changed }, Verify: true));

                Assert.Equal((1, 1, 0), (run.Result.Value.Matched, run.Result.Value.Mismatched, run.OmittedVerifyDiffs));
                WebServiceDeviceCaptureController replays = ReplaysOf(model);
                ProxyConnectionModel sameRow = Assert.Single(replays.ProxyConnections, row => row.Path == "/same");
                ProxyConnectionModel changedRow = Assert.Single(replays.ProxyConnections, row => row.Path == "/changed");
                foreach ((ProxyConnectionModel row, long number) in new[] { (sameRow, 1L), (changedRow, 2L) })
                {
                    ReplayVerification verdict =
                        Assert.Single(run.Result.Value.Items, sent => sent.RequestNumber == number).Verification;
                    Assert.Same(verdict, row.Verification.Verdict);
                    Assert.Same(verdict, row.Verification.Diff.Verification);
                }

                Assert.True(sameRow.Verification.Diff.Body.Equal);
                VerifyTextHunk hunk = Assert.Single(changedRow.Verification.Diff.Body.Text.Hunks);
                Assert.Contains(hunk.Lines, line => line.Kind == VerifyLineKind.Removed && line.Text == "captured line");
                Assert.Contains(hunk.Lines, line => line.Kind == VerifyLineKind.Added && line.Text == "replayed line");
                Assert.Equal(Words("REPLAY_NOTE_VERIFY_DIFFERED", Words("REPLAY_DIFFERENCE_BODY")), changedRow.ReplayNote);
                Assert.Contains(Words("REPLAY_VERIFY_DIFF_HINT", replays.DeviceName), Assert.Single(messages));
                Assert.Equal(2, server.Requests.Count);

                (host, CaptureConnectionView view, DataGrid grid) = await ShowReplaysAsync(model);
                view.ShowVerifyDiff = opened.Add;
                grid.SelectedItem = sameRow;
                await SettleAsync();
                ContextMenu menu = HostListUiTests.Open(HostListUiTests.Row(grid, changedRow));
                MenuItem item = Assert.Single(
                    menu.Items.OfType<MenuItem>(), entry => entry.Command == WebServiceProxyCommands.ViewVerifyDiff);
                Assert.Same(changedRow, item.CommandParameter);
                Assert.Same(view, item.CommandTarget);
                WebServiceProxyCommands.ViewVerifyDiff.Execute(item.CommandParameter, view);
                menu.IsOpen = false;
                var button = (Button)view.FindName("ViewVerifyDiffButton");
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.Same(sameRow, button.CommandParameter);
                ((RoutedCommand)button.Command).Execute(button.CommandParameter, button.CommandTarget);

                Assert.Equal(2, opened.Count);
                await Task.WhenAll(opened.Select(window => window.Presented)).WaitAsync(Patience);
                Assert.Same(changedRow.Verification, opened[0].Shown);
                Assert.Same(sameRow.Verification, opened[1].Shown);
                Assert.Contains("- captured line", Shown(opened[0], "BodyDiffText"));
                Assert.Contains("+ replayed line", Shown(opened[0], "BodyDiffText"));
                Assert.Equal(Words("VERIFY_DIFF_BODY_EQUAL"), Shown(opened[1], "BodyDiffText"));
                Assert.Equal(Words("VERIFY_DIFF_MATCHED"), Shown(opened[1], "SummaryText").Split(Environment.NewLine)[0]);
                Assert.Equal(2, server.Requests.Count);
            }
            finally
            {
                foreach (VerifyDiffWindow window in opened)
                {
                    window.Close();
                }

                host?.Close();
                await RemoveAllAsync(model);
            }
        });

        // Differences that do not fit the tab's budget are not kept, but every send is recorded with its verdict: the run's
        // message says how many were not kept, which alone makes it a warning.
        [Fact]
        public Task ReissueAndVerify_RecordsEveryVerdict_AndSaysHowManyDifferencesItDidNotKeep() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("replayed"));
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ProxyConnectionModel[] captures = { Capture(1, server.Port, "/one", "replayed"), Capture(2, server.Port, "/two", "replayed") };
            CaptureConnectionView source = HostListUiTests.CaptureView(new HostListPolicy(), captures);
            source.ReplayTarget = Target(model, verifyDiffBudgetBytes: 1);
            var messages = new List<(string Text, MessageBoxImage Image)>();
            source.ShowReplayMessage = (text, image) => messages.Add((text, image));
            try
            {
                ReplayRunResult run = await source.ReissueFromMenuAsync(new ReissueRequest(captures, Verify: true));

                Assert.Equal(
                    (2, 0, 2, 1L),
                    (run.Result.Value.Matched, run.Result.Value.Mismatched, run.OmittedVerifyDiffs, run.VerifyDiffBudgetBytes));
                WebServiceDeviceCaptureController replays = ReplaysOf(model);
                Assert.Equal(2, replays.ProxyConnections.Count);
                Assert.All(replays.ProxyConnections, row =>
                {
                    Assert.True(row.Verification.Verdict.Matches);
                    Assert.Null(row.Verification.Diff);
                    Assert.True(row.Verification.NotKeptBytes > 0);
                    Assert.Equal(
                        Words("REPLAY_NOTE_VERIFY_MATCHED") + " " +
                            Words("REPLAY_NOTE_VERIFY_DIFF_NOT_KEPT", row.Verification.NotKeptBytes, 0L),
                        row.ReplayNote);
                });
                (string text, MessageBoxImage image) = Assert.Single(messages);
                Assert.Contains(Words("REPLAY_DIFFS_NOT_KEPT", 2, replays.DeviceName, 0L), text);
                Assert.DoesNotContain(Words("REPLAY_VERIFY_DIFF_HINT", replays.DeviceName), text);
                Assert.Equal(MessageBoxImage.Warning, image);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // The row menu offers View verification differences on replay rows only, and it applies to rows of verified sends
        // only, also when their differences were not kept: the window then says so, with the verdict. It opens only a row
        // of the view's own tab, the very object that it names: a copy with the same number and record opens nothing.
        [Fact]
        public Task ViewVerifyDiff_AppliesOnlyToRowsOfVerifiedSends_OfTheViewsOwnTab() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var opened = new List<VerifyDiffWindow>();
            Window host = null;
            try
            {
                WebServiceDeviceCaptureController replays = ReplaysOf(model);
                ReplayRecorder recorder = Recorder(replays, budget: 3000);
                VerifyDiff kept = TextDiff("kept", 3000);
                VerifyDiff notKept = TextDiff("not kept", 2048);
                Add(recorder, kept);
                Add(recorder, notKept);
                recorder.Add(Sent(null), Answered());
                ProxyConnectionModel[] rows = replays.ProxyConnections.ToArray();
                ProxyConnectionModel captured = Capture(9, 80, "/captured", "body");

                (host, CaptureConnectionView view, DataGrid grid) = await ShowReplaysAsync(model);
                view.ShowVerifyDiff = opened.Add;
                Assert.Equal(
                    new[] { true, true, false, false, false },
                    new object[] { rows[0], rows[1], rows[2], captured, null }
                        .Select(row => WebServiceProxyCommands.ViewVerifyDiff.CanExecute(row, view)));
                ContextMenu menu = HostListUiTests.Open(HostListUiTests.Row(grid, rows[2]));
                Assert.Same(rows[2], Assert.Single(
                    menu.Items.OfType<MenuItem>(), entry => entry.Command == WebServiceProxyCommands.ViewVerifyDiff).CommandParameter);
                menu.IsOpen = false;

                var copy = new ProxyConnectionModel(rows[0]) { Verification = rows[0].Verification };
                WebServiceProxyCommands.ViewVerifyDiff.Execute(copy, view);
                WebServiceProxyCommands.ViewVerifyDiff.Execute(rows[2], view);
                Assert.Empty(opened);
                WebServiceProxyCommands.ViewVerifyDiff.Execute(rows[1], view);
                VerifyDiffWindow window = Assert.Single(opened);
                await window.Presented.WaitAsync(Patience);
                Assert.Same(rows[1].Verification, window.Shown);
                Assert.Equal(Words("VERIFY_DIFF_NOT_KEPT", 2048, 0L), Message(window));
                Assert.Equal(Words("VERIFY_DIFF_DIFFERED", Words("REPLAY_DIFFERENCE_BODY")), Shown(window, "SummaryText"));
                Assert.Equal(string.Empty, Shown(window, "BodyDiffText"));

                host.Content = HostListUiTests.CaptureView(new HostListPolicy(), captured);
                await SettleAsync();
                var capturedView = (CaptureConnectionView)host.Content;
                ContextMenu capturedMenu = HostListUiTests.Open(
                    HostListUiTests.Row((DataGrid)capturedView.FindName("ConnectionList"), captured));
                Assert.DoesNotContain(
                    capturedMenu.Items.OfType<MenuItem>(), entry => entry.Command == WebServiceProxyCommands.ViewVerifyDiff);
                capturedMenu.IsOpen = false;
            }
            finally
            {
                foreach (VerifyDiffWindow window in opened)
                {
                    window.Close();
                }

                host?.Close();
                await RemoveAllAsync(model);
            }
        });

        // The window shows its row's record until the row leaves its tab: the tab is cleared or closed, the row is removed
        // or replaced, or the tab's rows are replaced. Then, or once the window closes, it lets go of the record and clears
        // what it showed; other rows coming and going change nothing.
        [Theory]
        [InlineData("clear")]
        [InlineData("close tab")]
        [InlineData("remove")]
        [InlineData("replace")]
        [InlineData("reload")]
        [InlineData("close window")]
        public Task TheWindow_LetsGoOfTheRecord_OnceItsRowLeavesTheTab_OrItCloses(string change) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            VerifyDiffWindow window = null;
            bool closed = false;
            try
            {
                ICaptureDeviceContext context = model.GetOrAddReplayContext();
                var replays = (WebServiceDeviceCaptureController)context.CaptureController;
                ReplayRecorder recorder = Recorder(replays, VerifyDiffLimits.MaxRetainedBytes);
                Add(recorder, TextDiff("one"));
                Add(recorder, TextDiff("two"));
                ProxyConnectionModel other = replays.ProxyConnections[0];
                ProxyConnectionModel row = replays.ProxyConnections[1];
                window = new VerifyDiffWindow(row, replays);
                await ShowOffscreenAsync(window);
                await window.Presented.WaitAsync(Patience);
                Assert.Contains("+ two", Shown(window, "BodyDiffText"));

                replays.ProxyConnections.Remove(other);
                Add(recorder, TextDiff("three"));
                await SettleAsync();
                Assert.Same(row.Verification, window.Shown);

                switch (change)
                {
                    case "clear":
                        replays.ClearAllCaptures();
                        break;
                    case "close tab":
                        await model.RemoveDeviceContextAsync(context);
                        break;
                    case "remove":
                        replays.ProxyConnections.Remove(row);
                        break;
                    case "replace":
                        replays.ProxyConnections[replays.ProxyConnections.IndexOf(row)] =
                            ReplayRecorder.ToRow(9, ReplayProvenance.Composed, Answered());
                        break;
                    case "reload":
                        replays.ReplaceLoadedCapturesFrom(new WebServiceDeviceCaptureController(
                            "Loaded", DeviceType.Unknown, true, new ProxyPortPool()));
                        break;
                    default:
                        window.Close();
                        closed = true;
                        break;
                }

                await SettleAsync();
                Assert.Null(window.Shown);
                Assert.All(
                    new[] { "SummaryText", "DetailText", "BodyDiffText", "JsonDiffText" },
                    name => Assert.Equal(string.Empty, Shown(window, name)));
                Assert.Equal(closed ? null : Words("VERIFY_DIFF_STALE", row.RequestNumber, replays.DeviceName), Message(window));
            }
            finally
            {
                if (!closed)
                {
                    window?.Close();
                }

                await RemoveAllAsync(model);
            }
        });

        // Text prepared for a record that the window no longer shows, because its row left the tab meanwhile, is dropped.
        [Fact]
        public Task TheWindow_DropsTextPreparedForARecordThatItNoLongerShows() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            using var release = new ManualResetEventSlim();
            VerifyDiffWindow window = null;
            try
            {
                WebServiceDeviceCaptureController replays = ReplaysOf(model);
                Add(Recorder(replays, VerifyDiffLimits.MaxRetainedBytes), TextDiff("two"));
                ProxyConnectionModel row = Assert.Single(replays.ProxyConnections);
                window = new VerifyDiffWindow(row, replays, recorded =>
                {
                    release.Wait(Patience);
                    return VerifyDiffPresentation.Of(recorded);
                });
                Assert.Equal(Words("VERIFY_DIFF_PENDING"), Message(window));

                replays.ProxyConnections.Remove(row);
                Assert.Null(window.Shown);
                release.Set();
                await window.Presented.WaitAsync(Patience);
                await SettleAsync();

                Assert.Equal(string.Empty, Shown(window, "BodyDiffText"));
                Assert.Equal(string.Empty, Shown(window, "SummaryText"));
                Assert.Equal(Words("VERIFY_DIFF_STALE", row.RequestNumber, replays.DeviceName), Message(window));
            }
            finally
            {
                release.Set();
                window?.Close();
                await RemoveAllAsync(model);
            }
        });

        // A text body's differences: the verdict and both status codes; whether they are complete and each limitation, a
        // code that this version does not know named; how verify read each body; then the hunks, each line with the mark of
        // its break, so lines that differ only in their breaks show it, a cut line marked, and a character that would start
        // a new line in the text box shown as a mark.
        [Fact]
        public void TextDifferences_ShowEachLinesBreak_AndMarkCutLines()
        {
            string nl = Environment.NewLine;
            var differed = new ReplayVerification(new[] { ReplayVerifier.StatusDifference, ReplayVerifier.BodyDifference });
            VerifyDiff diff = Diff(
                differed,
                new VerifyBodyDiff(
                    false,
                    new VerifyTextDiff(VerifyEquivalence.Different, new[]
                    {
                        new VerifyTextHunk(2, 2, 2, 4, new[]
                        {
                            new VerifyTextLine(VerifyLineKind.Context, "same", "\r\n", false),
                            new VerifyTextLine(VerifyLineKind.Removed, "break", "\n", false),
                            new VerifyTextLine(VerifyLineKind.Added, "break", "\r\n", false),
                            new VerifyTextLine(VerifyLineKind.Added, "a long line", "\u2028", true),
                            new VerifyTextLine(VerifyLineKind.Added, "last\u2029line", string.Empty, false),
                        }),
                    }),
                    null,
                    null,
                    null),
                actualStatus: 404,
                state: VerifyDiffState.Limited,
                limitations: new[] { VerifyDiffLimitation.ValueCut, "verify.diff.future" });

            VerifyDiffPresentation shown = VerifyDiffPresentation.Of(new RecordedVerification(differed, diff));

            Assert.Equal(
                Words("VERIFY_DIFF_DIFFERED", Words("REPLAY_DIFFERENCE_STATUS") + ", " + Words("REPLAY_DIFFERENCE_BODY")) +
                    nl + Words("VERIFY_DIFF_STATUS", 200, 404),
                shown.Summary);
            Assert.Equal(
                string.Join(
                    nl,
                    Words("VERIFY_DIFF_STATE_LIMITED"),
                    "\u2022 " + Words("VERIFY_DIFF_LIMIT_VALUE_CUT"),
                    "\u2022 " + Words("VERIFY_DIFF_LIMIT_OTHER", "verify.diff.future"),
                    Words(
                        "VERIFY_DIFF_SIDE",
                        Words("VERIFY_DIFF_ORIGINAL"),
                        12L,
                        12L,
                        Words("VERIFY_DIFF_BASIS_DECODED"),
                        Words("VERIFY_DIFF_FRAMING_REMOVED"),
                        Words("VERIFY_DIFF_END_COMPLETE"),
                        Words("VERIFY_DIFF_TEXT_CHARSET", "utf-8")),
                    Words(
                        "VERIFY_DIFF_SIDE",
                        Words("VERIFY_DIFF_ACTUAL"),
                        20L,
                        20L,
                        Words("VERIFY_DIFF_BASIS_PLAIN"),
                        Words("VERIFY_DIFF_FRAMING_NONE"),
                        Words("VERIFY_DIFF_END_NOT_APPLICABLE"),
                        Words("VERIFY_DIFF_TEXT_CHARSET", "utf-8"))),
                shown.Detail);
            Assert.StartsWith(Words("VERIFY_DIFF_TEXT_DIFFERENT") + nl, shown.Body);
            Assert.Contains(Words("VERIFY_DIFF_BREAK_LEGEND", Words("VERIFY_DIFF_CUT")), shown.Body);
            Assert.EndsWith(
                string.Join(
                    nl,
                    string.Empty,
                    "@@ -2,2 +2,4 @@",
                    "  same\u240D\u240A",
                    "- break\u240A",
                    "+ break\u240D\u240A",
                    "+ a long line" + Words("VERIFY_DIFF_CUT") + "\u27E8LS\u27E9",
                    "+ last\u27E8PS\u27E9line"),
                shown.Body);
            Assert.Equal(Words("VERIFY_DIFF_JSON_NONE"), shown.Json);
            Assert.Null(shown.Message);
        }

        // A binary body's differences: both lengths, then each run with its offset, its length on each side and each side's
        // excerpt, followed by "..." when the run is longer than the excerpt; nothing for a side without bytes there.
        [Fact]
        public void ByteDifferences_ShowEachRunsExcerpts_AndMarkLongerRuns()
        {
            string nl = Environment.NewLine;
            string captured = "    " + Words("VERIFY_DIFF_CAPTURED") + " ";
            string replayed = "    " + Words("VERIFY_DIFF_REPLAYED") + " ";
            var differed = new ReplayVerification(new[] { ReplayVerifier.BodyDifference });
            VerifyDiff diff = Diff(differed, new VerifyBodyDiff(
                false,
                null,
                new VerifyByteDiff(40, 42, new[]
                {
                    new VerifyByteRange(4, 2, 2, "41 42", "43 44"),
                    new VerifyByteRange(
                        20, 17, 17, "00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F", "10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F"),
                    new VerifyByteRange(40, 0, 2, string.Empty, "0D 0A"),
                }),
                null,
                null));

            Assert.Equal(
                string.Join(
                    nl,
                    Words("VERIFY_DIFF_BYTES", 40L, 42L),
                    Words("VERIFY_DIFF_RANGE", 4L, 2L, 2L),
                    captured + "41 42",
                    replayed + "43 44",
                    Words("VERIFY_DIFF_RANGE", 20L, 17L, 17L),
                    captured + "00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F \u2026",
                    replayed + "10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F \u2026",
                    Words("VERIFY_DIFF_RANGE", 40L, 0L, 2L),
                    replayed + "0D 0A"),
                VerifyDiffPresentation.Of(new RecordedVerification(differed, diff)).Body);
        }

        // JSON differences: each change with its kind and path, then each side's value as written, a value's line breaks
        // continued on indented lines, cut paths and values marked; equal values with how their bytes differ; and why
        // there is no JSON comparison, a reason that this version does not know named.
        [Fact]
        public void JsonDifferences_ShowEachChangeAndEachSidesValue_OrWhyThereAreNone()
        {
            string nl = Environment.NewLine;
            string captured = "    " + Words("VERIFY_DIFF_CAPTURED") + " ";
            string replayed = "    " + Words("VERIFY_DIFF_REPLAYED") + " ";
            string continued = new(' ', replayed.Length);
            var differed = new ReplayVerification(new[] { ReplayVerifier.BodyDifference });
            VerifyTextDiff text = new(VerifyEquivalence.Same, Array.Empty<VerifyTextHunk>());
            VerifyDiffPresentation Present(VerifyBodyDiff body) =>
                VerifyDiffPresentation.Of(new RecordedVerification(differed, Diff(differed, body)));

            VerifyDiffPresentation changes = Present(new VerifyBodyDiff(false, text, null, new VerifyJsonDiff(
                VerifyEquivalence.Different,
                Array.Empty<string>(),
                new[]
                {
                    new VerifyJsonChange("$.a", false, VerifyJsonChangeKind.Changed, "1", false, "1.0", false),
                    new VerifyJsonChange("$.b", true, VerifyJsonChangeKind.Added, null, false, "{\n  \"x\": \"y\u2028z\"\n}", true),
                    new VerifyJsonChange("$[\"c d\"]", false, VerifyJsonChangeKind.Removed, "null", false, null, false),
                }), null));
            Assert.Equal(
                string.Join(
                    nl,
                    Words("VERIFY_DIFF_JSON_DIFFERENT"),
                    string.Empty,
                    Words("VERIFY_DIFF_JSON_CHANGE", Words("VERIFY_DIFF_JSON_CHANGED"), "$.a"),
                    captured + "1",
                    replayed + "1.0",
                    string.Empty,
                    Words("VERIFY_DIFF_JSON_CHANGE", Words("VERIFY_DIFF_JSON_ADDED"), "$.b" + Words("VERIFY_DIFF_CUT")),
                    replayed + "{",
                    continued + "  \"x\": \"y\u27E8LS\u27E9z\"",
                    continued + "}" + Words("VERIFY_DIFF_CUT"),
                    string.Empty,
                    Words("VERIFY_DIFF_JSON_CHANGE", Words("VERIFY_DIFF_JSON_REMOVED"), "$[\"c d\"]"),
                    captured + "null"),
                changes.Json);
            Assert.Equal(Words("VERIFY_DIFF_TEXT_SAME"), changes.Body);

            Assert.Equal(
                string.Join(
                    nl,
                    Words("VERIFY_DIFF_JSON_SAME"),
                    "\u2022 " + Words("VERIFY_DIFF_JSON_EXPLANATION_FORMATTING"),
                    "\u2022 " + Words("VERIFY_DIFF_JSON_EXPLANATION_MEMBER_ORDER"),
                    "\u2022 " + Words("VERIFY_DIFF_JSON_EXPLANATION_OTHER", "future")),
                Present(new VerifyBodyDiff(false, text, null, new VerifyJsonDiff(
                    VerifyEquivalence.Same,
                    new[] { VerifyJsonExplanation.Formatting, VerifyJsonExplanation.MemberOrder, "future" },
                    Array.Empty<VerifyJsonChange>()), null)).Json);
            Assert.Equal(
                Words("VERIFY_DIFF_JSON_UNAVAILABLE", Words("VERIFY_DIFF_JSON_UNAVAILABLE_DUPLICATE_KEYS")),
                Present(new VerifyBodyDiff(false, text, null, null, VerifyJsonUnavailable.DuplicateKeys)).Json);
            Assert.Equal(
                Words("VERIFY_DIFF_JSON_UNAVAILABLE", Words("VERIFY_DIFF_JSON_UNAVAILABLE_OTHER", "future")),
                Present(new VerifyBodyDiff(false, text, null, null, "future")).Json);
        }

        // Without body differences to show, the window says why: the bodies are equal, the differences are unavailable
        // (each limitation says why), the tab did not keep them, or none were recorded. The verdict is always shown.
        [Fact]
        public void WithoutBodyDifferences_TheWindowSaysWhy_AndShowsTheVerdict()
        {
            var statusOnly = new ReplayVerification(new[] { ReplayVerifier.StatusDifference });
            string verdict = Words("VERIFY_DIFF_DIFFERED", Words("REPLAY_DIFFERENCE_STATUS"));

            VerifyDiffPresentation equal = VerifyDiffPresentation.Of(
                new RecordedVerification(statusOnly, Diff(statusOnly, Equal(), actualStatus: 500)));
            Assert.Equal(verdict + Environment.NewLine + Words("VERIFY_DIFF_STATUS", 200, 500), equal.Summary);
            Assert.Equal((Words("VERIFY_DIFF_BODY_EQUAL"), Words("VERIFY_DIFF_BODY_EQUAL")), (equal.Body, equal.Json));

            VerifyDiffPresentation unavailable = VerifyDiffPresentation.Of(new RecordedVerification(
                statusOnly,
                Diff(
                    statusOnly,
                    null,
                    actualStatus: 500,
                    state: VerifyDiffState.Unavailable,
                    limitations: new[] { VerifyDiffLimitation.SerializedLimit })));
            Assert.StartsWith(
                Words("VERIFY_DIFF_STATE_UNAVAILABLE") + Environment.NewLine + "\u2022 " + Words("VERIFY_DIFF_LIMIT_SERIALIZED"),
                unavailable.Detail);
            Assert.Equal(
                (Words("VERIFY_DIFF_BODY_UNAVAILABLE"), Words("VERIFY_DIFF_BODY_UNAVAILABLE"), (string)null),
                (unavailable.Body, unavailable.Json, unavailable.Message));

            Assert.Equal(
                new VerifyDiffPresentation(
                    verdict, string.Empty, string.Empty, string.Empty, Words("VERIFY_DIFF_NOT_KEPT", 3000, 16L)),
                VerifyDiffPresentation.Of(new RecordedVerification(statusOnly, NotKeptBytes: 3000)));
            Assert.Equal(
                new VerifyDiffPresentation(
                    Words("VERIFY_DIFF_MATCHED"), string.Empty, string.Empty, string.Empty, Words("VERIFY_DIFF_NOT_RECORDED")),
                VerifyDiffPresentation.Of(new RecordedVerification(new ReplayVerification(Array.Empty<string>()))));
        }

        private static string Words(string key, params object[] arguments) => Localization.GetLocalizedString(key, arguments);

        private static string[] Codes(Type holder) =>
            holder.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue())
                .ToArray();

        // Each value has a key of its own, which en-us has.
        private static void AssertWorded<T>(IReadOnlyCollection<T> values, Func<T, string> keyOf)
        {
            Assert.NotEmpty(values);
            Assert.Empty(values.Where(value => keyOf(value) == null));
            string[] keys = values.Select(keyOf).ToArray();
            Assert.All(keys, key => Assert.DoesNotContain("NO_IMPL_", Localization.GetLocalizedString(key, AnyArguments)));
            Assert.Equal(keys.Length, keys.Distinct().Count());
        }

        // Differences as VerifyDiffOperation records them, of the given serialized size.
        private static VerifyDiff Diff(
            ReplayVerification verdict,
            VerifyBodyDiff body,
            int serializedBytes = 2048,
            int actualStatus = 200,
            VerifyDiffState state = VerifyDiffState.Complete,
            string[] limitations = null) =>
            new(state, limitations ?? Array.Empty<string>(), verdict, 200, actualStatus, CapturedSide, ReplayedSide, body, serializedBytes);

        private static VerifyBodyDiff Equal() => new(true, null, null, null, null);

        // A body that differs in its one line of text.
        private static VerifyDiff TextDiff(string replayed, int serializedBytes = 2048) => Diff(
            new ReplayVerification(new[] { ReplayVerifier.BodyDifference }),
            new VerifyBodyDiff(
                false,
                new VerifyTextDiff(VerifyEquivalence.Different, new[]
                {
                    new VerifyTextHunk(1, 1, 1, 1, new[]
                    {
                        new VerifyTextLine(VerifyLineKind.Removed, "captured", string.Empty, false),
                        new VerifyTextLine(VerifyLineKind.Added, replayed, string.Empty, false),
                    }),
                }),
                null,
                null,
                null),
            serializedBytes);

        // A recorder for the tab's rows on the calling thread, as from the Replay menu of the "Source tab" tab.
        private static ReplayRecorder Recorder(WebServiceDeviceCaptureController controller, long budget) =>
            new(
                controller,
                null,
                action => action(),
                _ => new ReplayProvenance(ReplaySourceKind.Reissued, "Source tab", 7),
                verifyDiffBudgetBytes: budget);

        // A verified send as ReplayOperation hands it to its sink: its verdict in the result, its differences with the
        // exchange.
        private static void Add(ReplayRecorder recorder, VerifyDiff diff)
        {
            ReplayExchange exchange = Answered();
            exchange.SetVerifyDiff(diff);
            recorder.Add(Sent(diff.Verification), exchange);
        }

        private static ReplayItemResult Sent(ReplayVerification verification) =>
            new(1, null, 7, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, verification);

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

        private static ProxyConnectionModel Capture(long number, int port, string path, string responseBody) =>
            new(number, "200", DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{port}", "GET", path,
                $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n", [], "HTTP/1.1 200 OK\r\n",
                Encoding.UTF8.GetBytes(responseBody), "127.0.0.1");

        // What the views pass: rows are added on the UI thread, synchronously.
        private static ReplayRecordingTarget Target(
            CaptureAppModel model, long verifyDiffBudgetBytes = VerifyDiffLimits.MaxRetainedBytes)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(
                model, action => dispatcher.Invoke(action), _ => null, verifyDiffBudgetBytes: verifyDiffBudgetBytes);
        }

        private static WebServiceDeviceCaptureController ReplaysOf(CaptureAppModel model) =>
            (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;

        // The model's Replays tab in a view of its own, shown off screen at its designed size.
        private static async Task<(Window Host, CaptureConnectionView View, DataGrid Grid)> ShowReplaysAsync(CaptureAppModel model)
        {
            var view = new CaptureConnectionView { AutoScroll = false, DataContext = model.GetOrAddReplayContext() };
            var grid = (DataGrid)view.FindName("ConnectionList");
            grid.IsSynchronizedWithCurrentItem = false;
            var host = new Window
            {
                Width = 1280,
                Height = 720,
                Left = -12000,
                Top = -12000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = view,
            };
            host.Show();
            await SettleAsync();
            return (host, view, grid);
        }

        private static async Task ShowOffscreenAsync(Window window)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -12000;
            window.Top = -12000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            await SettleAsync();
        }

        private static async Task SettleAsync() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        // The text that a named text block or text box of the window shows.
        private static string Shown(Window window, string name) => window.FindName(name) switch
        {
            TextBox box => box.Text,
            TextBlock block => block.Text,
            _ => throw new ArgumentException("No text element has that name.", nameof(name)),
        };

        // The window's message, or null while it is hidden.
        private static string Message(Window window)
        {
            var bar = (TextBlock)window.FindName("MessageText");
            return bar.Visibility == Visibility.Visible ? bar.Text : null;
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
