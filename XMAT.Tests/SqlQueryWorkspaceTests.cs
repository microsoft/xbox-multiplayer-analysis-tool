// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.Models;
using XMAT.SharedInterfaces;
using static XMAT.Tests.SqlQueryWorkspaceTestData;

namespace XMAT.Tests
{
    public class SqlQueryWorkspaceTests
    {
        public SqlQueryWorkspaceTests() => Localization.LoadLanguage("en-us");

        [Fact]
        public async Task DefaultsToActiveAndRetainsAnExplicitStaleLoadedIdentityWithoutRelabelingResults()
        {
            var sources = new Sources();
            await using var workspace = new SqlQueryWorkspaceModel(sources, sourcePath: _ => @"C:\captures\loaded.xmatcap");
            var document = workspace.NewDocument();
            document.Text = "SELECT 1";
            await workspace.RefreshSourcesAsync();
            Assert.Equal(sources.ActiveId, document.SelectedSource.Id);
            document.SelectedSource = workspace.Sources.Single(source => source.Id == sources.LoadedId);
            Assert.Equal(@"C:\captures\loaded.xmatcap", document.SelectedSource.ToolTip);
            Assert.True(await document.RunAsync());
            var retained = document.ResultRun;

            sources.Items = [new(sources.ActiveId, QuerySourceKind.Active, "active"),
                new(Guid.NewGuid(), QuerySourceKind.Loaded, "replacement.xmatcap")];
            await workspace.RefreshSourcesAsync();
            Assert.Equal(sources.LoadedId, document.SelectedSource.Id);
            Assert.False(document.SelectedSource.IsAvailable);
            Assert.Contains(document.SelectedSource, document.SourceChoices);
            Assert.False(document.CanRun);
            Assert.False(await document.RunAsync());
            Assert.Same(retained, document.ResultRun);
            Assert.Equal(sources.LoadedId, retained.SourceId);
            Assert.Single(sources.ExecutedSql);
        }

        [Fact]
        public async Task StoppedCaptureRequirementDoesNotBlockDraftEditingOrCompletedResultExport()
        {
            var canSnapshot = true;
            var sources = new Sources();
            await using var workspace = new SqlQueryWorkspaceModel(sources, canSnapshot: () => canSnapshot);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT 1";
            Assert.True(await document.RunAsync());
            canSnapshot = false;
            workspace.NotifySnapshotAvailability();
            document.Text = "SELECT 2";
            Assert.False(document.CanRun);
            Assert.True(document.CanEditFiles);
            Assert.True(document.CanExportAll);
            Assert.False(await document.RefreshSchemaAsync());
            Assert.Equal("SELECT 1", document.ResultRun.Sql);
            canSnapshot = true;
            workspace.NotifySnapshotAvailability();
            Assert.True(document.CanRun);
        }

        [Fact]
        public async Task SelectionExecutionCapturesSqlSourceOffsetAndOptionsBeforeEditingOrSourceChanges()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sources = new Sources
            {
                Execute = (_, _, _) => { entered.TrySetResult(); return release.Task; }
            };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "-- λ😀\r\nSELECT 42;\r\nSELECT 99;";
            var start = document.Text.IndexOf("SELECT", StringComparison.Ordinal);
            document.TimeoutSeconds = 12;
            document.MaximumResultMiB = 2;
            var run = document.RunAsync(start, "SELECT 42;".Length);
            await entered.Task;
            document.Text = "a newer draft";
            document.SelectedSource = workspace.Sources.Single(source => source.Kind == QuerySourceKind.Loaded);
            release.TrySetResult(new Result(1));
            Assert.True(await run);
            Assert.Equal("SELECT 42;", Assert.Single(sources.ExecutedSql));
            Assert.Equal(sources.ActiveId, document.ResultRun.SourceId);
            Assert.Equal(start, document.ResultRun.EditorOffset);
            Assert.Equal("a newer draft", document.Text);
            Assert.Equal(TimeSpan.FromSeconds(12), sources.LastOptions.Timeout);
            Assert.Equal(2L * 1024 * 1024, sources.LastOptions.MaximumResultBytes);
            Assert.Equal(1, sources.DisposedSessions);
        }

