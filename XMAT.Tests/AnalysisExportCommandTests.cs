// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.AccessControl;
using System.Text;
using System.Windows.Data;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.Models;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;

namespace XMAT.Tests
{
    public class AnalysisExportCommandTests
    {
        [Fact]
        public void CanExecute_UsesCurrentCompletedXboxRunAndDoesNotProbeFileSystem()
        {
            var runs = new ObservableCollection<ICaptureAnalysisRun>();
            AnalysisRunModel validRun = CreateRun(
                XboxLiveCaptureAnalyzer.Analyzer,
                CreateCompleteResults(
                    @"Z:\source-that-does-not-exist",
                    "Selected capture"));
            AnalysisRunModel networkRun = CreateRun(
                XMAT.NetworkTraceCaptureAnalysis.NetworkTraceCaptureAnalyzer.Analyzer,
                CreateCompleteResults(
                    @"Z:\network-source-that-does-not-exist",
                    "Network capture"));
            runs.Add(validRun);
            runs.Add(networkRun);
            ICollectionView view = CollectionViewSource.GetDefaultView(runs);
            Assert.True(view.MoveCurrentTo(validRun));

            Assert.True(AnalysisReportExportCommand.CanExecute(runs));

            Assert.True(view.MoveCurrentTo(networkRun));
            Assert.False(AnalysisReportExportCommand.CanExecute(runs));

            Assert.True(view.MoveCurrentTo(validRun));
            ((AnalysisResultsModel)validRun.AnalysisData).ArtifactManifest =
                Array.Empty<AnalysisArtifact>();
            Assert.False(AnalysisReportExportCommand.CanExecute(runs));

            ((AnalysisResultsModel)validRun.AnalysisData).ArtifactManifest =
                new[] { CreateArtifact("index.html", "index") };
            ((AnalysisResultsModel)validRun.AnalysisData).ActiveProcessStep =
                AnalysisResultsModel.ProcessStep.RunningValidationRules;
            Assert.False(AnalysisReportExportCommand.CanExecute(runs));

            view.MoveCurrentToPosition(-1);
            Assert.False(AnalysisReportExportCommand.CanExecute(runs));
        }

        [Fact]
        public void SnapshotSelected_RemainsBoundToRunSelectedBeforeDialogs()
        {
            var runs = new ObservableCollection<ICaptureAnalysisRun>();
            AnalysisResultsModel firstResults = CreateCompleteResults(
                @"C:\working\first",
                "First capture");
            AnalysisResultsModel secondResults = CreateCompleteResults(
                @"C:\working\second",
                "Second capture");
            AnalysisRunModel firstRun = CreateRun(
                XboxLiveCaptureAnalyzer.Analyzer,
                firstResults);
            AnalysisRunModel secondRun = CreateRun(
                XboxLiveCaptureAnalyzer.Analyzer,
                secondResults);
            runs.Add(firstRun);
            runs.Add(secondRun);
            ICollectionView view = CollectionViewSource.GetDefaultView(runs);
            Assert.True(view.MoveCurrentTo(firstRun));

            AnalysisExportSource source =
                AnalysisReportExportCommand.SnapshotSelected(runs);
            Assert.NotNull(source);

            Assert.True(view.MoveCurrentTo(secondRun));
            firstResults.OutputDirectory = @"C:\working\mutated";
            firstResults.SourceCaptureName = "Mutated capture";

            Assert.Equal(@"C:\working\first", source.SourceDirectory);
            Assert.Equal("First capture", source.SourceCaptureName);
        }

