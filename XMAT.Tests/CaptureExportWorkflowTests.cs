// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    // File > Export and File > Export Sanitized Capture... through CaptureExportWorkflow (S2, design 4 and 7.2, G1 to G19),
    // with scripted dialogs (ScriptedCaptureExportDialogs), real SQLite sources and the real SazExportOperation, unless a
    // test substitutes its run (G16). Destinations are in this test's own folder on a local NTFS drive; the copy kind is
    // reached through SazExportTestHooks. Each test uses its own application model. G19's raw sends use L2's Raw fixtures,
    // which open no socket.
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class CaptureExportWorkflowTests : IDisposable
    {
        private const string Device = "selected";
        private const string Other = "other";
        private const string Canary = "canary-5b0e9d7a";

        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        // The keys of the texts that say how an export ended: a publication, a refusal or a residue (4.4).
        private static readonly string[] OutcomeKeys =
        {
            "EXPORT_CREATED", "EXPORT_CREATED_COPY", "EXPORT_REPLACED", "EXPORT_REPLACED_COPY", "EXPORT_CANCELLED",
            "EXPORT_FAILED", "EXPORT_SAZ_REQUIRED", "EXPORT_UNSUPPORTED", "EXPORT_NO_PRIVATE_STAGING",
            "EXPORT_UNMARKED_SOURCE", "EXPORT_SOURCE_UNAVAILABLE", "EXPORT_TOO_MANY_ROWS", "EXPORT_TOO_LARGE",
            "EXPORT_INVALID_START_LINE", "EXPORT_EXISTS", "EXPORT_CHANGED", "EXPORT_LOCATION_UNVERIFIED", "EXPORT_NOT_A_FILE",
            "EXPORT_READ_ONLY", "EXPORT_IN_USE", "EXPORT_ACCESS_DENIED", "EXPORT_UNCERTAIN", "EXPORT_UNCERTAIN_COPY_CREATED",
            "EXPORT_UNCERTAIN_COPY_REPLACED", "EXPORT_UNCERTAIN_COPY_UNVERIFIED", "EXPORT_RESIDUE_STAGING",
            "EXPORT_RESIDUE_EARLIER", "EXPORT_RESIDUE_RESTORE", "SANITIZED_EXPORT_INVALID_HTTP", "EXPORT_REPLACE_QUESTION",
            "EXPORT_COPY_REPLACE_QUESTION", "EXPORT_COPY_NOTICE",
        };

        private static readonly string[] NoteKeys =
        {
            "EXPORT_NOTE_SKIPPED", "EXPORT_NOTE_OVERSIZED", "EXPORT_NOTE_UNREADABLE", "EXPORT_NOTE_BODIES",
            "EXPORT_NOTE_UNDECODABLE", "EXPORT_NOTE_RENUMBERED", "EXPORT_NOTE_REPLACED", "EXPORT_NOTE_DROPPED",
            "EXPORT_NOTE_REFORMATTED", "EXPORT_NOTE_CLIENT", "EXPORT_NOTE_TIMES",
        };

        private readonly CaptureUiApplicationFixture _application;
        private readonly string _directory;

        public CaptureExportWorkflowTests(CaptureUiApplicationFixture application)
        {
            _application = application;
            _directory = Path.Join(AppContext.BaseDirectory, "CaptureExportWorkflow", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose() => TestFolderCleanup.Delete(_directory);

        // G1: Export is enabled for a live, imported, loaded and Replays web proxy tab, also while a capture runs, and
        // disabled for a network tab, for none, and while an analysis or a capture-data operation runs. The sanitized export
        // keeps its own conditions.
        [Fact]
        public Task G1_ExportIsEnabledForEveryWebProxyTab_AlsoWhileACaptureRuns() => _application.RunAsync(async () =>
        {
            using SqLtDatabase database = CaptureDatabase(out IDataTable table, out _);
            string saved = SavedCapture("loaded.xmatcap", rows => AddRow(rows, Device, 1), false, Device);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var live = new WebServiceDeviceCaptureController("live", DeviceType.GenericProxyDevice, false);
            var liveTab = new Context(live) { IsReadOnly = false };
            try
            {
                Assert.False(model.CanExportCaptures);
                model.CaptureDeviceContexts.Add(liveTab);
                model.SelectedDeviceContext = liveTab;
                Assert.True(model.CanExportCaptures);
                live.IsRunning = true;
                Assert.True(model.CanExportCaptures);

                ActiveTab(model, Device, table);
                Assert.True(model.CanExportCaptures);
                Assert.False(model.CanExportSanitizedCaptures);
                model.SelectedDeviceContext = model.GetOrAddReplayContext();
                Assert.True(model.CanExportCaptures);
                LoadTab(model, saved, Device);
                Assert.True(model.CanExportCaptures);
                Assert.False(model.CanExportSanitizedCaptures);
                live.IsRunning = false;
                Assert.True(model.CanExportSanitizedCaptures);

                ICaptureDeviceContext loaded = model.SelectedDeviceContext;
                model.SelectedDeviceContext = new Context(
                    new WebServiceDeviceCaptureController("trace", DeviceType.GenericProxyDevice, true),
                    CaptureType.NetworkTrace);
                Assert.False(model.CanExportCaptures);
                model.SelectedDeviceContext = null;
                Assert.False(model.CanExportCaptures);

                model.SelectedDeviceContext = loaded;
                Assert.True(model.TryBeginAnalysis(out IDisposable analysis));
                using (analysis)
                {
                    Assert.False(model.CanExportCaptures);
                }

                Assert.True(model.TryBeginCaptureDataOperation(out IDisposable operation));
                using (operation)
                {
                    Assert.False(model.CanExportCaptures);
                }

                Assert.True(model.CanExportCaptures);
            }
            finally
            {
                live.IsRunning = false;
                await CloseAsync(model);
            }
        });

        // G1, G14: File > Export's binding asks the model's own condition, and its destination dialog has the Fiddler
        // filter, saz as its default extension (WPF reads it back without its dot), and OverwritePrompt off.
        [Fact]
        public void G1_G14_TheMenuAsksCanExportCaptures_AndTheDestinationDialogDoesNotAskToOverwrite()
        {
            using Stream stream = typeof(CaptureExportWorkflowTests).Assembly.GetManifestResourceStream("XMAT.Tests.MainWindow.xaml");
            XDocument document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XElement binding = document.Descendants(presentation + "CommandBinding")
                .Single(element => (string)element.Attribute("Command") == "{x:Static local:AppCommands.Export}");
            Assert.Equal(("Export_Executed", "Export_CanExecute"),
                ((string)binding.Attribute("Executed"), (string)binding.Attribute("CanExecute")));

            _application.Run(window =>
            {
                foreach (SazExportKind kind in new[] { SazExportKind.Plain, SazExportKind.Sanitized })
                {
                    Microsoft.Win32.SaveFileDialog dialog = MainWindow.CreateCaptureExportDialog(kind);
                    Assert.False(dialog.OverwritePrompt);
                    Assert.Equal(("saz", true), (dialog.DefaultExt, dialog.AddExtension));
                    Assert.EndsWith("|*.saz", dialog.Filter, StringComparison.Ordinal);
                }
            });
        }

        // G2: of two tabs in the shared table whose numbers overlap, the export holds only the selected tab's rows, with
        // their numbers; the grid's status filter and selection change nothing (U2).
        [Fact]
        public Task G2_ExportsTheSelectedTabsRowsOnly_WithTheirNumbers_WhateverItsGridShows() => _application.RunAsync(async () =>
        {
            using SqLtDatabase database = CaptureDatabase(out IDataTable table, out _);
            AddRow(table, Device, 1);
            AddRow(table, Other, 1);
            AddRow(table, Device, 2, status: "404");
            AddRow(table, Other, 2);
            AddRow(table, Other, 3);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                ActiveTab(model, Other, table, select: false);
                WebServiceDeviceCaptureController selected = ActiveTab(model, Device, table);
                selected.StatusFilterList = selected.UpdateFilter(new[] { "200", "404" });
                selected.StatusFilterList.Single(item => item.Text == "404").IsChecked = false;
                selected.SelectedConnectionModel = selected.ProxyConnections[0];
                string destination = Destination("selected.saz");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs);

                Assert.Equal(SazExportPublication.Created, outcome.Result.Publication);
                Assert.Equal(new[] { "1:selected-1", "2:selected-2" }, Markers(destination));
                AssertNoStaging();
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G3: a loaded .xmatcap tab exports its own rows, not another loaded tab's.
        [Fact]
        public Task G3_ALoadedTab_ExportsItsOwnRows() => _application.RunAsync(async () =>
        {
            string saved = SavedCapture(
                "two-devices.xmatcap",
                rows =>
                {
                    AddRow(rows, Other, 1);
                    AddRow(rows, Device, 7);
                    AddRow(rows, Other, 2);
                    AddRow(rows, Device, 8);
                },
                false,
                Other,
                Device);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, saved, Device);
                string destination = Destination("loaded.saz");

                CaptureExportOutcome outcome = await ExportAsync(
                    model, SazExportKind.Plain, Dialogs(new CaptureExportScript { Destination = destination }));

                Assert.Equal((SazExportPublication.Created, 2), (outcome.Result.Publication, outcome.Result.Summary.Sessions));
                Assert.Equal(new[] { "7:selected-7", "8:selected-8" }, Markers(destination));
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G4: a row that the tab gains after the export began is not in the archive; every call to the frozen source runs on
        // a worker, never on the UI thread.
        [Fact]
        public Task G4_ARowAddedAfterTheExportBegan_IsNotExported_AndTheSourceIsReadOffTheUiThread() => _application.RunAsync(async () =>
        {
            int ui = Environment.CurrentManagedThreadId;
            using SqLtDatabase database = CaptureDatabase(out IDataTable table, out _);
            for (int number = 1; number <= 3; number++)
            {
                AddRow(table, Device, number);
            }

            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                ActiveTab(model, Device, table);
                string destination = Destination("live.saz");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination });
                dialogs.BeforeDestination = () => AddRow(table, Device, 4);
                RecordingSource source = null;

                CaptureExportOutcome outcome = await ExportAsync(
                    model,
                    SazExportKind.Plain,
                    dialogs,
                    configure: workflow => workflow.SourceFrozen = frozen => source = new RecordingSource(frozen));

                Assert.Equal(SazExportPublication.Created, outcome.Result.Publication);
                Assert.Equal(new[] { "1:selected-1", "2:selected-2", "3:selected-3" }, Markers(destination));
                Assert.NotEmpty(source.Calls);
                Assert.Contains(source.Calls, call => call.Call == "row");
                Assert.All(source.Calls, call => Assert.NotEqual(ui, call.Thread));
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G5: a failure, whether a result, an exception that carries a canary, or a source that no export reads, gives
        // EXPORT_FAILED; XMAT goes on, the lease ends, and the log line holds only a code or an exception's type.
        [Theory]
        [InlineData("result")]
        [InlineData("exception")]
        [InlineData("source")]
        public Task G5_AFailure_ShowsExportFailed_AndLogsOnlyACodeOrAType(string failure) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            try
            {
                if (failure == "source")
                {
                    var controller = new WebServiceDeviceCaptureController(Device, DeviceType.GenericProxyDevice, true);
                    controller.LoadCaptureDataset(new FakeDataset(CaptureTextSource.Literal, new FakeRecord(1, Device)));
                    SelectTab(model, controller);
                }
                else
                {
                    LoadTab(model, SavedCapture("failing.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                }

                string destination = Destination("failed.saz");
                var run = new FakeRun(_ => failure == "exception"
                    ? throw new IOException(Canary)
                    : Result(SazExportPublication.NotPublished, SazExportCodes.WriteFailed));
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs, runner: run, logged: logged);

                Assert.Equal("EXPORT_FAILED", outcome.Shown[^1]);
                string expected = failure switch
                {
                    "result" => SazExportCodes.WriteFailed,
                    "exception" => typeof(IOException).FullName,
                    _ => SazExportCodes.SourceUnsupported,
                };
                Assert.Equal(CaptureExportWorkflow.LogPrefix + expected, Assert.Single(logged));
                Assert.DoesNotContain(Texts(dialogs), text => text.Contains(Canary, StringComparison.Ordinal));
                Assert.False(model.IsCaptureDataOperationRunning);
                Assert.False(File.Exists(destination));
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G6: Cancel in the progress window of each step, binding, preparing, admission and writing, gives EXPORT_CANCELLED,
        // publishes nothing and leaves no staging file, in either export.
        [Theory]
        [InlineData("Binding", false)]
        [InlineData("Preparing", false)]
        [InlineData("Admitting", false)]
        [InlineData("Writing", false)]
        [InlineData("Binding", true)]
        [InlineData("Preparing", true)]
        [InlineData("Admitting", true)]
        [InlineData("Writing", true)]
        public Task G6_CancelInEachStep_PublishesNothing(string cancelled, bool sanitized) => _application.RunAsync(async () =>
        {
            CaptureExportStep step = Enum.Parse<CaptureExportStep>(cancelled);
            SazExportKind kind = Kind(sanitized);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("unmarked.xmatcap", rows => AddRow(rows, Device, 1), true, Device), Device);
                string destination = Destination("cancelled.saz");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, AcceptAsStored = true });
                dialogs.CancelBefore = step;

                CaptureExportOutcome outcome = await ExportAsync(model, kind, dialogs);

                Assert.Equal(SazExportCodes.Cancelled, outcome.Code);
                Assert.Equal("EXPORT_CANCELLED", outcome.Shown[^1]);
                Assert.Equal(step, dialogs.Steps[^1].Step);
                Assert.False(File.Exists(destination));
                Assert.False(model.IsCaptureDataOperationRunning);
                AssertNoStaging();
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G6: Cancel while rows are being written: the export stops, publishes nothing, leaves an existing destination as it
        // was and no staging file, and the count line counted rows (S2-V13).
        [Fact]
        public Task G6_CancelWhileWriting_PublishesNothing() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            using var gate = new ManualResetEventSlim();
            try
            {
                LoadTab(model, SavedCapture("writing.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string destination = Destination("existing.saz");
                File.WriteAllText(destination, "earlier file");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, Replace = true });
                RecordingSource source = null;
                dialogs.DuringStep = async progress =>
                {
                    if (progress.Step == CaptureExportStep.Writing)
                    {
                        await source.RowReached.Task.WaitAsync(Patience);
                        Assert.True(progress.RequestCancellation());
                        gate.Set();
                    }
                };

                CaptureExportOutcome outcome = await ExportAsync(
                    model,
                    SazExportKind.Plain,
                    dialogs,
                    configure: workflow => workflow.SourceFrozen = frozen => source = new RecordingSource(frozen) { RowGate = gate });

                Assert.Equal((SazExportPublication.NotPublished, SazExportCodes.Cancelled),
                    (outcome.Result.Publication, outcome.Code));
                Assert.Equal("EXPORT_CANCELLED", outcome.Shown[^1]);
                Assert.Equal("earlier file", File.ReadAllText(destination));
                AssertNoStaging();
            }
            finally
            {
                gate.Set();
                await CloseAsync(model);
            }
        });

        // G6: closing XMAT while an export writes cancels it and waits for it to end; the export then shows nothing.
        [Fact]
        public Task G6_ClosingXmatWhileWriting_CancelsTheExportAndWaitsForIt() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            using var gate = new ManualResetEventSlim();
            try
            {
                LoadTab(model, SavedCapture("closing.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string destination = Destination("closing.saz");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination });
                RecordingSource source = null;
                Task<bool> closing = null;
                dialogs.DuringStep = async progress =>
                {
                    if (progress.Step == CaptureExportStep.Writing)
                    {
                        await source.RowReached.Task.WaitAsync(Patience);
                        closing = model.CanCloseAsync();
                        Assert.False(closing.IsCompleted);
                        gate.Set();
                    }
                };

                CaptureExportOutcome outcome = await ExportAsync(
                    model,
                    SazExportKind.Sanitized,
                    dialogs,
                    configure: workflow => workflow.SourceFrozen = frozen => source = new RecordingSource(frozen) { RowGate = gate });

                Assert.True(await closing.WaitAsync(Patience));
                Assert.Equal(SazExportCodes.Cancelled, outcome.Code);
                Assert.DoesNotContain("EXPORT_CANCELLED", outcome.Shown);
                Assert.Empty(dialogs.Messages);
                Assert.False(File.Exists(destination));
                Assert.False(model.IsCaptureDataOperationRunning);
            }
            finally
            {
                gate.Set();
                await CloseAsync(model);
            }
        });

        // G6, S2-V13: the count line counts every row processed, the rows left out included, of the rows planned.
        [Fact]
        public Task G6_TheCountLine_CountsRowsLeftOut() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(
                    model,
                    SavedCapture(
                        "left-out.xmatcap",
                        rows =>
                        {
                            AddRow(rows, Device, 1);
                            AddRow(rows, Device, 2, requestHead: string.Empty);
                            AddRow(rows, Device, 3);
                        },
                        false,
                        Device),
                    Device);
                var dialogs = Dialogs(new CaptureExportScript { Destination = Destination("counted.saz") });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs);

                CaptureExportProgress writing = dialogs.Steps.Single(step => step.Step == CaptureExportStep.Writing);
                Assert.True(writing.HasCount);
                Assert.Equal((3, 3), (writing.Processed, writing.Total));
                Assert.Equal((2, 1), (outcome.Result.Summary.Sessions, outcome.Result.Summary.RowsWithoutRequest));
                Assert.Contains("EXPORT_NOTE_SKIPPED", outcome.Shown);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G7: a destination without .saz, a folder that does not exist, and a work folder that admission does not admit
        // are each refused with their text, before any row is read, and nothing is written.
        [Theory]
        [InlineData("saz-required")]
        [InlineData("unsupported")]
        [InlineData("no-private-staging")]
        public Task G7_DestinationRefusals_ComeBeforeAnyRowIsRead(string refusal) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            try
            {
                LoadTab(model, SavedCapture("refused.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string destination = refusal switch
                {
                    "saz-required" => Destination("native.xmatcap"),
                    "unsupported" => Path.Join(_directory, "missing", "export.saz"),
                    _ => Destination("copy.saz"),
                };
                if (refusal == "saz-required")
                {
                    File.WriteAllText(destination, "native capture");
                }

                SazExportOperation operation = refusal == "no-private-staging"
                    ? new SazExportOperation(new SazExportTestHooks(AdmitAsKind: CaptureArchiveKind.Copy, RefuseWorkFolder: true))
                    : null;
                RecordingSource source = null;
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination });

                CaptureExportOutcome outcome = await ExportAsync(
                    model,
                    SazExportKind.Plain,
                    dialogs,
                    operation,
                    logged: logged,
                    configure: workflow => workflow.SourceFrozen = frozen => source = new RecordingSource(frozen));

                (string code, string key) = refusal switch
                {
                    "saz-required" => (SazExportCodes.SazRequired, "EXPORT_SAZ_REQUIRED"),
                    "unsupported" => (SazExportCodes.UnsupportedLocation, "EXPORT_UNSUPPORTED"),
                    _ => (SazExportCodes.NoPrivateStaging, "EXPORT_NO_PRIVATE_STAGING"),
                };
                Assert.Equal((code, key), (outcome.Code, outcome.Shown[^1]));
                Assert.Equal(new[] { CaptureExportStep.Preparing, CaptureExportStep.Admitting }, dialogs.Steps.Select(step => step.Step));
                Assert.Equal(0, source.Count("row"));
                Assert.Equal(CaptureExportWorkflow.LogPrefix + code, Assert.Single(logged));
                if (refusal == "saz-required")
                {
                    Assert.Equal("native capture", File.ReadAllText(destination));
                }
                else
                {
                    Assert.False(File.Exists(destination));
                }

                AssertNoStaging();
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G8: the publication's text with the count and the name, created or replaced, private or copied; then
        // EXPORT_NOT_SANITIZED after a plain export only.
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        public Task G8_G9_ThePublicationsText_WithItsCountAndName(bool replace, bool copy, bool sanitizedExport) =>
            _application.RunAsync(async () =>
        {
            SazExportKind kind = Kind(sanitizedExport);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("published.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string destination = Destination("published.saz");
                if (replace)
                {
                    File.WriteAllText(destination, "earlier file");
                }

                var sanitized = new SanitizedExportSummary { Sessions = 5, RedactedValues = 6, OmittedBodies = 7, OmittedHeaders = 8 };
                var run = new FakeRun(_ => Result(
                    replace ? SazExportPublication.Replaced : SazExportPublication.Created,
                    summary: Summary(sessions: 5, sanitized: kind == SazExportKind.Sanitized ? sanitized : null)));
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, Replace = true });

                CaptureExportOutcome outcome = await ExportAsync(
                    model, kind, dialogs, Hooks(copy ? CaptureArchiveKind.Copy : null), run);

                string published = (replace, copy) switch
                {
                    (false, false) => "EXPORT_CREATED",
                    (false, true) => "EXPORT_CREATED_COPY",
                    (true, false) => "EXPORT_REPLACED",
                    _ => "EXPORT_REPLACED_COPY",
                };
                var expected = new List<string>();
                if (kind == SazExportKind.Sanitized)
                {
                    expected.Add("SANITIZED_EXPORT_CONFIRM");
                }

                expected.Add(kind == SazExportKind.Sanitized ? "FILE_EXPORT_SANITIZED" : "FILE_EXPORT_CAPTURES");
                if (replace)
                {
                    expected.Add(copy ? "EXPORT_COPY_REPLACE_QUESTION" : "EXPORT_REPLACE_QUESTION");
                }

                if (copy)
                {
                    expected.Add("EXPORT_COPY_NOTICE");
                }

                expected.Add(published);
                expected.Add(kind == SazExportKind.Sanitized ? "SANITIZED_EXPORT_COMPLETE" : "EXPORT_NOT_SANITIZED");
                Assert.Equal(expected, outcome.Shown);
                Assert.Null(outcome.Code);
                CaptureExportText text = dialogs.Messages.Single().Parts[0];
                Assert.Equal(new object[] { 5, "published.saz" }, text.Args);
                if (kind == SazExportKind.Sanitized)
                {
                    string summary = dialogs.Messages.Single().Parts[1].Localize();
                    Assert.Equal(Words("SANITIZED_EXPORT_COMPLETE", 5, 6, 7, 8), summary);
                    Assert.StartsWith("Sessions exported: 5", summary, StringComparison.Ordinal);
                    Assert.DoesNotContain("Sanitized SAZ created.", summary, StringComparison.Ordinal);
                }
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G8: a note for each count above zero and none at zero, in 4.4's order, and the analyze-input note only above
        // 2,147,483,648 bytes (Q-S2-2).
        [Theory]
        [InlineData(0, 2_147_483_648L)]
        [InlineData(1, 2_147_483_648L)]
        [InlineData(0, 2_147_483_649L)]
        public Task G8_ANoteForEachCountAboveZero_AndTheAnalyzeInputNoteAboveItsLimit(int notes, long archiveBytes) =>
            _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("notes.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                var run = new FakeRun(_ => Result(
                    SazExportPublication.Created, summary: Summary(notes: notes, archiveBytes: archiveBytes)));
                var dialogs = Dialogs(new CaptureExportScript { Destination = Destination("notes.saz") });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs, runner: run);

                var expected = new List<string> { "FILE_EXPORT_CAPTURES", "EXPORT_CREATED", "EXPORT_NOT_SANITIZED" };
                if (notes > 0)
                {
                    expected.AddRange(NoteKeys);
                }

                if (archiveBytes > SazExportOperation.AnalyzeInputCeilingBytes)
                {
                    expected.Add("EXPORT_NOTE_ANALYZE_INPUT");
                }

                Assert.Equal(expected, outcome.Shown);
                Assert.All(
                    dialogs.Messages.Single().Parts.Where(part => NoteKeys.Contains(part.Key)),
                    part => Assert.Equal(new object[] { (long)notes }, part.Args));
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G9: the sanitized export, run for real, shows its four counts as the core's summary has them, after the
        // publication's text (IF-7); its refusals and failures use 4.4's texts, and en-us no longer has its own failure and
        // cancellation texts, or the keys that only the old export used.
        [Fact]
        public Task G9_TheSanitizedExport_ShowsTheCoresSummary() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(
                    model,
                    SavedCapture(
                        "secrets.xmatcap",
                        rows => AddRow(
                            rows,
                            Device,
                            1,
                            requestHead: "GET /items/1?access_token=" + Canary + " HTTP/1.1\r\nHost: example.test\r\n" +
                                "Authorization: Bearer " + Canary + "\r\n\r\n"),
                        false,
                        Device),
                    Device);
                string destination = Destination("sanitized.saz");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Sanitized, dialogs);

                Assert.Equal(
                    new[] { "SANITIZED_EXPORT_CONFIRM", "FILE_EXPORT_SANITIZED", "EXPORT_CREATED", "SANITIZED_EXPORT_COMPLETE" },
                    outcome.Shown.Take(4));
                SanitizedExportSummary summary = outcome.Result.Summary.Sanitized;
                Assert.True(summary.RedactedValues > 0);
                CaptureExportText counts = dialogs.Messages.Single().Parts[1];
                Assert.Equal(
                    new object[] { summary.Sessions, summary.RedactedValues, summary.OmittedBodies, summary.OmittedHeaders },
                    counts.Args);
                Assert.DoesNotContain("EXPORT_NOT_SANITIZED", outcome.Shown);
                using (ZipArchive archive = ZipFile.OpenRead(destination))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        using var reader = new StreamReader(entry.Open());
                        Assert.DoesNotContain(Canary, reader.ReadToEnd(), StringComparison.Ordinal);
                    }
                }

                foreach (string key in new[]
                {
                    "SANITIZED_EXPORT_FAILED", "SANITIZED_EXPORT_CANCELLED", "SANITIZED_EXPORT_UNAVAILABLE",
                    "SANITIZED_EXPORT_SAZ_REQUIRED", "SANITIZED_EXPORT_PROGRESS_DETAILS", "CAPTURE_TEXT_EXPORT_POLICY_REQUIRED",
                })
                {
                    Assert.Equal("NO_IMPL_" + key, Words(key));
                }
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G9, IF-7: the sanitized summary follows only a created or replaced archive, never one that was not published or
        // whose publication is uncertain.
        [Theory]
        [InlineData("NotPublished")]
        [InlineData("Uncertain")]
        public Task G9_TheSanitizedSummary_FollowsOnlyAPublishedArchive(string ended) =>
            _application.RunAsync(async () =>
        {
            SazExportPublication publication = Enum.Parse<SazExportPublication>(ended);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("unpublished.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                var run = new FakeRun(_ => Result(
                    publication,
                    publication == SazExportPublication.Uncertain ? SazExportCodes.Uncertain : SazExportCodes.WriteFailed,
                    publication == SazExportPublication.Uncertain ? SazExportUncertainty.Rename : SazExportUncertainty.None,
                    Summary(sanitized: new SanitizedExportSummary { Sessions = 1 })));
                var dialogs = Dialogs(new CaptureExportScript { Destination = Destination("unpublished.saz") });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Sanitized, dialogs, runner: run);

                Assert.Equal(
                    publication == SazExportPublication.Uncertain ? "EXPORT_UNCERTAIN" : "EXPORT_FAILED",
                    outcome.Shown[^1]);
                Assert.DoesNotContain("SANITIZED_EXPORT_COMPLETE", outcome.Shown);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G11: each result that the run can give has its text, with the user's own name and folder where it has them.
        [Theory]
        [InlineData(SazExportCodes.Exists, "EXPORT_EXISTS")]
        [InlineData(SazExportCodes.Changed, "EXPORT_CHANGED")]
        [InlineData(SazExportCodes.InUse, "EXPORT_IN_USE")]
        [InlineData(SazExportCodes.AccessDenied, "EXPORT_ACCESS_DENIED")]
        [InlineData(SazExportCodes.LocationUnverified, "EXPORT_LOCATION_UNVERIFIED")]
        [InlineData(SazExportCodes.InvalidStartLine, "EXPORT_INVALID_START_LINE")]
        [InlineData(SazExportCodes.SanitizerInvalidHttp, "SANITIZED_EXPORT_INVALID_HTTP")]
        [InlineData(SazExportCodes.TooLarge, "EXPORT_TOO_LARGE")]
        [InlineData(SazExportCodes.WriteFailed, "EXPORT_FAILED")]
        [InlineData(SazExportCodes.PlanUsed, "EXPORT_FAILED")]
        [InlineData(SazExportCodes.HeadUnencodable, "EXPORT_FAILED")]
        [InlineData(SazExportCodes.Cancelled, "EXPORT_CANCELLED")]
        public Task G11_EachRunRefusal_HasItsText(string code, string key) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            try
            {
                LoadTab(model, SavedCapture("refusal.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                var run = new FakeRun(_ => Result(SazExportPublication.NotPublished, code));
                var dialogs = Dialogs(new CaptureExportScript { Destination = Destination("refusal.saz") });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs, runner: run, logged: logged);

                Assert.Equal((code, key), (outcome.Code, outcome.Shown[^1]));
                Assert.Equal(new[] { "FILE_EXPORT_CAPTURES", key }, outcome.Shown);
                CaptureExportText text = dialogs.Messages.Single().Parts.Single();
                object[] expected = key switch
                {
                    "EXPORT_EXISTS" or "EXPORT_CHANGED" or "EXPORT_IN_USE" or "EXPORT_ACCESS_DENIED" =>
                        new object[] { "refusal.saz" },
                    "EXPORT_LOCATION_UNVERIFIED" => new object[] { "refusal.saz", _directory },
                    "EXPORT_TOO_LARGE" => new object[] { SazExportBudgets.Default.MaxTotalStoredBytes / 1_048_576 },
                    _ => Array.Empty<object>(),
                };
                Assert.Equal(expected, text.Args);
                Assert.Equal(
                    code == SazExportCodes.Cancelled ? Array.Empty<string>() : new[] { CaptureExportWorkflow.LogPrefix + code },
                    logged);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G11: an uncertain publication has its form's text, the copy kind's three by the core's discriminator.
        [Theory]
        [InlineData("Rename", false, "EXPORT_UNCERTAIN")]
        [InlineData("CopyCreated", true, "EXPORT_UNCERTAIN_COPY_CREATED")]
        [InlineData("CopyReplaced", true, "EXPORT_UNCERTAIN_COPY_REPLACED")]
        [InlineData("CopyUnverified", true, "EXPORT_UNCERTAIN_COPY_UNVERIFIED")]
        public Task G11_G15_EachUncertainPublication_HasItsText(string form, bool copy, string key) =>
            _application.RunAsync(async () =>
        {
            SazExportUncertainty uncertainty = Enum.Parse<SazExportUncertainty>(form);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("uncertain.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                var run = new FakeRun(_ => Result(SazExportPublication.Uncertain, SazExportCodes.Uncertain, uncertainty));
                var dialogs = Dialogs(new CaptureExportScript { Destination = Destination("uncertain.saz") });

                CaptureExportOutcome outcome = await ExportAsync(
                    model, SazExportKind.Plain, dialogs, Hooks(copy ? CaptureArchiveKind.Copy : null), run);

                Assert.Equal((SazExportCodes.Uncertain, key), (outcome.Code, outcome.Shown[^1]));
                Assert.DoesNotContain("EXPORT_NOT_SANITIZED", outcome.Shown);
                Assert.Equal(CaptureExportSeverity.Warning, dialogs.Messages.Single().Severity);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G11: each residue has its text after the publication's: the staging file's, and the earlier file's, which is to
        // be restored only when nothing was published.
        [Theory]
        [InlineData("Created", "Staging", "EXPORT_RESIDUE_STAGING")]
        [InlineData("Replaced", "EarlierFile", "EXPORT_RESIDUE_EARLIER")]
        [InlineData("Uncertain", "EarlierFile", "EXPORT_RESIDUE_EARLIER")]
        [InlineData("NotPublished", "EarlierFile", "EXPORT_RESIDUE_RESTORE")]
        [InlineData("NotPublished", "Staging", "EXPORT_RESIDUE_STAGING")]
        public Task G11_EachResidue_HasItsText(string ended, string left, string key) =>
            _application.RunAsync(async () =>
        {
            SazExportPublication publication = Enum.Parse<SazExportPublication>(ended);
            SazExportResidueKind residue = Enum.Parse<SazExportResidueKind>(left);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            try
            {
                LoadTab(model, SavedCapture("residue.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string code = publication switch
                {
                    SazExportPublication.NotPublished => SazExportCodes.Changed,
                    SazExportPublication.Uncertain => SazExportCodes.Uncertain,
                    _ => null,
                };
                var run = new FakeRun(_ => Result(
                    publication,
                    code,
                    publication == SazExportPublication.Uncertain ? SazExportUncertainty.Rename : SazExportUncertainty.None,
                    null,
                    new SazExportResidue(residue, ".xmat-0123.tmp", "C:\\work\u202e")));
                var dialogs = Dialogs(new CaptureExportScript { Destination = Destination("residue.saz") });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs, runner: run, logged: logged);

                Assert.Equal(code, outcome.Code);
                Assert.Equal(1, outcome.Shown.Count(shown => shown == key));
                Assert.Equal(2, outcome.Shown.ToList().IndexOf(key));
                CaptureExportText text = dialogs.Messages.Single().Parts[1];
                Assert.Equal(new object[] { ".xmat-0123.tmp", "C:\\work\\u202E" }, text.Args);
                Assert.Contains(CaptureExportWorkflow.LogPrefix + SazExportCodes.CleanupFailed, logged);
                Assert.Equal(
                    publication is SazExportPublication.Created or SazExportPublication.Replaced,
                    outcome.Shown.Contains("EXPORT_NOT_SANITIZED"));
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G11: admission's own refusals of the entry: a folder or a link, and a read-only file, each left as it was.
        [Theory]
        [InlineData("folder", SazExportCodes.NotAFile, "EXPORT_NOT_A_FILE")]
        [InlineData("read-only", SazExportCodes.ReadOnly, "EXPORT_READ_ONLY")]
        public Task G11_AdmissionRefusals_OfTheEntry(string entry, string code, string key) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            string destination = Destination("entry.saz");
            try
            {
                LoadTab(model, SavedCapture("entry.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                if (entry == "folder")
                {
                    Directory.CreateDirectory(destination);
                }
                else
                {
                    File.WriteAllText(destination, "read-only file");
                    File.SetAttributes(destination, FileAttributes.ReadOnly);
                }

                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, Replace = true });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs);

                Assert.Equal((code, key), (outcome.Code, outcome.Shown[^1]));
                Assert.Empty(dialogs.Questions);
                Assert.Equal(new object[] { "entry.saz" }, dialogs.Messages.Single().Parts.Single().Args);
                if (entry == "folder")
                {
                    Assert.True(Directory.Exists(destination));
                }
                else
                {
                    Assert.Equal("read-only file", File.ReadAllText(destination));
                }
            }
            finally
            {
                if (File.Exists(destination))
                {
                    File.SetAttributes(destination, FileAttributes.Normal);
                }

                await CloseAsync(model);
            }
        });

        // G11, S2-V8: a file name with a bidirectional control is shown escaped, an over-long one bounded, and no text names
        // the tab, which for a loaded capture is file data.
        [Fact]
        public Task G11_NamesAreShownEscapedAndBounded_AndNoTextNamesTheTab() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("tab-name.xmatcap", rows => AddRow(rows, Canary, 1), false, Canary), Canary);
                string bidi = "report\u202egnp.saz";
                string longName = new string('\u200b', 60) + "long.saz";
                foreach (string name in new[] { bidi, longName })
                {
                    var run = new FakeRun(_ => Result(SazExportPublication.Created));
                    var dialogs = Dialogs(new CaptureExportScript { Destination = Destination(name) });

                    await ExportAsync(model, SazExportKind.Plain, dialogs, runner: run);

                    var shown = (string)dialogs.Messages.Single().Parts[0].Args[1];
                    Assert.Equal(UntrustedText.Show(name, 260), shown);
                    Assert.True(shown.Length <= 260);
                    Assert.DoesNotContain('\u202e', shown);
                    Assert.DoesNotContain(Texts(dialogs), text => text.Contains(Canary, StringComparison.Ordinal));
                }

                Assert.Equal("report\\u202Egnp.saz", UntrustedText.Show(bidi, 260));
                Assert.EndsWith("\u2026", UntrustedText.Show(longName, 260), StringComparison.Ordinal);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G11 (S2-V2, Q-S2-5, Q-S2-6): no outcome text says that a destination is unchanged, states how long a replaced
        // file's name is absent, or says that a copy could not be opened while it was copied.
        [Fact]
        public void G11_NoOutcomeText_SaysUnchangedOrPromisesWhatXmatCannot()
        {
            foreach (string key in OutcomeKeys)
            {
                string text = Words(key, "name.saz", "C:\\folder");
                Assert.False(text.StartsWith("NO_IMPL_", StringComparison.Ordinal), key);
                foreach (string word in new[]
                {
                    "unchanged", "second", "millisecond", "moment", "briefly", "instant", "could not be opened",
                    "cannot be opened", "can't be opened", "locked",
                })
                {
                    Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // G12: while an export runs, from its first step to its last, nothing that changes capture data can begin: closing
        // a tab, Clear, Open, Import and a replay; all of them can once it has ended.
        [Fact]
        public Task G12_WhileAnExportRuns_NothingThatChangesCaptureDataCanBegin() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var blocked = new List<string>();
            void Check(string when)
            {
                bool begun = model.TryBeginCaptureDataOperation(out IDisposable operation);
                operation?.Dispose();
                using var cancellation = new CancellationTokenSource();
                bool replay = model.TryBeginCancellableCaptureDataOperation(cancellation, out IDisposable replayLease);
                replayLease?.Dispose();
                if (!model.CanModifyCaptureData && !model.CanBeginCaptureDataOperation && !begun && !replay &&
                    !model.CanExportCaptures)
                {
                    blocked.Add(when);
                }
            }

            try
            {
                LoadTab(model, SavedCapture("busy.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string destination = Destination("busy.saz");
                File.WriteAllText(destination, "earlier file");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, Replace = true });
                dialogs.BeforeStep = progress => Check(progress.Step.ToString());
                dialogs.AfterQuestion = (question, _) => Check(question.Parts[0].Key);
                dialogs.BeforeDestination = () => Check("destination");
                dialogs.DuringStep = progress =>
                {
                    Check("during " + progress.Step);
                    return Task.CompletedTask;
                };

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs);

                Assert.Equal(SazExportPublication.Replaced, outcome.Result.Publication);
                Assert.Equal(
                    new[]
                    {
                        "Preparing", "during Preparing", "destination", "Admitting", "during Admitting",
                        "EXPORT_REPLACE_QUESTION", "Writing", "during Writing",
                    },
                    blocked);
                Assert.True(model.CanModifyCaptureData);
                Assert.True(model.CanExportCaptures);
                Assert.True(model.TryBeginCaptureDataOperation(out IDisposable after));
                after.Dispose();
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G13 (section 9, S2-V3, S2-V4, R4-1): a loaded unmarked tab. The lease and the freeze come before the binding, the
        // binding before the question, the question before planning, and planning before the destination dialog. No writes
        // nothing; Yes exports the text as stored and ends with EXPORT_NOTE_AS_STORED.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task G13_AnUnmarkedTab_IsBoundThenAskedAbout_BeforeAnythingIsPlanned(bool accept) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("unmarked.xmatcap", rows => AddRow(rows, Device, 1), true, Device), Device);
                string destination = Destination("unmarked.saz");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, AcceptAsStored = accept });

                CaptureExportOutcome outcome = await ExportAsync(
                    model,
                    SazExportKind.Plain,
                    dialogs,
                    configure: workflow => workflow.SourceFrozen = frozen =>
                    {
                        dialogs.Events.Add("freeze, lease " + model.IsCaptureDataOperationRunning);
                        Assert.Equal(CaptureTextSemantics.AmbiguousUnmarked, frozen.TextSource.Semantics);
                        return frozen;
                    });

                CaptureExportQuestion question = Assert.Single(dialogs.Questions);
                Assert.Equal(
                    ("CAPTURE_TEXT_AMBIGUOUS_TITLE", CaptureExportAnswers.YesNo, false),
                    (question.TitleKey, question.Answers, question.DefaultYes));
                string[] begun = { "freeze, lease True", "step:Binding", "ask:EXPORT_UNMARKED_QUESTION" };
                if (!accept)
                {
                    Assert.Equal(begun, dialogs.Events);
                    Assert.Null(outcome.Result);
                    Assert.Null(outcome.Code);
                    Assert.False(File.Exists(destination));
                    return;
                }

                Assert.Equal(
                    begun.Concat(new[] { "step:Preparing", "destination", "step:Admitting", "step:Writing", "show:EXPORT_CREATED" }),
                    dialogs.Events.Take(8));
                Assert.Equal("EXPORT_NOTE_AS_STORED", outcome.Shown[^1]);
                Assert.Equal(SazExportPublication.Created, outcome.Result.Publication);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G13: the sanitized export of an unmarked tab asks one question, its confirmation with the acceptance, default
        // Cancel; OK grants nothing beyond that export, so the next export asks again.
        [Fact]
        public Task G13_TheSanitizedExport_AsksOneCombinedQuestion_ThatGrantsOnlyThatExport() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("unmarked.xmatcap", rows => AddRow(rows, Device, 1), true, Device), Device);
                var sanitized = Dialogs(new CaptureExportScript { Destination = Destination("sanitized.saz") });

                CaptureExportOutcome first = await ExportAsync(model, SazExportKind.Sanitized, sanitized);

                CaptureExportQuestion question = Assert.Single(sanitized.Questions);
                Assert.Equal(
                    new[] { "SANITIZED_EXPORT_CONFIRM", "SANITIZED_EXPORT_CONFIRM_UNMARKED" },
                    question.Parts.Select(part => part.Key));
                Assert.Equal(
                    ("FILE_EXPORT_SANITIZED", CaptureExportAnswers.OkCancel, false),
                    (question.TitleKey, question.Answers, question.DefaultYes));
                Assert.Equal(SazExportPublication.Created, first.Result.Publication);
                Assert.Equal("EXPORT_NOTE_AS_STORED", first.Shown[^1]);

                var plain = Dialogs(new CaptureExportScript { Destination = Destination("plain.saz"), AcceptAsStored = false });
                CaptureExportOutcome second = await ExportAsync(model, SazExportKind.Plain, plain);

                Assert.Equal("EXPORT_UNMARKED_QUESTION", Assert.Single(plain.Questions).Parts.Single().Key);
                Assert.Null(second.Result);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G13 (R4-1): a source that cannot be bound, by IsBindable or by its binding, is refused with
        // EXPORT_SOURCE_UNAVAILABLE without a question, nothing planned or written, and a log line with the code only.
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public Task G13_ASourceThatCannotBeBound_IsRefusedWithoutAQuestion(bool bindable, bool sanitized) =>
            _application.RunAsync(async () =>
        {
            SazExportKind kind = Kind(sanitized);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            try
            {
                CaptureTextSource text = bindable
                    ? CaptureTextSource.CreateFile(
                        CaptureTextSemantics.AmbiguousUnmarked,
                        default,
                        _ => throw new IOException(Canary),
                        () => true)
                    : CaptureTextSource.Unknown;
                var controller = new WebServiceDeviceCaptureController(Device, DeviceType.GenericProxyDevice, true);
                controller.LoadCaptureDataset(new FakeDataset(text, new FakeRecord(1, Device)));
                SelectTab(model, controller);
                string destination = Destination("unbound.saz");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, AcceptAsStored = true });

                CaptureExportOutcome outcome = await ExportAsync(model, kind, dialogs, logged: logged);

                Assert.Equal(SazExportCodes.SourceUnavailable, outcome.Code);
                Assert.Equal(new[] { "EXPORT_SOURCE_UNAVAILABLE" }, outcome.Shown);
                Assert.Empty(dialogs.Questions);
                Assert.Equal(
                    bindable ? new[] { CaptureExportStep.Binding } : Array.Empty<CaptureExportStep>(),
                    dialogs.Steps.Select(step => step.Step));
                Assert.Equal(CaptureExportWorkflow.LogPrefix + SazExportCodes.SourceUnavailable, Assert.Single(logged));
                Assert.False(File.Exists(destination));
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G14 (S2-V1): no replace question for a new file; a found file is asked about after admission, default No, and No
        // writes nothing; Yes replaces it.
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public Task G14_OnlyAFileThatAdmissionFound_IsAskedAbout(bool existing, bool replace) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("replace.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string destination = Destination("replace.saz");
                if (existing)
                {
                    File.WriteAllText(destination, "earlier file");
                }

                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, Replace = replace });

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs);

                if (!existing)
                {
                    Assert.Empty(dialogs.Questions);
                    Assert.Equal(SazExportPublication.Created, outcome.Result.Publication);
                    return;
                }

                CaptureExportQuestion question = Assert.Single(dialogs.Questions);
                Assert.Equal(
                    ("FILE_EXPORT_CAPTURES", CaptureExportAnswers.YesNo, false, "EXPORT_REPLACE_QUESTION"),
                    (question.TitleKey, question.Answers, question.DefaultYes, question.Parts.Single().Key));
                Assert.Equal(new object[] { "replace.saz", _directory }, question.Parts.Single().Args);
                Assert.Equal(
                    new[] { "step:Preparing", "destination", "step:Admitting", "ask:EXPORT_REPLACE_QUESTION" },
                    dialogs.Events.Take(4));
                if (!replace)
                {
                    Assert.Equal(4, dialogs.Events.Count);
                    Assert.Equal("earlier file", File.ReadAllText(destination));
                    return;
                }

                Assert.Equal(SazExportPublication.Replaced, outcome.Result.Publication);
                Assert.Equal(new[] { "1:selected-1" }, Markers(destination));
                AssertNoStaging();
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G14: a file that appears after admission is not replaced (EXPORT_EXISTS), and a confirmed file that changes before
        // the commit is left alone (EXPORT_CHANGED).
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task G14_ANewOrChangedFile_IsLeftAlone(bool confirmed) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("race.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string destination = Destination("race.saz");
                if (confirmed)
                {
                    File.WriteAllText(destination, "earlier file");
                }

                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, Replace = true });
                dialogs.BeforeStep = progress =>
                {
                    if (progress.Step == CaptureExportStep.Writing)
                    {
                        File.WriteAllText(destination, "another file");
                    }
                };

                CaptureExportOutcome outcome = await ExportAsync(model, SazExportKind.Plain, dialogs);

                Assert.Equal(
                    confirmed ? (SazExportCodes.Changed, "EXPORT_CHANGED") : (SazExportCodes.Exists, "EXPORT_EXISTS"),
                    (outcome.Code, outcome.Shown[^1]));
                Assert.Equal("another file", File.ReadAllText(destination));
                AssertNoStaging();
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G15 (Q-S2-1; Q-S2-6): the copy kind asks nothing before a new file, shows EXPORT_COPY_NOTICE in a window that
        // opens at once, before the run starts, and waits for no answer; it ends with EXPORT_CREATED_COPY.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task G15_TheCopyKind_ShowsItsNoticeBeforeTheRun_AndAsksNothingForANewFile(bool sanitized) =>
            _application.RunAsync(async () =>
        {
            SazExportKind kind = Kind(sanitized);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("copy.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string destination = Destination("copy.saz");
                var run = new FakeRun(_ => Result(SazExportPublication.Created));
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination });

                CaptureExportOutcome outcome = await ExportAsync(model, kind, dialogs, Hooks(CaptureArchiveKind.Copy), run);

                Assert.Equal(kind == SazExportKind.Sanitized ? 1 : 0, dialogs.Questions.Count);
                Assert.DoesNotContain(dialogs.Questions, question => question.Parts[0].Key.Contains("REPLACE", StringComparison.Ordinal));
                int notice = dialogs.Events.IndexOf("notice:EXPORT_COPY_NOTICE");
                Assert.Equal(dialogs.Events.IndexOf("step:Admitting") + 1, notice);
                Assert.Equal("step:Writing", dialogs.Events[notice + 1]);
                CaptureExportProgress writing = dialogs.Steps.Single(step => step.Step == CaptureExportStep.Writing);
                Assert.Equal((TimeSpan.Zero, "EXPORT_COPY_NOTICE"), (writing.Delay, writing.Notice.Key));
                Assert.Equal(new object[] { _directory }, writing.Notice.Args);
                Assert.Single(run.Calls);
                Assert.Contains("EXPORT_CREATED_COPY", outcome.Shown);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G15: an existing file on the copy kind gets EXPORT_COPY_REPLACE_QUESTION, default No; No shows no notice and writes
        // nothing; Yes shows the notice and ends with EXPORT_REPLACED_COPY. The private kind shows no notice.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task G15_TheCopyKind_AsksBeforeReplacing_AndShowsTheNoticeOnlyAfterYes(bool replace) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(model, SavedCapture("copy.xmatcap", rows => AddRow(rows, Device, 1), false, Device), Device);
                string destination = Destination("copy.saz");
                File.WriteAllText(destination, "earlier file");
                var run = new FakeRun(_ => Result(SazExportPublication.Replaced));
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination, Replace = replace });

                CaptureExportOutcome outcome = await ExportAsync(
                    model, SazExportKind.Plain, dialogs, Hooks(CaptureArchiveKind.Copy), run);

                CaptureExportQuestion question = Assert.Single(dialogs.Questions);
                Assert.Equal(("EXPORT_COPY_REPLACE_QUESTION", false), (question.Parts[0].Key, question.DefaultYes));
                if (!replace)
                {
                    Assert.DoesNotContain("notice:EXPORT_COPY_NOTICE", dialogs.Events);
                    Assert.Empty(run.Calls);
                    Assert.Equal("earlier file", File.ReadAllText(destination));
                    return;
                }

                Assert.Equal(
                    dialogs.Events.IndexOf("ask:EXPORT_COPY_REPLACE_QUESTION") + 1,
                    dialogs.Events.IndexOf("notice:EXPORT_COPY_NOTICE"));
                Assert.Contains("EXPORT_REPLACED_COPY", outcome.Shown);

                var privateDialogs = Dialogs(new CaptureExportScript { Destination = Destination("private.saz") });
                await ExportAsync(model, SazExportKind.Plain, privateDialogs, runner: new FakeRun(_ => Result(SazExportPublication.Created)));
                Assert.DoesNotContain(privateDialogs.Events, item => item.StartsWith("notice:", StringComparison.Ordinal));
                Assert.Equal(TimeSpan.FromMilliseconds(250), privateDialogs.Steps.Single(step => step.Step == CaptureExportStep.Writing).Delay);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G16 (S2-V9): both exports call the substituted operation once, on a worker, with the plan, the admitted target and
        // its decision, the row progress and XMAT's work folder; nothing else writes the archive.
        [Fact]
        public Task G16_BothExports_RunTheSharedOperationWithTheRequestOf41() => _application.RunAsync(async () =>
        {
            int ui = Environment.CurrentManagedThreadId;
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(
                    model,
                    SavedCapture("shared.xmatcap", rows => { AddRow(rows, Device, 1); AddRow(rows, Device, 2); }, false, Device),
                    Device);
                string existing = Destination("existing.saz");
                File.WriteAllText(existing, "earlier file");
                var run = new FakeRun(_ => Result(SazExportPublication.Created));
                foreach ((SazExportKind kind, string destination) in new[]
                {
                    (SazExportKind.Plain, Destination("plain.saz")),
                    (SazExportKind.Sanitized, existing),
                })
                {
                    var dialogs = Dialogs(new CaptureExportScript { Destination = destination, Replace = true });
                    await ExportAsync(model, kind, dialogs, runner: run);
                }

                Assert.Equal(2, run.Calls.Count);
                (SazExportRequest Request, OperationContext Context, int Thread)[] calls = run.Calls.ToArray();
                for (int index = 0; index < calls.Length; index++)
                {
                    (SazExportRequest request, OperationContext context, int thread) = calls[index];
                    Assert.Equal(index == 0 ? SazExportKind.Plain : SazExportKind.Sanitized, request.Plan.Request.Kind);
                    Assert.Equal(2, request.Plan.Rows);
                    Assert.Same(SazExportBudgets.Default, request.Plan.Request.Budgets);
                    var target = Assert.IsType<SazExportDestination.ToTarget>(request.Destination);
                    Assert.Equal(index == 0 ? "plain.saz" : "existing.saz", target.Target.FileName);
                    if (index == 0)
                    {
                        Assert.Null(target.ReplaceDecision);
                    }
                    else
                    {
                        Assert.Same(target.Target, target.ReplaceDecision.Target);
                    }

                    Assert.NotNull(request.Rows);
                    Assert.Equal(CaptureExportDriver.WorkFolder, context.WorkingDirectory);
                    Assert.NotEqual(ui, thread);
                }

                Assert.False(File.Exists(Destination("plain.saz")));
                Assert.Equal("earlier file", File.ReadAllText(existing));
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G17 (R3-3, IF-6): at the lowered row budget the export goes on; one row more is refused with the limit-only text
        // before the destination dialog, with no admission and no staging; a lowered stored-data budget likewise.
        [Theory]
        [InlineData(2, false)]
        [InlineData(3, false)]
        [InlineData(1, true)]
        public Task G17_OverABudget_TheExportIsRefusedBeforeTheDestinationDialog(int rows, bool stored) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var logged = new List<string>();
            try
            {
                LoadTab(
                    model,
                    SavedCapture(
                        "budget.xmatcap",
                        table =>
                        {
                            for (int number = 1; number <= rows; number++)
                            {
                                AddRow(table, Device, number);
                            }
                        },
                        false,
                        Device),
                    Device);
                SazExportBudgets budgets = stored
                    ? SazExportBudgets.Default with { MaxTotalStoredBytes = 16 }
                    : SazExportBudgets.Default with { MaxRows = 2 };
                string destination = Destination("budget.saz");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination });

                CaptureExportOutcome outcome = await ExportAsync(
                    model, SazExportKind.Plain, dialogs, logged: logged, configure: workflow => workflow.Budgets = budgets);

                if (!stored && rows == 2)
                {
                    Assert.Contains("destination", dialogs.Events);
                    Assert.Equal(SazExportPublication.Created, outcome.Result.Publication);
                    return;
                }

                (string code, string key) = stored
                    ? (SazExportCodes.TooLarge, "EXPORT_TOO_LARGE")
                    : (SazExportCodes.TooManyRows, "EXPORT_TOO_MANY_ROWS");
                Assert.Equal(code, outcome.Code);
                Assert.Equal(new[] { key }, outcome.Shown);
                Assert.Equal(new[] { "step:Preparing", "show:" + key }, dialogs.Events);
                CaptureExportText text = dialogs.Messages.Single().Parts.Single();
                if (stored)
                {
                    Assert.Equal(new object[] { 0L }, text.Args);
                }
                else
                {
                    Assert.Equal(new object[] { 2 }, text.Args);
                    Assert.Equal("Not exported: the selected tab has more than 2 requests. Nothing was written.", text.Localize());
                }

                Assert.Equal(CaptureExportWorkflow.LogPrefix + code, Assert.Single(logged));
                Assert.False(File.Exists(destination));
                AssertNoStaging();
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G17, IF-6: planning stops once the row budget is passed: one page of 256 rows, never another one only to count.
        [Fact]
        public Task G17_PlanningStops_OnceTheRowBudgetIsPassed() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(
                    model,
                    SavedCapture(
                        "many.xmatcap",
                        table =>
                        {
                            for (int number = 1; number <= 600; number++)
                            {
                                AddRow(table, Device, number);
                            }
                        },
                        false,
                        Device),
                    Device);
                RecordingSource source = null;
                var dialogs = Dialogs(new CaptureExportScript { Destination = Destination("many.saz") });

                CaptureExportOutcome outcome = await ExportAsync(
                    model,
                    SazExportKind.Plain,
                    dialogs,
                    configure: workflow =>
                    {
                        workflow.Budgets = SazExportBudgets.Default with { MaxRows = 10 };
                        workflow.SourceFrozen = frozen => source = new RecordingSource(frozen);
                    });

                Assert.Equal(SazExportCodes.TooManyRows, outcome.Code);
                Assert.Equal(1, source.Count("page"));
                Assert.Equal(0, source.Count("row"));
                Assert.DoesNotContain("destination", dialogs.Events);
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G18 (triage section 3, R8-1): a loaded tab whose stored heads lack their empty line, with header-looking text bodies
        // and bodies of every byte value. Read back through the import's own code, no body becomes a header; Export gives
        // every body exactly, and Export Sanitized Capture the sanitizer's output: the text bodies unchanged and the binary
        // bodies omitted, flagged uninspectable and counted in the summary that it shows.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task G18_NoBodyRunsIntoItsHead_AndEachBodyIsWhatItsSurfaceKeeps(bool sanitized) => _application.RunAsync(async () =>
        {
            SazExportKind kind = Kind(sanitized);
            byte[] text = Encoding.ASCII.GetBytes("X-Injected: from-the-body\r\n\r\nplain words only\r\n");
            byte[] binary = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                LoadTab(
                    model,
                    SavedCapture(
                        "separator.xmatcap",
                        table =>
                        {
                            int number = 0;
                            foreach ((string type, byte[] body) in new[] { ("text/plain", text), ("application/octet-stream", binary) })
                            {
                                foreach (string end in new[] { "\r\n", string.Empty })
                                {
                                    number++;
                                    AddRow(
                                        table,
                                        Device,
                                        number,
                                        requestHead: $"POST /items/{number} HTTP/1.1\r\nHost: example.test\r\nContent-Type: {type}" + end,
                                        requestBody: body,
                                        responseHead: $"HTTP/1.1 200 OK\r\nContent-Type: {type}" + end,
                                        responseBody: body);
                                }
                            }
                        },
                        false,
                        Device),
                    Device);
                string destination = Destination("separator.saz");
                var dialogs = Dialogs(new CaptureExportScript { Destination = destination });

                CaptureExportOutcome outcome = await ExportAsync(model, kind, dialogs);

                ProxyConnectionModel[] read = Read(destination);
                Assert.Equal(4, read.Length);
                foreach (ProxyConnectionModel row in read)
                {
                    Assert.DoesNotContain("X-Injected", row.RequestLineAndHeaders, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("X-Injected", row.ResponseLineAndHeaders, StringComparison.OrdinalIgnoreCase);
                    bool isText = row.RequestNumber <= 2;
                    byte[] expected = kind == SazExportKind.Plain || isText ? (isText ? text : binary) : Array.Empty<byte>();
                    Assert.Equal(expected, row.RequestBody ?? Array.Empty<byte>());
                    Assert.Equal(expected, row.ResponseBody ?? Array.Empty<byte>());
                }

                if (kind == SazExportKind.Sanitized)
                {
                    SanitizedExportSummary summary = outcome.Result.Summary.Sanitized;
                    Assert.Equal(4, summary.OmittedBodies);
                    Assert.Equal(summary.OmittedBodies, dialogs.Messages.Single().Parts[1].Args[2]);
                    using ZipArchive archive = ZipFile.OpenRead(destination);
                    foreach (int number in new[] { 3, 4 })
                    {
                        XDocument metadata = XDocument.Load(archive.GetEntry($"raw/{number}_m.xml").Open());
                        Assert.Contains(metadata.Descendants("SessionFlag"), flag =>
                            (string)flag.Attribute("N") == "x-xmat-request-body-omitted" &&
                            (string)flag.Attribute("V") == "uninspectable");
                    }
                }
            }
            finally
            {
                await CloseAsync(model);
            }
        });

        // G19 (Astra's Q3; 3.11 a): a Raw row end to end, an attempted send included. A composer records two raw sends into
        // the Replays tab and into the capture table that the tab's export reads. Each send connects to a scripted connection
        // (SeamConnection), and every other route that its run could take ends at RefusingReplayTransport, so no socket
        // opens. The first connection takes every write call and answers. The second send's prepared bytes pass 16 KiB, and
        // it is cancelled between its first and second write calls: a partial write of 16,384 bytes, with no response. Each
        // request has a field with white space around its value and a chunked body. The export, read back through the
        // import's own code and BoundedSazReader, holds each request as its row's normalized view: A2's request line, the
        // row's fields and the whole entity, never the prepared bytes, a chunk's framing or an untrimmed value; no flag but
        // the row's client address, and no text of a note; the completed send's response, and none for the attempted send.
        [Fact]
        public Task G19_ARawRowIsExportedAsItsNormalizedView_AnAttemptedSendIncluded() => _application.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            string authority = new Uri(ComposerRawRecordingTests.RawTarget).Authority;
            string chunked = "6\r\nxmat-a\r\n6\r\nxmat-b\r\n0\r\n\r\n";
            string entity = new('b', 20_000);
            using SqLtDatabase database = CaptureDatabase(out IDataTable table, out _);
            var model = new CaptureAppModel(dispatcher);
            ICaptureDeviceContext replays = model.GetOrAddReplayContext();
            typeof(WebServiceDeviceCaptureController)
                .GetField("_dataTable", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(replays.CaptureController, table);
            var completed = new SeamConnection();
            completed.Respond("HTTP/1.1 200 OK\r\nContent-Length: 9\r\n\r\nreply-one");
            var attempted = new SeamConnection();
            var connections = new ConcurrentQueue<SeamConnection>(new[] { completed, attempted });
            using var transport = new RefusingReplayTransport();
            using RequestReplayClient client = ComposerRawRecordingTests.Scripted(
                transport,
                completed,
                seams: setup => setup with
                {
                    Connector = (_, _) => Task.FromResult<Stream>(
                        connections.TryDequeue(out SeamConnection next)
                            ? next
                            : throw new InvalidOperationException("A third send.")),
                });
            var window = RequestComposerWindow.ForNewRequest(
                client, _ => true, ComposerRawRecordingTests.Recording(model, table));
            var runs = new List<ReplayRecorder>();
            window.RunEnded = runs.Add;
            attempted.AfterWrite = index =>
            {
                if (index == 0)
                {
                    dispatcher.Invoke(window.CancelSend);
                }
            };
            string destination = Destination("raw.saz");
            try
            {
                await ComposerRawRecordingTests.EnterRawModeAsync(window, ComposerRawRecordingTests.RawTarget, "x");
                foreach ((string value, string body) in new[]
                {
                    ("\t first value \t", chunked),
                    ("\t second value \t", "4e20\r\n" + entity + "\r\n0\r\n\r\n"),
                })
                {
                    ComposerRawRecordingTests.Find<TextBox>(window, "RawHeadBox").Text =
                        $"POST /items HTTP/1.1\r\nHost: {authority}\r\nX-Padded: {value}\r\nTransfer-Encoding: chunked";
                    ComposerRawRecordingTests.Find<TextBox>(window, "BodyBox").Text = body;
                    await window.SendAsync();
                }

                Assert.Equal(2, runs.Count);
                ReplayItemResult sent = runs[0].LastItem;
                ReplayItemResult tried = runs[1].LastItem;
                Assert.Equal(
                    (true, ReplayOutcome.Completed, RawWriteState.Complete),
                    (sent.Raw, sent.Outcome, sent.RequestWrite));
                Assert.Equal(
                    (true, RawCodes.SendCancelled, RawWriteState.Partial, (long?)RawRequestImage.WriteBytes),
                    (tried.Raw, tried.Code, tried.RequestWrite, tried.RequestBytesWritten));
                Assert.Equal((long)RawRequestImage.WriteBytes, attempted.Written.Length);
                Assert.Empty(transport.Attempts);

                // The rows hold the normalized views: each value trimmed, and Transfer-Encoding replaced by
                // Content-Length with the entity, the attempted send's whole entity included.
                IDataRecord[] rows = table.Dataset.DataRecords.ToArray();
                string padded = "POST /items HTTP/1.1\r\nHost: " + authority + "\r\nX-Padded: ";
                Assert.Equal(
                    new[]
                    {
                        padded + "first value\r\nContent-Length: 12\r\n\r\n",
                        padded + "second value\r\nContent-Length: 20000\r\n\r\n",
                    },
                    rows.Select(row => row.Str(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders)));
                Assert.Equal(
                    new[] { "xmat-axmat-b", entity },
                    rows.Select(row =>
                        Encoding.ASCII.GetString(Stored(row, WebServiceCaptureMethod.FieldKey_RequestBody))));

                model.SelectedDeviceContext = replays;
                CaptureExportOutcome outcome = await ExportAsync(
                    model, SazExportKind.Plain, Dialogs(new CaptureExportScript { Destination = destination }));
                Assert.Equal(SazExportPublication.Created, outcome.Result.Publication);

                string[] absent =
                {
                    Encoding.Latin1.GetString(completed.Written.ToArray()),
                    Encoding.Latin1.GetString(attempted.Written.ToArray()),
                    chunked,
                    "4e20\r\n",
                    "\t first value \t",
                    "\t second value \t",
                    "Transfer-Encoding",
                    RawSendWording.StateSentence(RawWriteState.Complete),
                    RawSendWording.StateSentence(RawWriteState.Partial),
                    Words("COMPOSER_RAW_HANDED", (long)RawRequestImage.WriteBytes),
                    RawRequest.Message(RawCodes.SendCancelled),
                };
                using (ZipArchive archive = ZipFile.OpenRead(destination))
                {
                    for (int number = 1; number <= rows.Length; number++)
                    {
                        IDataRecord row = rows[number - 1];
                        string head = row.Str(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders);
                        Assert.Equal(
                            Encoding.Latin1.GetBytes(
                                    $"POST {ComposerRawRecordingTests.RawTarget}/items HTTP/1.1" +
                                    head.Substring(head.IndexOf("\r\n", StringComparison.Ordinal)))
                                .Concat(Stored(row, WebServiceCaptureMethod.FieldKey_RequestBody))
                                .ToArray(),
                            Entry(archive, $"raw/{number}_c.txt"));
                        Assert.Equal(
                            new[] { ("x-clientip", row.Str(WebServiceCaptureMethod.FieldKey_ClientIP)) },
                            XDocument.Load(new MemoryStream(Entry(archive, $"raw/{number}_m.xml")))
                                .Descendants("SessionFlag")
                                .Select(flag => ((string)flag.Attribute("N"), (string)flag.Attribute("V"))));
                    }

                    Assert.Equal(
                        Encoding.Latin1.GetBytes(rows[0].Str(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders))
                            .Concat(Stored(rows[0], WebServiceCaptureMethod.FieldKey_ResponseBody))
                            .ToArray(),
                        Entry(archive, "raw/1_s.txt"));
                    Assert.Null(archive.GetEntry("raw/2_s.txt"));
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string text = Encoding.Latin1.GetString(Entry(archive, entry.FullName));
                        Assert.All(absent, part => Assert.DoesNotContain(part, text, StringComparison.Ordinal));
                    }
                }

                FiddlerSazImportResult imported =
                    FiddlerSazHandler.ImportSazFileWithDiagnostics(destination, CancellationToken.None);
                BoundedSazReadResult bounded = BoundedSazReader.Read(
                    new MemoryStream(File.ReadAllBytes(destination)),
                    new BoundedSazLimits(
                        1L << 30, 1L << 28, 1L << 30, 1000, 3064, BoundedSazReader.DefaultMaxCentralDirectoryBytes),
                    CancellationToken.None);
                foreach ((IReadOnlyList<ProxyConnectionModel> records, int incomplete, int malformed) in
                    new (IReadOnlyList<ProxyConnectionModel>, int, int)[]
                    {
                        (imported.Records.ToList(), imported.SkippedIncompleteFrames,
                            imported.SkippedMalformedHeaderLines),
                        (bounded.Records, bounded.SkippedIncompleteFrames, bounded.SkippedMalformedHeaderLines),
                    })
                {
                    Assert.Equal((0, 0), (incomplete, malformed));
                    Assert.Equal(
                        new[]
                        {
                            ("POST", "http", "/items", "xmat-axmat-b", "reply-one"),
                            ("POST", "http", "/items", entity, ""),
                        },
                        records.Select(record => (
                            record.Method,
                            record.Scheme,
                            record.Path,
                            Encoding.ASCII.GetString(record.RequestBody ?? Array.Empty<byte>()),
                            Encoding.ASCII.GetString(record.ResponseBody ?? Array.Empty<byte>()))));
                }
            }
            finally
            {
                window.Close();
                await CloseAsync(model);
            }
        });

        // The application's dialogs (G6, G15): with a notice, the progress window opens before the step starts and shows the
        // notice, and its Cancel reaches the export.
        [Fact]
        public Task WindowDialogs_OpenTheNoticeBeforeTheStep_AndCancelReachesTheExport() => _application.RunAsync(async () =>
        {
            Window owner = OwnerWindow();
            var step = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new List<Exception>();
            var cancels = 0;
            var progress = new CaptureExportProgress(
                CaptureExportStep.Writing,
                "FILE_EXPORT_CAPTURES",
                "EXPORT_PROGRESS_STATUS",
                TimeSpan.Zero,
                new CaptureExportText("EXPORT_COPY_NOTICE", "C:\\share"),
                () =>
                {
                    cancels++;
                    step.TrySetResult();
                });
            SanitizedExportProgressWindow shown = null;
            string notice = null;
            EndAfterPatience(step);
            try
            {
                await new MainWindow.CaptureExportDialogs().RunAsync(owner, progress, () =>
                {
                    Contain(step, failures, () =>
                    {
                        shown = OpenProgressWindow() ?? throw new InvalidOperationException("No progress window is open.");
                        notice = ((TextBlock)shown.FindName("NoticeText")).Text;
                        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                            DispatcherPriority.ApplicationIdle,
                            new Action(() => Contain(step, failures, () => ((Button)shown.FindName("CancelExport"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)))));
                    });
                    return step.Task;
                }).WaitAsync(Patience);

                ThrowFirst(failures);
                Assert.Equal(Words("EXPORT_COPY_NOTICE", "C:\\share"), notice);
                Assert.Equal(1, cancels);
                Assert.True(progress.IsCancellationRequested);
                Assert.False(shown.IsVisible);
            }
            finally
            {
                step.TrySetResult();
                owner.Close();
            }
        });

        // The application's dialogs (G6): the count line follows the run's row progress, and once the publication has
        // started, Cancel, Escape and closing the window cancel nothing.
        [Fact]
        public Task WindowDialogs_CountTheRows_AndTurnCancelOffOncePublishing() => _application.RunAsync(async () =>
        {
            Window owner = OwnerWindow();
            var step = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new List<Exception>();
            var cancels = 0;
            var progress = new CaptureExportProgress(
                CaptureExportStep.Writing,
                "FILE_EXPORT_CAPTURES",
                "EXPORT_PROGRESS_STATUS",
                TimeSpan.Zero,
                null,
                () => cancels++);
            string count = null;
            bool cancelEnabled = true;
            EndAfterPatience(step);
            try
            {
                await new MainWindow.CaptureExportDialogs().RunAsync(owner, progress, () =>
                {
                    Contain(step, failures, () =>
                    {
                        SanitizedExportProgressWindow shown =
                            OpenProgressWindow() ?? throw new InvalidOperationException("No progress window is open.");
                        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                            DispatcherPriority.ApplicationIdle,
                            new Action(() => Contain(step, failures, () =>
                            {
                                progress.ReportRows(new SazExportProgress(3, 7));
                                count = ((TextBlock)shown.FindName("CountText")).Text;
                                progress.ReportPhase(new OperationProgress(SazExportCodes.PublishingPhase));
                                var cancel = (Button)shown.FindName("CancelExport");
                                cancelEnabled = cancel.IsEnabled;
                                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                shown.RaiseEvent(new KeyEventArgs(
                                    Keyboard.PrimaryDevice,
                                    PresentationSource.FromVisual(shown),
                                    Environment.TickCount,
                                    Key.Escape)
                                {
                                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                                });
                                shown.Close();
                                step.TrySetResult();
                            })));
                    });
                    return step.Task;
                }).WaitAsync(Patience);

                ThrowFirst(failures);
                Assert.Equal(Words("EXPORT_PROGRESS_COUNT", 3, 7), count);
                Assert.False(cancelEnabled);
                Assert.Equal(0, cancels);
                Assert.False(progress.CanCancel);
            }
            finally
            {
                step.TrySetResult();
                owner.Close();
            }
        });

        private static ScriptedCaptureExportDialogs Dialogs(CaptureExportScript script) => new(script);

        // Runs part of a window test where nothing may throw: in the progress window's own event, which starts the step,
        // or in an action queued on the shared dispatcher. A failure is kept for ThrowFirst, once the window has closed,
        // and ends the step, which closes the window.
        private static void Contain(TaskCompletionSource step, List<Exception> failures, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                failures.Add(exception);
                step.TrySetResult();
            }
        }

        // The progress window is modal and closes once its step has ended. With no delay, RunAsync shows it before it
        // returns a task that WaitAsync could bound, so the step also ends after the patience, whatever the test does.
        private static void EndAfterPatience(TaskCompletionSource step) =>
            _ = Task.Delay(Patience).ContinueWith(_ => step.TrySetResult(), TaskScheduler.Default);

        private static void ThrowFirst(List<Exception> failures)
        {
            if (failures.Count > 0)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
        }

        // The export that a theory names by a public type: the core's types are internal.
        private static SazExportKind Kind(bool sanitized) => sanitized ? SazExportKind.Sanitized : SazExportKind.Plain;

        private static SazExportOperation Hooks(CaptureArchiveKind? kind) =>
            kind == null ? null : new SazExportOperation(new SazExportTestHooks(AdmitAsKind: kind));

        private static async Task<CaptureExportOutcome> ExportAsync(
            CaptureAppModel model,
            SazExportKind kind,
            ScriptedCaptureExportDialogs dialogs,
            SazExportOperation operation = null,
            IOperation<SazExportRequest, SazExportResult> runner = null,
            List<string> logged = null,
            Action<CaptureExportWorkflow> configure = null)
        {
            CaptureExportWorkflow workflow = CaptureExportDriver.Workflow(dialogs, operation, runner);
            List<string> log = logged ?? new List<string>();
            workflow.LogError = log.Add;
            configure?.Invoke(workflow);
            CaptureExportOutcome outcome = await workflow.RunAsync(model, kind, owner: null);
            Assert.False(model.IsCaptureDataOperationRunning);
            return outcome;
        }

        private string Destination(string name) => Path.Join(_directory, name);

        private void AssertNoStaging()
        {
            Assert.Empty(Directory.GetFiles(_directory, ".xmat-*", SearchOption.AllDirectories));
            if (Directory.Exists(CaptureExportDriver.WorkFolder))
            {
                Assert.Empty(Directory.GetFiles(CaptureExportDriver.WorkFolder, ".xmat-*"));
            }
        }

        // A capture file with the rows that addRows adds, under one capture-devices row per device, as File > Save Captures
        // writes it; unmarked sets its user_version to 0, as XMAT before slice Q left it.
        private string SavedCapture(string name, Action<IDataTable> addRows, bool unmarked, params string[] devices)
        {
            string path = Path.Join(_directory, name);
            using (SqLtDatabase database = CaptureDatabase(out IDataTable table, out IDataTable deviceTable))
            {
                foreach (string device in devices)
                {
                    deviceTable.AddRow((long)DeviceType.GenericProxyDevice, device, (long)CaptureType.WebProxy);
                }

                addRows(table);
                database.SaveToFile(path);
            }

            if (unmarked)
            {
                HostileSqliteSchemas.SetUserVersion(path, 0);
            }

            return path;
        }

        // Same layout as WebServiceCaptureMethod.InitializeDataTables(), in memory, with the capture devices table, as
        // CaptureAppModel.InitializeDatabase() makes it.
        private static SqLtDatabase CaptureDatabase(out IDataTable table, out IDataTable devices)
        {
            var database = new SqLtDatabase();
            database.Initialize();
            table = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                new Field<long>(WebServiceCaptureMethod.FieldKey_RequestNumber),
                new Field<long>(WebServiceCaptureMethod.FieldKey_ConnectionId),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestScheme),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestHost),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPort),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPath),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestStatus),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP));
            devices = database.CreateTable(
                "CaptureDevices",
                new Field<long>("DeviceType"),
                new Field<string>("DeviceName"),
                new Field<long>("CaptureType"));
            return database;
        }

        // One exchange of device, numbered number, whose response body names it ("device-number") unless given.
        private static void AddRow(
            IDataTable table,
            string device,
            long number,
            string requestHead = null,
            byte[] requestBody = null,
            string responseHead = null,
            byte[] responseBody = null,
            string status = "200")
        {
            requestBody ??= Array.Empty<byte>();
            responseBody ??= Encoding.ASCII.GetBytes(device + "-" + number);
            DateTime started = Start.AddSeconds(number);
            table.AddRow(
                device,
                number,
                number,
                ProxyConnectionModel.ToDateTimeString(started),
                ProxyConnectionModel.ToDateTimeString(started.AddMilliseconds(25)),
                "https",
                "example.test",
                "443",
                "GET",
                "/items/" + number,
                status,
                requestHead ?? $"GET /items/{number} HTTP/1.1\r\nHost: example.test\r\n\r\n",
                Convert.ToBase64String(requestBody),
                responseHead ?? $"HTTP/1.1 {status} Status\r\nContent-Length: {responseBody.Length}\r\n\r\n",
                Convert.ToBase64String(responseBody),
                "10.0.0.1");
        }

        // A read-only web proxy tab whose rows are device's rows in table, as an imported tab and the Replays tab read the
        // shared capture table, with those rows shown.
        private static WebServiceDeviceCaptureController ActiveTab(
            CaptureAppModel model, string device, IDataTable table, bool select = true)
        {
            var controller = new WebServiceDeviceCaptureController(device, DeviceType.GenericProxyDevice, true);
            typeof(WebServiceDeviceCaptureController)
                .GetField("_dataTable", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, table);
            foreach (IDataRecord record in controller.GetCaptureRecords().ToList())
            {
                controller.ProxyConnections.AddFromDataRecord(record, true);
            }

            var context = new Context(controller);
            model.CaptureDeviceContexts.Add(context);
            if (select)
            {
                model.SelectedDeviceContext = context;
            }

            return controller;
        }

        private static void SelectTab(CaptureAppModel model, WebServiceDeviceCaptureController controller)
        {
            var context = new Context(controller);
            model.CaptureDeviceContexts.Add(context);
            model.SelectedDeviceContext = context;
        }

        // Loads a saved capture as File > Load Captures does, and selects device's tab.
        private static void LoadTab(CaptureAppModel model, string path, string device)
        {
            model.LoadDataCaptures(path);
            model.SelectedDeviceContext = model.CaptureDeviceContexts.Single(
                context => context.DeviceName == device && context.IsReadOnly && context.CaptureController is
                    WebServiceDeviceCaptureController { ProxyConnections.Count: > 0 });
        }

        private static async Task CloseAsync(CaptureAppModel model)
        {
            model.SelectedDeviceContext = null;
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                if (context.IsReadOnly)
                {
                    await model.RemoveDeviceContextAsync(context);
                }
                else
                {
                    model.CaptureDeviceContexts.Remove(context);
                }
            }
        }

        private static ProxyConnectionModel[] Read(string path) =>
            FiddlerSazHandler.ImportSazFile(path, CancellationToken.None).ToArray();

        private static byte[] Entry(ZipArchive archive, string name)
        {
            ZipArchiveEntry entry = archive.GetEntry(name);
            Assert.True(entry != null, name + " is missing.");
            using Stream stream = entry.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }

        // A body as a table row stores it, in Base64.
        private static byte[] Stored(IDataRecord row, string field) => Convert.FromBase64String(row.Str(field));

        // Each session read back, as its number and its response body.
        private static string[] Markers(string path) =>
            Read(path)
                .Select(row => row.RequestNumber + ":" + Encoding.ASCII.GetString(row.ResponseBody ?? Array.Empty<byte>()))
                .ToArray();

        // Every text that the dialogs showed, localized.
        private static IEnumerable<string> Texts(ScriptedCaptureExportDialogs dialogs) =>
            dialogs.Messages.SelectMany(message => message.Parts)
                .Concat(dialogs.Questions.SelectMany(question => question.Parts))
                .Concat(dialogs.Steps.Where(step => step.Notice != null).Select(step => step.Notice))
                .Select(text => text.Localize())
                .ToList();

        private static string Words(string key, params object[] args) => Localization.GetLocalizedString(key, args);

        private static SazExportSummary Summary(
            int sessions = 2, int notes = 0, long archiveBytes = 4_096, SanitizedExportSummary sanitized = null) =>
            new(sessions, sessions, sessions * 3, notes, notes, notes, notes, notes, notes, notes, notes, notes, 0, notes,
                notes, archiveBytes, sanitized);

        private static OperationResult<SazExportResult> Result(
            SazExportPublication publication,
            string code = null,
            SazExportUncertainty uncertainty = SazExportUncertainty.None,
            SazExportSummary summary = null,
            params SazExportResidue[] residues)
        {
            OperationOutcome outcome =
                residues.Length > 0 ? OperationOutcome.CleanupFailed :
                publication is SazExportPublication.Created or SazExportPublication.Replaced ? OperationOutcome.Succeeded :
                code == SazExportCodes.Cancelled ? OperationOutcome.Cancelled :
                OperationOutcome.Failed;
            return new OperationResult<SazExportResult>(
                outcome,
                new SazExportResult(publication, uncertainty, code, summary ?? Summary(), residues),
                code == null
                    ? Array.Empty<OperationDiagnostic>()
                    : new[] { new OperationDiagnostic(code, OperationDiagnosticSeverity.Error, "A fixed message.") });
        }

        private static Window OwnerWindow()
        {
            var owner = new Window
            {
                Width = 200,
                Height = 100,
                Left = -12000,
                Top = -12000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
            };
            owner.Show();
            return owner;
        }

        private static SanitizedExportProgressWindow OpenProgressWindow() =>
            Application.Current.Windows.OfType<SanitizedExportProgressWindow>().SingleOrDefault(window => window.IsVisible);

        // A substituted run (G16): records each request with its context and thread, and returns result's value.
        private sealed class FakeRun(Func<SazExportRequest, OperationResult<SazExportResult>> result) :
            IOperation<SazExportRequest, SazExportResult>
        {
            internal ConcurrentQueue<(SazExportRequest Request, OperationContext Context, int Thread)> Calls { get; } = new();

            public Task<OperationResult<SazExportResult>> ExecuteAsync(
                SazExportRequest request, OperationContext context, CancellationToken cancellationToken)
            {
                Calls.Enqueue((request, context, Environment.CurrentManagedThreadId));
                return Task.FromResult(result(request));
            }
        }

        // Wraps an export's frozen source (CaptureExportWorkflow.SourceFrozen) and records each call with its thread. A row
        // read can wait at a gate that the test opens, and then sees the export's cancellation.
        private sealed class RecordingSource : SazExportSource
        {
            private readonly SazExportSource _inner;

            internal RecordingSource(SazExportSource inner)
                : base(inner.TextSource)
            {
                _inner = inner;
            }

            internal ConcurrentQueue<(string Call, int Thread)> Calls { get; } = new();

            internal TaskCompletionSource RowReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal ManualResetEventSlim RowGate { get; init; }

            internal override bool IsUnsupported => _inner.IsUnsupported;

            internal int Count(string call) => Calls.Count(item => item.Call == call);

            internal override long? GetWatermark(CancellationToken cancellationToken)
            {
                Record("watermark");
                return _inner.GetWatermark(cancellationToken);
            }

            internal override SazExportPage ReadPage(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                Record("page");
                return _inner.ReadPage(afterRowId, throughRowId, scan, budgets, cancellationToken);
            }

            internal override StoredRow ReadRow(SazExportKey key, SazExportBudgets budgets, CancellationToken cancellationToken)
            {
                Record("row");
                RowReached.TrySetResult();
                if (RowGate != null)
                {
                    RowGate.Wait(Patience);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return _inner.ReadRow(key, budgets, cancellationToken);
            }

            private void Record(string call) => Calls.Enqueue((call, Environment.CurrentManagedThreadId));
        }

        // A dataset that no export reads, with the text source given, as a test double of the data layer.
        private sealed class FakeDataset(CaptureTextSource source, params IDataRecord[] records) :
            IDataset,
            ICaptureTextSourceProvider
        {
            public CaptureTextSource CaptureTextSource => source;

            public IEnumerable<IFieldDefinition> FieldDefs => Array.Empty<IFieldDefinition>();

            public IEnumerable<IDataRecord> DataRecords => records;

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId) =>
                records.Where(record => record.RowId >= firstRowId);

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId, int limit) =>
                DataRecordsFrom(firstRowId).Take(limit);

            public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] filters) =>
                records.Where(record => filters.All(filter => record.Str(filter.Name) == (filter.Value as string)));
        }

        private sealed class FakeRecord(int number, string device) : IDataRecord
        {
            public int RowId => number;

            public long Int(string name) => name == WebServiceCaptureMethod.FieldKey_RequestNumber ? number : 1;

            public string Str(string name) => name switch
            {
                WebServiceCaptureMethod.FieldKey_DeviceName => device,
                WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders => "GET / HTTP/1.1\r\nHost: example.test\r\n\r\n",
                WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
                WebServiceCaptureMethod.FieldKey_RequestStatus => "200",
                WebServiceCaptureMethod.FieldKey_RequestMethod => "GET",
                _ => string.Empty,
            };
        }

        private sealed class Context(IDeviceCaptureController controller, CaptureType type = CaptureType.WebProxy) :
            ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.GenericProxyDevice;

            public CaptureType CaptureType => type;

            public string DeviceName => (controller as WebServiceDeviceCaptureController)?.DeviceName;

            public bool IsSelected { get; set; }

            public bool IsReadOnly { get; set; } = true;

            public System.Drawing.Color DeviceColor { get; set; }

            public IDeviceCaptureController CaptureController => controller;
        }
    }
}
