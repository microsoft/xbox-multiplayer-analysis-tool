// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace XMAT.Tests
{
    public class AnalysisReportExporterTests
    {
        private static readonly DateTime CompletionTime =
            new(2026, 10, 26, 14, 23, 21, DateTimeKind.Local);

        [Fact]
        public void Export_CopiesCompleteManifestByteForByteWithoutChangingSource()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Capture #1",
                CompletionTime);
            var sourceSnapshot = SnapshotFiles(sourceDirectory);

            string publishedPath = new AnalysisReportExporter().Export(
                source,
                destinationParent);

            Assert.Equal(
                Path.Combine(
                    destinationParent,
                    AnalysisReportExporter.CreateFolderName(
                        source.SourceCaptureName,
                        source.CompletedAt)),
                publishedPath);
            AssertManifestMatches(source, publishedPath);
            AssertFilesUnchanged(sourceDirectory, sourceSnapshot);
        }

        [Fact]
        public void ExportedIndexReferencesFilesPresentInPublishedFolder()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("resource-source");
            string destinationParent = fixture.CreateDirectory("destination");
            byte[] indexBytes = ReadXltaResource("index.html");
            string indexText = Encoding.UTF8.GetString(indexBytes);
            var files = new Dictionary<string, byte[]>
            {
                ["index.html"] = indexBytes
            };

            foreach (Match match in Regex.Matches(
                indexText,
                "(?:href|src)=\"(?<path>[^\"]+)\"",
                RegexOptions.CultureInvariant))
            {
                string relativePath = match.Groups["path"].Value;
                Assert.False(Path.IsPathRooted(relativePath));
                Assert.DoesNotContain("://", relativePath, StringComparison.Ordinal);
                files[relativePath] = ReadXltaResource(relativePath);
            }

            AnalysisExportSource source = WriteSource(
                sourceDirectory,
                "Resource-backed report",
                CompletionTime,
                files);

            string publishedPath = new AnalysisReportExporter().Export(
                source,
                destinationParent);

            string publishedIndex = File.ReadAllText(
                Path.Combine(publishedPath, "index.html"));
            foreach (Match match in Regex.Matches(
                publishedIndex,
                "(?:href|src)=\"(?<path>[^\"]+)\"",
                RegexOptions.CultureInvariant))
            {
                Assert.True(
                    File.Exists(Path.Combine(
                        publishedPath,
                        match.Groups["path"].Value)),
                    $"Missing index.html dependency '{match.Groups["path"].Value}'.");
            }
        }

        [Fact]
        public void CreateFolderName_SanitizesAndCapsCaptureButPreservesLegalCharacters()
        {
            const string prefix = "XMAT Analysis - ";
            string suffix = $" - {CompletionTime:yyyy-MM-dd HHmmss}";
            string captureName =
                "Cap:ture?#%雪" + new string('a', 70) + ". ";

            string folderName = AnalysisReportExporter.CreateFolderName(
                captureName,
                CompletionTime);

            Assert.StartsWith(prefix, folderName, StringComparison.Ordinal);
            Assert.EndsWith(suffix, folderName, StringComparison.Ordinal);
            string captureComponent = folderName.Substring(
                prefix.Length,
                folderName.Length - prefix.Length - suffix.Length);
            Assert.Equal(60, captureComponent.Length);
            Assert.StartsWith(
                "Cap_ture_#%雪",
                captureComponent,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                captureComponent,
                character => Path.GetInvalidFileNameChars().Contains(character));
            Assert.False(
                captureComponent.EndsWith(".", StringComparison.Ordinal) ||
                captureComponent.EndsWith(" ", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ...   ")]
        public void CreateFolderName_UsesCaptureFallback(string captureName)
        {
            Assert.Equal(
                $"XMAT Analysis - Capture - {CompletionTime:yyyy-MM-dd HHmmss}",
                AnalysisReportExporter.CreateFolderName(
                    captureName,
                    CompletionTime));
        }

        [Theory]
        [InlineData("th-TH")]
        [InlineData("ar-SA")]
        public void CreateFolderName_UsesInvariantGregorianTimestamp(
            string cultureName)
        {
            CultureInfo originalCulture =
                CultureInfo.CurrentCulture;
            CultureInfo originalUiCulture =
                CultureInfo.CurrentUICulture;
            try
            {
                var culture = new CultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                Assert.Equal(
                    "XMAT Analysis - Capture - 2026-10-26 142321",
                    AnalysisReportExporter.CreateFolderName(
                        "Capture",
                        CompletionTime));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        }

        [Fact]
        public void CreateFolderName_DoesNotSplitSurrogatePairAtCaptureLimit()
        {
            string captureName =
                new string('a', 59) + "😀suffix";

            string folderName = AnalysisReportExporter.CreateFolderName(
                captureName,
                CompletionTime);

            Assert.Equal(
                $"XMAT Analysis - {new string('a', 59)} - " +
                    "2026-10-26 142321",
                folderName);
        }

        [Fact]
        public void Export_PreservesHashPercentAndUnicodeInPublishedPath()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory(
                "destination with spaces # % 雪");
            AnalysisExportSource source = WriteSource(
                sourceDirectory,
                "Team #50% 雪",
                CompletionTime,
                new Dictionary<string, byte[]>
                {
                    ["index.html"] = Encoding.UTF8.GetBytes("index")
                });

            string publishedPath = new AnalysisReportExporter().Export(
                source,
                destinationParent);

            Assert.Equal(
                Path.Combine(
                    destinationParent,
                    $"XMAT Analysis - Team #50% 雪 - " +
                        $"{CompletionTime:yyyy-MM-dd HHmmss}"),
                publishedPath);
            Assert.True(File.Exists(Path.Combine(
                publishedPath,
                "index.html")));
        }

        [Fact]
        public void Export_UsesCollisionSuffixWithoutMergingOrOverwriting()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Collision",
                CompletionTime);
            var exporter = new AnalysisReportExporter();

            string firstPath = exporter.Export(
                source,
                destinationParent);
            File.WriteAllText(
                Path.Combine(firstPath, "recipient-note.txt"),
                "preserve");
            string secondPath = exporter.Export(
                source,
                destinationParent);

            Assert.Equal(firstPath + " (2)", secondPath);
            Assert.Equal(
                "preserve",
                File.ReadAllText(Path.Combine(firstPath, "recipient-note.txt")));
            Assert.False(
                File.Exists(Path.Combine(secondPath, "recipient-note.txt")));
            AssertManifestMatches(source, secondPath);
        }

        [Fact]
        public void Export_NameCreationRaceAdvancesToNextSuffix()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Race",
                CompletionTime);
            var fileSystem = new FaultInjectingFileSystem();
            fileSystem.BeforeMove = (stagingPath, destinationPath, call) =>
            {
                if (call == 1)
                {
                    Directory.CreateDirectory(destinationPath);
                    throw new IOException("Injected destination creation race.");
                }
            };
            var exporter = CreateExporter(fileSystem);

            string publishedPath = exporter.Export(
                source,
                destinationParent);

            string basePath = Path.Combine(
                destinationParent,
                AnalysisReportExporter.CreateFolderName(
                    source.SourceCaptureName,
                    source.CompletedAt));
            Assert.Equal(basePath + " (2)", publishedPath);
            Assert.True(Directory.Exists(basePath));
            AssertManifestMatches(source, publishedPath);
            AssertNoStagingDirectories(destinationParent);
        }

        [Fact]
        public void Export_StagingCreationRaceDoesNotDeleteUnownedDirectory()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Staging race",
                CompletionTime);
            string racedDirectory = null;
            var fileSystem = new FaultInjectingFileSystem();
            fileSystem.BeforeCreateSingleDirectory = (path, call) =>
            {
                if (call == 1)
                {
                    racedDirectory = path;
                    Directory.CreateDirectory(path);
                    File.WriteAllText(
                        Path.Combine(path, "other-owner.txt"),
                        "preserve");
                }
            };

            string publishedPath = CreateExporter(fileSystem).Export(
                source,
                destinationParent);

            Assert.NotNull(racedDirectory);
            Assert.True(Directory.Exists(racedDirectory));
            Assert.Equal(
                "preserve",
                File.ReadAllText(Path.Combine(
                    racedDirectory,
                    "other-owner.txt")));
            AssertManifestMatches(source, publishedPath);
            Directory.Delete(racedDirectory, true);
            AssertNoStagingDirectories(destinationParent);
        }

        [Fact]
        public void Export_MissingDestinationParentIsNotRecreated()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = Path.Combine(
                fixture.Root,
                "missing-parent");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Missing parent",
                CompletionTime);

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                new AnalysisReportExporter().Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.DestinationMissing,
                exception.FailureKind);
            Assert.False(Directory.Exists(destinationParent));
        }

        [Fact]
        public void Export_ParentDeletedBeforeStagingIsNotRecreated()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Deleted parent",
                CompletionTime);
            var fileSystem = new FaultInjectingFileSystem();
            fileSystem.BeforeCreateSingleDirectory = (path, call) =>
            {
                if (call == 1)
                {
                    Directory.Delete(destinationParent);
                }
            };

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                CreateExporter(fileSystem).Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.DestinationMissing,
                exception.FailureKind);
            Assert.False(Directory.Exists(destinationParent));
        }

        [Fact]
        public void PhysicalFileSystem_CreateSingleDirectoryDoesNotRecreateParent()
        {
            using var fixture = new TestDirectory();
            string parent = fixture.CreateDirectory("deleted");
            string child = Path.Combine(parent, "child");
            Directory.Delete(parent);

            Assert.Throws<DirectoryNotFoundException>(() =>
                new PhysicalAnalysisReportExportFileSystem()
                    .CreateSingleDirectory(child));
            Assert.False(Directory.Exists(parent));
        }

        [Fact]
        public void Export_DeniedDestinationLeavesNoStagingResidue()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("denied");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Denied",
                CompletionTime);
            AnalysisReportExportException exception = null;

            using (new DirectoryAccessDenyScope(
                destinationParent,
                FileSystemRights.CreateDirectories |
                    FileSystemRights.CreateFiles,
                InheritanceFlags.ContainerInherit |
                    InheritanceFlags.ObjectInherit))
            {
                exception = Assert.Throws<AnalysisReportExportException>(() =>
                    new AnalysisReportExporter().Export(
                        source,
                        destinationParent));
            }

            Assert.Equal(
                AnalysisReportExportFailureKind.AccessDenied,
                exception.FailureKind);
            Assert.Empty(Directory.EnumerateDirectories(destinationParent));
        }

        [Fact]
        public void Export_MidCopyFailureCleansStagingAndRetrySucceeds()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Copy fault",
                CompletionTime);
            var sourceSnapshot = SnapshotFiles(sourceDirectory);
            var fileSystem = new FaultInjectingFileSystem();
            fileSystem.BeforeCopy = (sourcePath, destinationPath, call) =>
            {
                if (call == 2)
                {
                    throw new IOException("Injected copy failure.");
                }
            };

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                CreateExporter(fileSystem).Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.CopyFailed,
                exception.FailureKind);
            Assert.Equal(
                AnalysisReportPublicationState.NotPublished,
                exception.PublicationState);
            Assert.Null(exception.ResiduePath);
            AssertNoStagingDirectories(destinationParent);
            Assert.Empty(Directory.EnumerateDirectories(destinationParent));
            AssertFilesUnchanged(sourceDirectory, sourceSnapshot);

            string publishedPath = CreateExporter(fileSystem).Export(
                source,
                destinationParent);
            AssertManifestMatches(source, publishedPath);
            AssertNoStagingDirectories(destinationParent);
        }

        [Fact]
        public void Export_SourceChangedAfterCompletionFailsManifestValidation()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Changed source",
                CompletionTime);
            File.AppendAllText(
                Path.Combine(sourceDirectory, "index.html"),
                "changed after completion");

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                new AnalysisReportExporter().Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.ManifestMismatch,
                exception.FailureKind);
            Assert.Empty(Directory.EnumerateDirectories(destinationParent));
        }

        [Fact]
        public void Export_CorruptedStagingFileFailsValidationBeforePublish()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Corrupt staging",
                CompletionTime);
            var fileSystem = new FaultInjectingFileSystem();
            fileSystem.AfterCopy = (sourcePath, destinationPath, call) =>
            {
                if (call == 1)
                {
                    File.AppendAllText(
                        destinationPath,
                        "injected corruption");
                }
            };

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                CreateExporter(fileSystem).Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.ManifestMismatch,
                exception.FailureKind);
            Assert.Equal(
                AnalysisReportPublicationState.NotPublished,
                exception.PublicationState);
            Assert.Null(exception.ResiduePath);
            AssertNoStagingDirectories(destinationParent);
            Assert.Empty(Directory.EnumerateDirectories(destinationParent));
        }

        [Fact]
        public void Export_WindowsAccessDeniedIOExceptionRetriesThenFailsAndCleansStaging()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Publish fault",
                CompletionTime);
            var fileSystem = new FaultInjectingFileSystem
            {
                BeforeMove = (stagingPath, destinationPath, call) =>
                    throw new IOException(
                        "Injected Windows access denied rename failure.",
                        unchecked((int)0x80070005))
            };
            var retries = new List<int>();
            var exporter = new AnalysisReportExporter(
                fileSystem,
                retryNumber => retries.Add(retryNumber));

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                exporter.Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.PublishFailed,
                exception.FailureKind);
            Assert.Equal(
                AnalysisReportPublicationState.NotPublished,
                exception.PublicationState);
            Assert.Equal(4, fileSystem.MoveCallCount);
            Assert.Equal(new[] { 1, 2, 3 }, retries);
            Assert.Null(exception.PublishedPath);
            Assert.Null(exception.ResiduePath);
            AssertNoStagingDirectories(destinationParent);
            Assert.Empty(Directory.EnumerateDirectories(destinationParent));
        }

        [Fact]
        public void Export_MoveThatThrowsAfterPublicationReportsUncertainPublication()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Uncertain",
                CompletionTime);
            var fileSystem = new FaultInjectingFileSystem();
            fileSystem.AfterMove = (stagingPath, destinationPath, call) =>
            {
                if (call == 1)
                {
                    throw new IOException(
                        "Injected failure after the rename completed.");
                }
            };

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                CreateExporter(fileSystem).Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.PublishFailed,
                exception.FailureKind);
            Assert.Equal(
                AnalysisReportPublicationState.Unknown,
                exception.PublicationState);
            Assert.NotNull(exception.PublishedPath);
            Assert.True(Directory.Exists(exception.PublishedPath));
            Assert.Null(exception.ResiduePath);
            AssertNoStagingDirectories(destinationParent);
        }

        [Fact]
        public void Export_CleanupFailureReportsOwnedResidue()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Cleanup fault",
                CompletionTime);
            var fileSystem = new FaultInjectingFileSystem
            {
                BeforeCopy = (sourcePath, destinationPath, call) =>
                    throw new IOException("Injected copy failure."),
                BeforeDelete = (path, recursive, call) =>
                    throw new IOException("Injected cleanup failure.")
            };

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                CreateExporter(fileSystem).Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.CopyFailed,
                exception.FailureKind);
            Assert.NotNull(exception.ResiduePath);
            Assert.True(Directory.Exists(exception.ResiduePath));
            Assert.StartsWith(
                destinationParent,
                exception.ResiduePath,
                StringComparison.OrdinalIgnoreCase);
            Directory.Delete(exception.ResiduePath, true);
        }

        [Fact]
        public void Export_PermissionObscuredCleanupFailurePreservesOriginalFailureAndReportsResidue()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Permission-obscured cleanup",
                CompletionTime);
            string stagingPath = null;
            var fileSystem = new FaultInjectingFileSystem
            {
                BeforeCreateSingleDirectory = (path, call) =>
                    stagingPath = path,
                DirectoryExistsOverride = path =>
                    stagingPath != null &&
                    string.Equals(
                        path,
                        stagingPath,
                        StringComparison.OrdinalIgnoreCase)
                        ? false
                        : null,
                BeforeCopy = (sourcePath, destinationPath, call) =>
                    throw new IOException("Injected copy failure."),
                BeforeDelete = (path, recursive, call) =>
                    throw new IOException("Injected cleanup failure.")
            };

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                CreateExporter(fileSystem).Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.CopyFailed,
                exception.FailureKind);
            Assert.Equal(
                "Injected copy failure.",
                exception.InnerException?.Message);
            Assert.Equal(stagingPath, exception.ResiduePath);
            Assert.Equal(1, fileSystem.DeleteCallCount);
            Directory.Delete(stagingPath, true);
        }

        [Fact]
        public void Export_TransientRenameFailuresRetryThenPublish()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Retry success",
                CompletionTime);
            var fileSystem = new FaultInjectingFileSystem();
            fileSystem.BeforeMove = (stagingPath, destinationPath, call) =>
            {
                if (call <= 2)
                {
                    throw new IOException(
                        "Injected transient rename failure.");
                }
            };
            var retries = new List<int>();
            var exporter = new AnalysisReportExporter(
                fileSystem,
                retryNumber => retries.Add(retryNumber));

            string publishedPath = exporter.Export(
                source,
                destinationParent);

            Assert.Equal(3, fileSystem.MoveCallCount);
            Assert.Equal(new[] { 1, 2 }, retries);
            AssertManifestMatches(source, publishedPath);
            AssertNoStagingDirectories(destinationParent);
        }

        [Fact]
        public void Export_RefusesArtifactPathAt260Characters()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            string folderName = AnalysisReportExporter.CreateFolderName(
                "Path guard",
                CompletionTime);
            string finalRoot = Path.Combine(destinationParent, folderName);
            int fileNameLength =
                AnalysisReportExporter.MaximumCompatiblePathLength -
                finalRoot.Length -
                1;
            Assert.InRange(fileNameLength, 1, 240);
            string relativePath = new string('p', fileNameLength);
            AnalysisExportSource source = WriteSource(
                sourceDirectory,
                "Path guard",
                CompletionTime,
                new Dictionary<string, byte[]>
                {
                    [relativePath] = Encoding.UTF8.GetBytes("path guard")
                });

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                new AnalysisReportExporter().Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.PathTooLong,
                exception.FailureKind);
            Assert.Contains(
                destinationParent,
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "shorter",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                ".xmat-",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Null(exception.ResiduePath);
            Assert.Empty(Directory.EnumerateDirectories(destinationParent));
        }

        [Fact]
        public void Export_RejectsDestinationInsideSource()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = Path.Combine(
                sourceDirectory,
                "exports");
            Directory.CreateDirectory(destinationParent);
            AnalysisExportSource source = CreateFullBundle(
                sourceDirectory,
                "Recursive",
                CompletionTime);

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                new AnalysisReportExporter().Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.InvalidDestination,
                exception.FailureKind);
            Assert.Empty(Directory.EnumerateDirectories(destinationParent));
        }

        [Fact]
        public void Export_MissingManifestArtifactFailsBeforeStaging()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            var missingArtifact = new AnalysisArtifact(
                "missing.json",
                10);
            var source = new AnalysisExportSource(
                sourceDirectory,
                "Missing artifact",
                CompletionTime,
                new[] { missingArtifact });

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                new AnalysisReportExporter().Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.ManifestMismatch,
                exception.FailureKind);
            Assert.Empty(Directory.EnumerateDirectories(destinationParent));
        }

        [Fact]
        public void Export_MissingSourceDirectoryFailsBeforeStaging()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = Path.Combine(
                fixture.Root,
                "missing-source");
            string destinationParent = fixture.CreateDirectory("destination");
            var source = new AnalysisExportSource(
                sourceDirectory,
                "Missing source",
                CompletionTime,
                new[]
                {
                    new AnalysisArtifact(
                        "index.html",
                        5)
                });

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                new AnalysisReportExporter().Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.SourceMissing,
                exception.FailureKind);
            Assert.Empty(Directory.EnumerateDirectories(destinationParent));
        }

        [Theory]
        [InlineData(@"..\outside.txt")]
        [InlineData(@"C:\rooted.txt")]
        [InlineData("index.html:secret")]
        [InlineData(@".\index.html")]
        [InlineData("index.html.")]
        [InlineData(@"nested\index.html")]
        public void Export_RejectsNonLeafArtifactBeforeStaging(
            string relativePath)
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            var fileSystem = new FaultInjectingFileSystem();
            var source = new AnalysisExportSource(
                sourceDirectory,
                "Invalid artifact",
                CompletionTime,
                new[]
                {
                    new AnalysisArtifact(
                        relativePath,
                        1)
                });

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                CreateExporter(fileSystem).Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.ManifestMismatch,
                exception.FailureKind);
            Assert.Equal(0, fileSystem.CreateSingleDirectoryCallCount);
        }

        [Fact]
        public void Export_RejectsCaseInsensitiveDuplicateLeafNamesBeforeStaging()
        {
            using var fixture = new TestDirectory();
            string sourceDirectory = fixture.CreateDirectory("source");
            string destinationParent = fixture.CreateDirectory("destination");
            byte[] contents = Encoding.UTF8.GetBytes("index");
            File.WriteAllBytes(
                Path.Combine(sourceDirectory, "index.html"),
                contents);
            AnalysisArtifact first = CreateArtifact(
                "index.html",
                "index");
            AnalysisArtifact duplicate = CreateArtifact(
                "INDEX.HTML",
                "index");
            var fileSystem = new FaultInjectingFileSystem();
            var source = new AnalysisExportSource(
                sourceDirectory,
                "Duplicate",
                CompletionTime,
                new[] { first, duplicate });

            var exception = Assert.Throws<AnalysisReportExportException>(() =>
                CreateExporter(fileSystem).Export(
                    source,
                    destinationParent));

            Assert.Equal(
                AnalysisReportExportFailureKind.ManifestMismatch,
                exception.FailureKind);
            Assert.Equal(0, fileSystem.CreateSingleDirectoryCallCount);
        }

        [Fact]
        public void FixtureRootResolver_CoversEveryResolutionLayer()
        {
            using var fixture = new TestDirectory();
            string callerRoot = CreateRepositoryRoot(
                fixture,
                "caller-root");
            string callerDirectory = Path.Combine(
                callerRoot,
                "XMAT.Tests");
            Directory.CreateDirectory(callerDirectory);
            AssertResolution(
                callerRoot,
                Path.Combine(callerDirectory, "FixtureSource.cs"),
                @"Z:\external-output",
                null,
                null,
                "CallerFilePath");

            string outputRoot = CreateRepositoryRoot(
                fixture,
                "output-root");
            string baseDirectory = Path.Combine(
                outputRoot,
                "artifacts",
                "bin");
            Directory.CreateDirectory(baseDirectory);
            AssertResolution(
                outputRoot,
                @"/_/XMAT.Tests/FixtureSource.cs",
                baseDirectory,
                null,
                null,
                "AppContext.BaseDirectory");

            string overrideRoot = CreateRepositoryRoot(
                fixture,
                "override-root");
            AssertResolution(
                overrideRoot,
                @"/_/XMAT.Tests/FixtureSource.cs",
                @"Z:\external-output",
                overrideRoot,
                null,
                "XMAT_TEST_CHECKOUT_ROOT");

            string workingRoot = CreateRepositoryRoot(
                fixture,
                "working-root");
            AssertResolution(
                workingRoot,
                @"/_/XMAT.Tests/FixtureSource.cs",
                @"Z:\external-output",
                fixture.CreateDirectory("invalid-override"),
                workingRoot,
                "Environment.CurrentDirectory");

            var exception = Assert.Throws<DirectoryNotFoundException>(() =>
                AnalysisReportFixtureRootResolver.Resolve(
                    @"/_/XMAT.Tests/FixtureSource.cs",
                    @"Z:\unresolved-output",
                    fixture.CreateDirectory("unresolved-override"),
                    null,
                    out _));
            Assert.Contains(
                "XboxMultiplayerAnalysisTool.sln",
                exception.Message,
                StringComparison.Ordinal);
        }

        private static string CreateRepositoryRoot(
            TestDirectory fixture,
            string name)
        {
            string root = fixture.CreateDirectory(name);
            File.WriteAllText(
                Path.Combine(
                    root,
                    "XboxMultiplayerAnalysisTool.sln"),
                string.Empty);
            return root;
        }

        private static void AssertResolution(
            string expectedRoot,
            string sourcePath,
            string baseDirectory,
            string checkoutOverride,
            string workingDirectory,
            string expectedResolution)
        {
            Assert.Equal(
                expectedRoot,
                AnalysisReportFixtureRootResolver.Resolve(
                    sourcePath,
                    baseDirectory,
                    checkoutOverride,
                    workingDirectory,
                    out string resolution));
            Assert.Equal(expectedResolution, resolution);
        }

        private static AnalysisReportExporter CreateExporter(
            IAnalysisReportExportFileSystem fileSystem)
        {
            return new AnalysisReportExporter(
                fileSystem,
                _ => { });
        }

        private static AnalysisExportSource CreateFullBundle(
            string sourceDirectory,
            string sourceCaptureName,
            DateTime completedAt)
        {
            string[] fileNames =
            {
                "PerEndpointReport.json",
                "CallReport.json",
                "StatsReport.json",
                "xlca_42_service_call_data.txt",
                "xlca_42_service_call_items.txt",
                "xlca_42_results.txt",
                "index.html",
                "bootstrap.css",
                "site.css",
                "report.css",
                "jquery.js",
                "bootstrap.min.js",
                "report_view.js",
                "stats.js",
                "calls.js"
            };
            return WriteSource(
                sourceDirectory,
                sourceCaptureName,
                completedAt,
                fileNames.ToDictionary(
                    fileName => fileName,
                    fileName => Encoding.UTF8.GetBytes(
                        $"{fileName}:{Guid.NewGuid():N}"),
                    StringComparer.Ordinal));
        }

        private static AnalysisExportSource WriteSource(
            string sourceDirectory,
            string sourceCaptureName,
            DateTime completedAt,
            IReadOnlyDictionary<string, byte[]> files)
        {
            var artifacts = new List<AnalysisArtifact>();
            foreach (var file in files)
            {
                string fullPath = Path.Combine(
                    sourceDirectory,
                    file.Key);
                string parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                File.WriteAllBytes(fullPath, file.Value);
                File.SetLastWriteTimeUtc(
                    fullPath,
                    new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
                artifacts.Add(new AnalysisArtifact(
                    file.Key,
                    file.Value.LongLength));
            }

            return new AnalysisExportSource(
                sourceDirectory,
                sourceCaptureName,
                completedAt,
                artifacts);
        }

        private static AnalysisArtifact CreateArtifact(
            string relativePath,
            string contents)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(contents);
            return new AnalysisArtifact(
                relativePath,
                bytes.LongLength);
        }

        private static Dictionary<string, FileSnapshot> SnapshotFiles(
            string directory)
        {
            return Directory.EnumerateFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(directory, path),
                    path => new FileSnapshot(
                        File.ReadAllBytes(path),
                        File.GetLastWriteTimeUtc(path)),
                    StringComparer.Ordinal);
        }

        private static void AssertFilesUnchanged(
            string directory,
            IReadOnlyDictionary<string, FileSnapshot> snapshot)
        {
            Assert.Equal(
                snapshot.Keys.OrderBy(path => path, StringComparer.Ordinal),
                Directory.EnumerateFiles(
                        directory,
                        "*",
                        SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(directory, path))
                    .OrderBy(path => path, StringComparer.Ordinal));
            foreach (var file in snapshot)
            {
                string fullPath = Path.Combine(directory, file.Key);
                Assert.Equal(file.Value.Bytes, File.ReadAllBytes(fullPath));
                Assert.Equal(
                    file.Value.LastWriteTimeUtc,
                    File.GetLastWriteTimeUtc(fullPath));
            }
        }

        private static void AssertManifestMatches(
            AnalysisExportSource source,
            string publishedPath)
        {
            Assert.Equal(
                source.ArtifactManifest
                    .Select(artifact => artifact.RelativePath)
                    .OrderBy(path => path, StringComparer.Ordinal),
                Directory.EnumerateFiles(
                        publishedPath,
                        "*",
                        SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(publishedPath, path))
                    .OrderBy(path => path, StringComparer.Ordinal));
            foreach (AnalysisArtifact artifact in source.ArtifactManifest)
            {
                string sourcePath = Path.Combine(
                    source.SourceDirectory,
                    artifact.RelativePath);
                string destinationPath = Path.Combine(
                    publishedPath,
                    artifact.RelativePath);
                Assert.Equal(
                    File.ReadAllBytes(sourcePath),
                    File.ReadAllBytes(destinationPath));
            }
        }

        private static byte[] ReadXltaResource(string fileName)
        {
            var resourceManager = new ResourceManager(
                "XboxMultiplayerAnalysisTool.g",
                typeof(PublicUtilities).Assembly);
            try
            {
                using Stream stream = resourceManager.GetStream(
                    $"resources/html/xlta/{fileName}".ToLowerInvariant(),
                    CultureInfo.InvariantCulture);
                Assert.NotNull(stream);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            finally
            {
                resourceManager.ReleaseAllResources();
            }
        }

        private static void AssertNoStagingDirectories(string parent)
        {
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(parent),
                path => Path.GetFileName(path).StartsWith(
                    AnalysisReportExporter.StagingDirectoryPrefix,
                    StringComparison.Ordinal));
        }

        private sealed record FileSnapshot(
            byte[] Bytes,
            DateTime LastWriteTimeUtc);

        private sealed class TestDirectory : IDisposable
        {
            internal string Root { get; }

            internal TestDirectory()
            {
                Root = AnalysisReportTestPaths.CreateOwnedDirectory(
                    "xre");
            }

            internal string CreateDirectory(string name)
            {
                string path = Path.Combine(Root, name);
                Directory.CreateDirectory(path);
                return path;
            }

            public void Dispose()
            {
                AnalysisReportTestPaths.DeleteOwnedDirectory(Root);
            }
        }

        private sealed class FaultInjectingFileSystem :
            IAnalysisReportExportFileSystem
        {
            private readonly IAnalysisReportExportFileSystem _inner =
                new PhysicalAnalysisReportExportFileSystem();

            internal Action<string, int> BeforeCreateSingleDirectory { get; set; }
            internal Action<string, string, int> BeforeCopy { get; set; }
            internal Action<string, string, int> AfterCopy { get; set; }
            internal Action<string, string, int> BeforeMove { get; set; }
            internal Action<string, string, int> AfterMove { get; set; }
            internal Action<string, bool, int> BeforeDelete { get; set; }
            internal Func<string, bool?> DirectoryExistsOverride { get; set; }

            internal int CreateSingleDirectoryCallCount { get; private set; }
            internal int CopyCallCount { get; private set; }
            internal int MoveCallCount { get; private set; }
            internal int DeleteCallCount { get; private set; }

            public bool DirectoryExists(string path)
            {
                bool? result = DirectoryExistsOverride?.Invoke(path);
                if (result.HasValue)
                {
                    return result.Value;
                }
                return _inner.DirectoryExists(path);
            }

            public bool FileExists(string path)
            {
                return _inner.FileExists(path);
            }

            public long GetFileLength(string path)
            {
                return _inner.GetFileLength(path);
            }

            public string ComputeSha256(string path)
            {
                return _inner.ComputeSha256(path);
            }

            public void CreateSingleDirectory(string path)
            {
                CreateSingleDirectoryCallCount++;
                BeforeCreateSingleDirectory?.Invoke(
                    path,
                    CreateSingleDirectoryCallCount);
                _inner.CreateSingleDirectory(path);
            }

            public void CopyFile(
                string sourcePath,
                string destinationPath,
                bool overwrite)
            {
                CopyCallCount++;
                BeforeCopy?.Invoke(
                    sourcePath,
                    destinationPath,
                    CopyCallCount);
                _inner.CopyFile(
                    sourcePath,
                    destinationPath,
                    overwrite);
                AfterCopy?.Invoke(
                    sourcePath,
                    destinationPath,
                    CopyCallCount);
            }

            public void MoveDirectory(
                string sourcePath,
                string destinationPath)
            {
                MoveCallCount++;
                BeforeMove?.Invoke(
                    sourcePath,
                    destinationPath,
                    MoveCallCount);
                _inner.MoveDirectory(sourcePath, destinationPath);
                AfterMove?.Invoke(
                    sourcePath,
                    destinationPath,
                    MoveCallCount);
            }

            public void DeleteDirectory(string path, bool recursive)
            {
                DeleteCallCount++;
                BeforeDelete?.Invoke(path, recursive, DeleteCallCount);
                _inner.DeleteDirectory(path, recursive);
            }
        }

    }

    internal static class AnalysisReportTestPaths
    {
        internal const int FixturePathBudget = 120;
        // Optional checkout override for path-mapped builds.
        private const string CheckoutRootEnvironmentVariable =
            "XMAT_TEST_CHECKOUT_ROOT";

        internal static string CreateOwnedDirectory(string prefix)
        {
            string fixtureRoot = ResolveFixtureRoot(
                out string resolution);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string path = Path.Combine(
                    fixtureRoot,
                    $".{prefix}-{Guid.NewGuid():N}".Substring(
                        0,
                        prefix.Length + 10));
                Assert.True(
                    path.Length <= FixturePathBudget,
                    $"The repository path leaves insufficient room for " +
                    $"export fixtures ({path.Length} > " +
                    $"{FixturePathBudget}); root resolved via " +
                    $"{resolution}: {path}");
                if (Directory.Exists(path))
                {
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(path);
                    using (new FileStream(
                        Path.Combine(path, ".owner"),
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    {
                    }
                    return path;
                }
                catch (IOException) when (
                    File.Exists(Path.Combine(path, ".owner")))
                {
                }
            }

            throw new IOException(
                "Could not reserve a unique analysis report fixture.");
        }

        internal static void DeleteOwnedDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                NormalizeFileAttributes(path);
                Directory.Delete(path, true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RemoveCurrentUserDenyRules(path);
                NormalizeFileAttributes(path);
                Directory.Delete(path, true);
            }
        }

        private static string ResolveFixtureRoot(
            out string resolution)
        {
            return AnalysisReportFixtureRootResolver.Resolve(
                GetSourceFilePath(),
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable(
                    CheckoutRootEnvironmentVariable),
                Environment.CurrentDirectory,
                out resolution);
        }

        private static string GetSourceFilePath(
            [CallerFilePath] string sourceFilePath = null)
        {
            return sourceFilePath;
        }

        private static void NormalizeFileAttributes(string path)
        {
            foreach (string file in Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }

        private static void RemoveCurrentUserDenyRules(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            using WindowsIdentity identity =
                WindowsIdentity.GetCurrent();
            Assert.NotNull(identity.User);
            var directory = new DirectoryInfo(path);
            DirectorySecurity security =
                FileSystemAclExtensions.GetAccessControl(
                    directory,
                    AccessControlSections.Access);
            bool changed = false;
            AuthorizationRuleCollection rules = security.GetAccessRules(
                true,
                false,
                typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Deny &&
                    Equals(rule.IdentityReference, identity.User))
                {
                    security.RemoveAccessRuleSpecific(rule);
                    changed = true;
                }
            }
            if (changed)
            {
                directory.SetAccessControl(security);
            }

            foreach (string child in Directory.EnumerateDirectories(
                path,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                RemoveCurrentUserDenyRules(child);
            }
        }
    }

    internal static class AnalysisReportFixtureRootResolver
    {
        internal static string Resolve(
            string sourceFilePath,
            string baseDirectory,
            string checkoutRootOverride,
            string workingDirectory,
            out string resolution)
        {
            string sourceRoot = GetSourceRoot(sourceFilePath);
            if (ContainsSolution(sourceRoot))
            {
                resolution = "CallerFilePath";
                return sourceRoot;
            }

            string outputRoot = FindRepositoryRoot(baseDirectory);
            if (outputRoot != null)
            {
                resolution = "AppContext.BaseDirectory";
                return outputRoot;
            }

            string overrideRoot = GetFullPathIfValid(
                checkoutRootOverride);
            if (ContainsSolution(overrideRoot))
            {
                resolution = "XMAT_TEST_CHECKOUT_ROOT";
                return overrideRoot;
            }

            string workingRoot = FindRepositoryRoot(
                workingDirectory);
            if (workingRoot != null)
            {
                resolution = "Environment.CurrentDirectory";
                return workingRoot;
            }

            throw new DirectoryNotFoundException(
                "Could not locate XboxMultiplayerAnalysisTool.sln from " +
                $"CallerFilePath '{sourceFilePath}', base directory " +
                $"'{baseDirectory}', verified checkout override " +
                $"'XMAT_TEST_CHECKOUT_ROOT={checkoutRootOverride}', or " +
                $"working directory '{workingDirectory}'.");
        }

        private static string GetSourceRoot(string sourceFilePath)
        {
            string sourceDirectory = GetFullPathIfValid(
                Path.GetDirectoryName(sourceFilePath));
            if (sourceDirectory == null ||
                !Directory.Exists(sourceDirectory))
            {
                return null;
            }
            return Directory.GetParent(sourceDirectory)?.FullName;
        }

        private static string FindRepositoryRoot(string startPath)
        {
            string fullPath = GetFullPathIfValid(startPath);
            if (fullPath == null ||
                !Directory.Exists(fullPath))
            {
                return null;
            }

            var directory = new DirectoryInfo(fullPath);
            while (directory != null)
            {
                if (ContainsSolution(directory.FullName))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
            return null;
        }

        private static bool ContainsSolution(string directory)
        {
            return !string.IsNullOrEmpty(directory) &&
                Directory.Exists(directory) &&
                File.Exists(Path.Combine(
                    directory,
                    "XboxMultiplayerAnalysisTool.sln"));
        }

        private static string GetFullPathIfValid(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                return null;
            }
        }
    }

    internal sealed class DirectoryAccessDenyScope : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly FileSystemAccessRule _denyRule;

        internal DirectoryAccessDenyScope(
            string path,
            FileSystemRights rights,
            InheritanceFlags inheritanceFlags = InheritanceFlags.None)
        {
            _directory = new DirectoryInfo(path);
            DirectorySecurity security =
                FileSystemAclExtensions.GetAccessControl(
                    _directory,
                    AccessControlSections.Access);
            using WindowsIdentity identity =
                WindowsIdentity.GetCurrent();
            Assert.NotNull(identity.User);
            _denyRule = new FileSystemAccessRule(
                identity.User,
                rights,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Deny);
            security.AddAccessRule(_denyRule);
            _directory.SetAccessControl(security);
        }

        public void Dispose()
        {
            if (!_directory.Exists)
            {
                return;
            }

            DirectorySecurity security =
                FileSystemAclExtensions.GetAccessControl(
                    _directory,
                    AccessControlSections.Access);
            security.RemoveAccessRuleSpecific(_denyRule);
            _directory.SetAccessControl(security);
        }
    }
}