        [Fact]
        public void CompletingResults_FinalizesMetadataBeforeCompleteNotification()
        {
            string root = AnalysisReportTestPaths.CreateOwnedDirectory(
                "xec");
            string outputDirectory = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDirectory);
            try
            {
                File.WriteAllText(
                    Path.Combine(outputDirectory, "index.html"),
                    "index");
                File.WriteAllText(
                    Path.Combine(outputDirectory, "calls.js"),
                    "calls");
                var results = new AnalysisResultsModel
                {
                    OutputDirectory = outputDirectory,
                    SourceCaptureName = "Metadata capture"
                };
                bool observedComplete = false;
                results.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(results.IsComplete) &&
                        results.IsComplete)
                    {
                        observedComplete = true;
                        Assert.NotEqual(default, results.CompletedAt);
                        Assert.Equal(
                            DateTimeKind.Local,
                            results.CompletedAt.Kind);
                        Assert.Equal(
                            new[] { "calls.js", "index.html" },
                            results.ArtifactManifest
                                .Select(artifact => artifact.RelativePath)
                                .OrderBy(path => path, StringComparer.Ordinal));
                    }
                };

                results.ActiveProcessStep =
                    AnalysisResultsModel.ProcessStep.DumpingRuleResults;
                Assert.Equal(default, results.CompletedAt);
                Assert.Empty(results.ArtifactManifest);
                results.ActiveProcessStep =
                    AnalysisResultsModel.ProcessStep.Complete;

                Assert.True(observedComplete);
                File.WriteAllText(
                    Path.Combine(outputDirectory, "later.txt"),
                    "created after completion");
                Assert.Equal(2, results.ArtifactManifest.Count);
            }
            finally
            {
                AnalysisReportTestPaths.DeleteOwnedDirectory(root);
            }
        }

        [Fact]
        public void CompletingResults_MetadataIoFailurePreservesSuccessfulAnalysis()
        {
            string root = AnalysisReportTestPaths.CreateOwnedDirectory(
                "xmf");
            string outputDirectory = Path.Combine(root, "locked");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(
                Path.Combine(outputDirectory, "index.html"),
                "index");
            var report = new TestReportViewModel();
            var results = new AnalysisResultsModel
            {
                OutputDirectory = outputDirectory,
                SourceCaptureName = "Metadata failure"
            };
            results.Reports.Add(report);

            try
            {
                Exception exception;
                using (new DirectoryAccessDenyScope(
                    outputDirectory,
                    FileSystemRights.ListDirectory))
                {
                    exception = Record.Exception(() =>
                        results.ActiveProcessStep =
                            AnalysisResultsModel.ProcessStep.Complete);
                }

                Assert.Null(exception);
                Assert.True(results.IsComplete);
                Assert.Empty(results.ArtifactManifest);
                Assert.Same(report, Assert.Single(results.Reports));
            }
            finally
            {
                AnalysisReportTestPaths.DeleteOwnedDirectory(root);
            }
        }

        private static AnalysisRunModel CreateRun(
            ICaptureAnalyzer analyzer,
            AnalysisResultsModel results)
        {
            return new AnalysisRunModel(analyzer, DateTime.UtcNow.Ticks)
            {
                AnalysisData = results
            };
        }

        private static AnalysisResultsModel CreateCompleteResults(
            string outputDirectory,
            string sourceCaptureName)
        {
            return new AnalysisResultsModel
            {
                ActiveProcessStep = AnalysisResultsModel.ProcessStep.Complete,
                OutputDirectory = outputDirectory,
                SourceCaptureName = sourceCaptureName,
                CompletedAt = new DateTime(
                    2026,
                    10,
                    26,
                    14,
                    23,
                    21,
                    DateTimeKind.Local),
                ArtifactManifest = new[]
                {
                    CreateArtifact("index.html", "index")
                }
            };
        }

        private static AnalysisArtifact CreateArtifact(
            string relativePath,
            string contents)
        {
            return new AnalysisArtifact(
                relativePath,
                Encoding.UTF8.GetByteCount(contents));
        }

        private sealed class TestReportViewModel : ReportViewModel
        {
            public override string ReportName => "Test report";

            internal TestReportViewModel() :
                base(null, "Test capture", null)
            {
            }
        }
    }
}