        [Theory]
        [InlineData(QueryError.InvalidQuery)]
        [InlineData(QueryError.ResultLimit)]
        [InlineData(QueryError.Timeout)]
        public async Task FailedRunPreservesSuccessfulResultAndRecordsSessionOnlyHistory(QueryError error)
        {
            var result = new Result(4);
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT successful";
            Assert.True(await document.RunAsync());
            var oldRun = document.ResultRun;
            sources.Execute = (_, _, _) => throw new QueryException(error, "verbatim SQLite or quota detail λ");
            document.Text = "SELECT failing";
            Assert.False(await document.RunAsync());
            Assert.Same(oldRun, document.ResultRun);
            Assert.False(result.Disposed);
            Assert.Contains("verbatim SQLite or quota detail λ", document.MessageText);
            Assert.Equal(4, document.RowCount);
            Assert.Equal(2, workspace.History.Count);
            Assert.Equal("SELECT failing", workspace.History[0].Sql);
            var reopened = workspace.Reopen(workspace.History[1]);
            Assert.Equal("SELECT successful", reopened.Text);
            Assert.True(reopened.IsDirty);
            Assert.Null(reopened.ResultRun);
            Assert.Equal(2, sources.ExecutedSql.Count);
        }

        [Fact]
        public async Task RejectsDoubleRunAndCancelsNativeWorkWithoutReplacingThePreviousResult()
        {
            var result = new Result(1);
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT 1";
            Assert.True(await document.RunAsync());
            sources.Execute = async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new Result(1);
            };
            var run = document.RunAsync();
            Assert.True(document.IsBusy);
            Assert.False(await document.RunAsync());
            document.Cancel();
            Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(document.IsBusy);
            Assert.False(result.Disposed);
            Assert.Equal(result.Id, document.ResultRun.ResultId);
            Assert.Equal(2, sources.ExecutedSql.Count);
            Assert.Equal(2, sources.DisposedSessions);
        }

        [Fact]
        public async Task SourceChangeInvalidatesAnInFlightSchemaWithoutPublishingItUnderAnotherSource()
        {
            var gate = new TaskCompletionSource<IReadOnlyList<QueryTable>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sources = new Sources { ReadSchema = (_, _) => gate.Task };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            var read = document.RefreshSchemaAsync();
            document.SelectedSource = workspace.Sources.Single(source => source.Kind == QuerySourceKind.Loaded);
            gate.TrySetResult(sources.Schema);
            await read;
            Assert.Empty(document.Schema);
            Assert.Empty(document.Examples);
            Assert.Equal(sources.LoadedId, document.SelectedSource.Id);
            sources.ReadSchema = null;
            Assert.True(await document.RefreshSchemaAsync());
            Assert.Equal("id", document.Schema[0].Columns[0].Name);
            document.SchemaSearch = "TEXT";
            Assert.Single(document.SchemaItems);
            Assert.Equal("some value", Assert.Single(document.SchemaItems[0].Children).Name);
        }

        [Fact]
        public async Task FailedFirstPageNeverPublishesCandidateAndSuccessfulReplacementDisposesOldStore()
        {
            var first = new Result(1);
            var broken = new Result(1)
            {
                PageReader = (_, _, _, _) => throw new IOException("result read failed")
            };
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(first) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT 1";
            Assert.True(await document.RunAsync());
            sources.Execute = (_, _, _) => Task.FromResult<IQueryResult>(broken);
            Assert.False(await document.RunAsync());
            Assert.True(broken.Disposed);
            Assert.False(first.Disposed);
            Assert.Equal(first.Id, document.ResultRun.ResultId);
            var empty = new Result(0) { Columns = [new(0, "", null, null, null), new(1, "same", null, null, null)] };
            sources.Execute = (_, _, _) => Task.FromResult<IQueryResult>(empty);
            Assert.True(await document.RunAsync());
            Assert.True(first.Disposed);
            Assert.Empty(document.PageRows);
            Assert.Equal(2, document.ResultColumns.Count);
            Assert.Equal(0, document.RowCount);
        }

        [Fact]
        public async Task CloseCancelsAndDrainsAnUncooperativePageReaderBeforeDisposalWithoutPublishingPartialSuccess()
        {
            var release = new TaskCompletionSource<QueryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = new Result(1);
            result.PageReader = (_, _, _, _) =>
            {
                reading.TrySetResult();
                return release.Task;
            };
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT waiting";
            var run = document.RunAsync();
            await reading.Task;
            var close = workspace.DisposeAsync().AsTask();
            Assert.False(close.IsCompleted);
            Assert.False(result.Disposed);
            release.TrySetResult(result.Page(0, 1));
            Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(5)));
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.Disposed);
            Assert.Null(document.ResultRun);
            Assert.Empty(workspace.Documents);
        }

        [Fact]
        public async Task CancelledApplicationClosePreservesDirtyDraftsAndResultsAndCanBeRequestedAgain()
        {
            var result = new Result(2);
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT dirty";
            Assert.True(await document.RunAsync());
            var prompts = 0;
            Assert.False(await workspace.RequestCloseAsync(_ => { prompts++; return Task.FromResult(false); }));
            Assert.False(workspace.IsClosing);
            Assert.True(document.CanRun);
            Assert.False(result.Disposed);
            Assert.True(await workspace.RequestCloseAsync(_ => { prompts++; return Task.FromResult(true); }));
            Assert.True(workspace.IsClosing);
            Assert.False(document.CanRun);
            workspace.CancelClosing();
            Assert.True(document.CanRun);
            Assert.True(document.IsDirty);
            Assert.Equal("SELECT dirty", document.Text);
            Assert.False(result.Disposed);
            Assert.Equal(2, prompts);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AbandonedClosePromptCannotConfirmANewerCloseGenerationOrDiscardANewDraft(bool oldDecision)
        {
            var sources = new Sources();
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var first = workspace.NewDocument();
            first.Text = "first draft";
            var oldPrompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldClose = workspace.RequestCloseAsync(_ => oldPrompt.Task);
            workspace.CancelClosing();
            var second = workspace.NewDocument();
            second.Text = "new draft";
            var newClose = workspace.RequestCloseAsync(_ => Task.FromResult(true));
            Assert.True(await newClose);
            oldPrompt.TrySetResult(oldDecision);
            Assert.False(await oldClose);
            Assert.True(workspace.IsClosing);
            Assert.Equal("new draft", second.Text);
            Assert.Equal(2, workspace.Documents.Count);
            workspace.CancelClosing();
        }

        [Fact]
        public async Task SqlFilesUseExplicitPersistenceAndKeepEditsMadeDuringSaveOrRead()
        {
            using var directory = new DirectoryFixture();
            var path = Path.Combine(directory.Path, "draft.sql");
            var sources = new Sources();
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            var document = workspace.NewDocument();
            document.Text = "SELECT 'λ😀';\r\n";
            var original = document.Text;
            var saving = true;
            document.PropertyChanged += (_, e) =>
            {
                if (saving && e.PropertyName == nameof(document.MessageText) &&
                    document.MessageText == Localization.GetLocalizedString("SQL_FILE_SAVING"))
                {
                    saving = false;
                    document.Text = "newer unsaved text";
                }
            };
            Assert.True(await document.SaveFileAsync(path, false));
            Assert.Equal(Encoding.UTF8.GetBytes(original), await File.ReadAllBytesAsync(path));
            Assert.True(document.IsDirty);
            Assert.Equal("newer unsaved text", document.Text);
            var reading = true;
            document.PropertyChanged += (_, e) =>
            {
                if (reading && e.PropertyName == nameof(document.MessageText) &&
                    document.MessageText == Localization.GetLocalizedString("SQL_FILE_LOADING"))
                {
                    reading = false;
                    document.Text = "edited while reading";
                }
            };
            Assert.False(await document.OpenFileAsync(path));
            Assert.Equal("edited while reading", document.Text);
            Assert.True(await document.OpenFileAsync(path));
            Assert.Equal(original, document.Text);
            Assert.False(document.IsDirty);
            Assert.Empty(sources.ExecutedSql);
            Assert.Single(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task DocumentCloseCancelKeepsResourcesAndConfirmedCloseDrainsAndReleasesExactlyOnce()
        {
            var result = new Result(1);
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT 1";
            Assert.True(await document.RunAsync());
            Assert.False(await workspace.CloseDocumentAsync(document, _ => Task.FromResult(false)));
            Assert.False(result.Disposed);
            Assert.Contains(document, workspace.Documents);
            Assert.True(document.CanRun);
            Assert.True(await workspace.CloseDocumentAsync(document, _ => Task.FromResult(true)));
            Assert.Empty(workspace.Documents);
            Assert.Equal(1, result.DisposeCalls);
            await document.DisposeAsync();
            Assert.Equal(1, result.DisposeCalls);
        }

        [Fact]
        public async Task InspectorReadsBoundedExplicitChunksAndDecodesUtf8AcrossTheirBoundaries()
        {
            var full = Encoding.UTF8.GetBytes("😀λtext");
            var result = new Result(1)
            {
                Columns = [new(0, "text", "TEXT", null, null)],
                Cells = _ => [new(QueryValueKind.Text, "", full.Length, true)],
                ChunkSize = 1
            };
            result.FullValues[(0, 0)] = full;
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT value";
            Assert.True(await document.RunAsync());
            Assert.Equal(0, result.ValueReads);
            document.SelectInspectorRow(document.PageRows[0]);
            var cell = document.InspectorRow.Cells[0];
            Assert.True(await document.InspectValueAsync(cell, true));
            var decoded = document.InspectorText;
            while (document.InspectorHasMore)
            {
                Assert.True(await document.InspectValueAsync(cell, false));
                decoded += document.InspectorText;
                Assert.True(document.InspectorText.Length <= 2);
            }
            Assert.Equal("😀λtext", decoded);
            Assert.Equal(full.Length, result.ValueReads);
        }

        [Fact]
        public async Task InspectorNeverPublishesAStaleCellChunkUnderANewerFieldSelection()
        {
            var gate = new TaskCompletionSource<QueryValueChunk>(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = new Result(1)
            {
                Columns = [new(0, "first", "TEXT", null, null), new(1, "second", "TEXT", null, null)],
                Cells = _ => [new(QueryValueKind.Text, "first", 5, false), new(QueryValueKind.Text, "second", 6, false)],
                ValueReader = (_, _, _, _, _) => gate.Task
            };
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT fields";
            Assert.True(await document.RunAsync());
            document.SelectInspectorRow(document.PageRows[0]);
            var cells = document.InspectorRow.Cells;
            var inspect = document.InspectValueAsync(cells[0], true);
            document.SelectInspectorCell(cells[1]);
            gate.TrySetResult(new(QueryValueKind.Text, 0, 5, Encoding.UTF8.GetBytes("first"), false));
            await inspect;
            Assert.Empty(document.InspectorText);
            Assert.Contains("second", document.InspectorChunkInfo);
            result.ValueReader = null;
            Assert.True(await document.InspectValueAsync(cells[1], true));
            Assert.Equal("second", document.InspectorText);
        }

        [Fact]
        public async Task BudgetLimitedDalPagesAreAccumulatedWithoutMistakingThemForTruncatedResults()
        {
            var result = new Result(1003);
            result.PageReader = (offset, rows, _, _) => Task.FromResult(result.Page(offset, Math.Min(rows, 7)));
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT many";
            Assert.True(await document.RunAsync());
            Assert.Equal(1000, document.PageRows.Count);
            Assert.Equal(1003, document.RowCount);
            Assert.True(await document.MovePageAsync(true));
            Assert.Equal(new long[] { 1000, 1001, 1002 }, document.PageRows.Select(row => row.Ordinal).ToArray());
            Assert.False(document.CanNextPage);
        }

        [Fact]
        public async Task DisplayPayloadBoundCanShortenAPageWithoutLosingAnyStoredRows()
        {
            var preview = new string('x', 4096);
            var result = new Result(200)
            {
                Columns = Enumerable.Range(0, 100).Select(index => new QueryColumn(index, "field" + index, "TEXT", null, null)).ToArray(),
                Cells = _ => Enumerable.Range(0, 100).Select(_ => new QueryCell(QueryValueKind.Text, preview, 65536, true)).ToArray()
            };
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT wide";
            Assert.True(await document.RunAsync());
            Assert.InRange(document.PageRows.Count, 1, 199);
            var ordinals = document.PageRows.Select(row => row.Ordinal).ToList();
            while (document.CanNextPage)
            {
                Assert.True(await document.MovePageAsync(true));
                Assert.All(document.PageRows.SelectMany(row => row.Cells), cell => Assert.True(cell.Preview.Length <= 257));
                ordinals.AddRange(document.PageRows.Select(row => row.Ordinal));
            }
            Assert.Equal(Enumerable.Range(0, 200).Select(index => (long)index), ordinals);
            Assert.Equal(200, document.RowCount);
        }

        [Fact]
        public async Task CancellingCloseRefreshesSourceChangesThatArrivedWhileTheWorkspaceWasSuspended()
        {
            var sources = new Sources();
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.SelectedSource = workspace.Sources.Single(source => source.Kind == QuerySourceKind.Loaded);
            document.Text = "SELECT 1";
            Assert.True(await workspace.RequestCloseAsync(_ => Task.FromResult(true)));
            sources.Items = [new(sources.ActiveId, QuerySourceKind.Active, "active")];
            await workspace.RefreshSourcesAsync();
            Assert.True(document.SelectedSource.IsAvailable);
            workspace.CancelClosing();
            Assert.False(document.SelectedSource.IsAvailable);
            Assert.False(document.CanRun);
            Assert.True(document.IsDirty);
        }
    }
}
