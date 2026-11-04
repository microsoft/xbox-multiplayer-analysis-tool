// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class SqLtDatabaseSaveTests
    {
        [Fact]
        public void SaveToFile_ReleasesDestinationAndProducesReadableDatabase()
        {
            using var directory = new TestDirectory();
            var path = Path.Join(directory.Path, "saved.xmatcap");
            using (SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table))
            {
                table.AddRow("expected");
                database.SaveToFile(path);
                table.AddRow("updated");
                database.SaveToFile(path);
            }

            using (var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.True(exclusive.Length > 0);
            }

            Assert.Equal(
                new[] { "expected", "updated" },
                ReadSavedValues(path));
            File.Delete(path);
            Assert.False(File.Exists(path));
        }

        [Theory]
        [InlineData("a.xmatcap", 8)]
        [InlineData("a.b", 2)]
        public void SaveToFile_BoundaryDestinationUsesNoLongerTemporaryName(
            string fileName,
            int hexDigits)
        {
            using var directory = new TestDirectory(shortPath: true);
            (string deepDirectory, string path) =
                CreateDeepDestination(
                    directory.Path,
                    fileName,
                    251);
            using SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table);
            table.AddRow("value");
            string temporaryPath = null;
            database.BackupFailureInjection =
                path => temporaryPath = path;

            database.SaveToFile(path);

            Assert.Equal(251, path.Length);
            Assert.Equal(
                259,
                path.Length +
                "-journal".Length);
            Assert.NotEqual(
                directory.Path,
                deepDirectory);
            Assert.Equal(
                fileName,
                Path.GetFileName(path));
            Assert.NotNull(
                temporaryPath);
            Assert.Equal(
                deepDirectory,
                Path.GetDirectoryName(
                    temporaryPath));
            Assert.Matches(
                hexDigits == 8
                    ? @"^\.[0-9a-f]{8}$"
                    : @"^\.[0-9a-f]{2}$",
                Path.GetFileName(
                    temporaryPath));
            Assert.True(
                temporaryPath.Length <=
                path.Length);
            Assert.True(
                temporaryPath.Length +
                "-journal".Length <
                260);
            Assert.True(
                new FileInfo(path).Length > 0);
            AssertOriginalAndNoTemporaryFile(
                deepDirectory,
                path,
                File.ReadAllBytes(path));
        }

        [Fact]
        public void SaveToFile_TemporaryNameCollisionsNeverModifyExistingFiles()
        {
            using var directory = new TestDirectory();
            var before =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);
            foreach (char hex in
                "0123456789abcdef")
            {
                string path = Path.Join(
                    directory.Path,
                    "." + hex);
                string contents =
                    "collision-" + hex;
                File.WriteAllText(
                    path,
                    contents);
                before.Add(
                    path,
                    contents);
            }
            string destination = Path.Join(
                directory.Path,
                ".b");
            using SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table);
            table.AddRow("replacement");

            CaptureSaveException exception =
                Assert.Throws<CaptureSaveException>(
                    () => database.SaveToFile(
                        destination));

            Assert.Equal(
                CaptureSaveFailure.Failed,
                exception.Failure);
            AssertEntries(
                directory.Path,
                before.Keys.ToArray());
            foreach ((string path, string contents) in
                before)
            {
                Assert.Equal(
                    contents,
                    File.ReadAllText(path));
            }
        }

        [Fact]
        public void SaveToFile_DirectoryNameCollisionRetriesWithoutModifyingDirectory()
        {
            using var directory = new TestDirectory();
            string collision = Path.Join(
                directory.Path,
                ".00");
            Directory.CreateDirectory(collision);
            string marker = Path.Join(
                collision,
                "marker.txt");
            File.WriteAllText(
                marker,
                "unchanged");
            string destination = Path.Join(
                directory.Path,
                "a.b");
            using SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table);
            table.AddRow("saved");
            Queue<string> candidates =
                UseTemporaryNames(
                    database,
                    2,
                    "00",
                    "01");

            database.SaveToFile(destination);

            Assert.Empty(candidates);
            Assert.True(File.Exists(destination));
            Assert.True(Directory.Exists(collision));
            Assert.Equal(
                "unchanged",
                File.ReadAllText(marker));
            Assert.False(
                Path.Exists(
                    Path.Join(
                        directory.Path,
                        ".01")));
            AssertEntries(
                directory.Path,
                collision,
                destination);
        }

        [Theory]
        [InlineData("-journal", false)]
        [InlineData("-wal", false)]
        [InlineData("-shm", true)]
        public void SaveToFile_ExistingSqliteCompanionIsNeverTouched(
            string suffix,
            bool directoryCompanion)
        {
            using var directory = new TestDirectory();
            string companion = Path.Join(
                directory.Path,
                ".0123abcd" + suffix);
            byte[] markerBytes = { 9, 8, 7, 6 };
            string markerPath;
            if (directoryCompanion)
            {
                Directory.CreateDirectory(companion);
                markerPath = Path.Join(
                    companion,
                    "marker.bin");
            }
            else
            {
                markerPath = companion;
            }
            File.WriteAllBytes(
                markerPath,
                markerBytes);
            string destination = Path.Join(
                directory.Path,
                "saved.xmatcap");
            using SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table);
            table.AddRow("saved");
            Queue<string> candidates =
                UseTemporaryNames(
                    database,
                    8,
                    "0123abcd",
                    "89abcdef");
            string temporary = null;
            database.BackupFailureInjection =
                path => temporary = path;

            database.SaveToFile(destination);

            Assert.Empty(candidates);
            Assert.Equal(
                ".89abcdef",
                Path.GetFileName(temporary));
            Assert.True(
                Path.Exists(companion));
            Assert.Equal(
                markerBytes,
                File.ReadAllBytes(markerPath));
            Assert.False(
                Path.Exists(
                    Path.Join(
                        directory.Path,
                        ".0123abcd")));
            Assert.Empty(
                Directory.GetFileSystemEntries(
                    directory.Path,
                    ".89abcdef*"));
            Assert.Equal(
                new[] { "saved" },
                ReadSavedValues(destination));
            AssertEntries(
                directory.Path,
                companion,
                destination);
        }

        [Fact]
        public void SaveToFile_PostReservationCompanionAndDeleteFailureRetryCleanly()
        {
            using var directory = new TestDirectory();
            string destination = Path.Join(
                directory.Path,
                "a.b");
            using SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table);
            table.AddRow("saved");
            Queue<string> candidates =
                UseTemporaryNames(
                    database,
                    2,
                    "00",
                    "01");
            byte[] marker = { 4, 3, 2, 1 };
            string firstTemporary = null;
            string companion = null;
            database.TemporaryReserved =
                path =>
                {
                    if (Path.GetFileName(path) ==
                        ".00")
                    {
                        firstTemporary = path;
                        companion =
                            path + "-journal";
                        File.WriteAllBytes(
                            companion,
                            marker);
                    }
                };
            var deleteCalls = 0;
            database.TemporaryDelete =
                path =>
                {
                    deleteCalls++;
                    if (deleteCalls == 1)
                    {
                        throw new IOException(
                            "Injected delete failure.");
                    }
                    File.Delete(path);
                };
            string committedTemporary = null;
            database.BackupFailureInjection =
                path =>
                    committedTemporary = path;

            database.SaveToFile(destination);

            Assert.Empty(candidates);
            Assert.Equal(1, deleteCalls);
            Assert.Equal(
                ".01",
                Path.GetFileName(
                    committedTemporary));
            Assert.True(File.Exists(firstTemporary));
            Assert.Equal(
                marker,
                File.ReadAllBytes(companion));
            Assert.False(
                Path.Exists(
                    Path.Join(
                        directory.Path,
                        ".01")));
            Assert.Equal(
                new[] { "saved" },
                ReadSavedValues(destination));
        }

        // A candidate equal to the destination's name, in any letter case, is skipped. A one-character destination
        // gets an undotted one-character temporary name; in every case the temporary name is no longer than the
        // destination's.
        [Theory]
        [InlineData(".a", "a", "b", ".b")]
        [InlineData(".A", "a", "b", ".b")]
        [InlineData("a", "a", "c", "c")]
        public void SaveToFile_SkipsCandidateEqualToTheDestination(
            string destinationName,
            string firstCandidate,
            string secondCandidate,
            string expectedTemporaryName)
        {
            using var directory = new TestDirectory();
            string destination = Path.Join(
                directory.Path,
                destinationName);
            using SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table);
            table.AddRow("saved");
            Queue<string> candidates =
                UseTemporaryNames(
                    database,
                    1,
                    firstCandidate,
                    secondCandidate);
            string temporary = null;
            database.BackupFailureInjection =
                path => temporary = path;

            database.SaveToFile(destination);

            Assert.Empty(candidates);
            Assert.Equal(
                expectedTemporaryName,
                Path.GetFileName(temporary));
            Assert.Equal(
                destination.Length,
                temporary.Length);
            AssertEntries(
                directory.Path,
                destination);
            Assert.Equal(
                new[] { "saved" },
                ReadSavedValues(destination));
        }

        [Fact]
        public void SaveToFile_ExistingDestinationIsReplacedOnlyAtCommit()
        {
            using var directory = new TestDirectory();
            string destination = Path.Join(
                directory.Path,
                ".a");
            File.WriteAllText(
                destination,
                "old-content");
            using SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table);
            table.AddRow("saved");
            Queue<string> candidates =
                UseTemporaryNames(
                    database,
                    1,
                    "a",
                    "b");
            string temporary = null;
            database.BackupFailureInjection =
                path =>
                {
                    temporary = path;
                    Assert.Equal(
                        "old-content",
                        File.ReadAllText(
                            destination));
                };

            database.SaveToFile(destination);

            Assert.Empty(candidates);
            Assert.Equal(
                ".b",
                Path.GetFileName(temporary));
            Assert.Equal(
                new[] { "saved" },
                ReadSavedValues(destination));
            AssertEntries(
                directory.Path,
                destination);
        }

        [Fact]
        public void SaveToFile_SkipsCandidateWhoseJournalIsTheDestination()
        {
            using var directory = new TestDirectory();
            string destination = Path.Join(
                directory.Path,
                ".0123abcd-journal");
            byte[] original = { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(
                destination,
                original);
            using SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table);
            table.AddRow("saved");
            Queue<string> candidates =
                UseTemporaryNames(
                    database,
                    8,
                    "0123abcd",
                    "89abcdef");
            string failedTemporary = null;
            database.BackupFailureInjection =
                path =>
                {
                    failedTemporary = path;
                    throw new IOException(
                        "Injected backup failure.");
                };

            Assert.Throws<CaptureSaveException>(
                () => database.SaveToFile(
                    destination));

            Assert.Empty(candidates);
            Assert.Equal(
                ".89abcdef",
                Path.GetFileName(
                    failedTemporary));
            Assert.Equal(
                original,
                File.ReadAllBytes(
                    destination));
            AssertEntries(
                directory.Path,
                destination);

            candidates =
                UseTemporaryNames(
                    database,
                    8,
                    "0123abcd",
                    "89abcdef");
            string committedTemporary = null;
            database.BackupFailureInjection =
                path =>
                    committedTemporary = path;

            database.SaveToFile(destination);

            Assert.Empty(candidates);
            Assert.Equal(
                ".89abcdef",
                Path.GetFileName(
                    committedTemporary));
            Assert.Equal(
                new[] { "saved" },
                ReadSavedValues(destination));
            AssertEntries(
                directory.Path,
                destination);
        }

        [Theory]
        [InlineData("backup")]
        [InlineData("move")]
        public void SaveToFile_FailurePreservesOriginalAndRemovesTemporaryFile(
            string failingStep)
        {
            using var directory = new TestDirectory();
            string path = Path.Join(
                directory.Path,
                failingStep + "-failure.xmatcap");
            using SqLtDatabase database =
                CreateSavedDatabase(
                    path,
                    out byte[] before);
            if (failingStep == "backup")
            {
                database.BackupFailureInjection =
                    _ => throw new IOException(
                        "Injected backup failure.");
            }
            else
            {
                database.MoveFailureInjection =
                    (_, _) => throw new IOException(
                        "Injected move failure.");
            }

            CaptureSaveException exception =
                Assert.Throws<CaptureSaveException>(
                    () => database.SaveToFile(path));

            Assert.Equal(
                CaptureSaveFailure.Failed,
                exception.Failure);
            AssertOriginalAndNoTemporaryFile(
                directory.Path,
                path,
                before);
        }

        [Fact]
        public void SaveToFile_TemporaryCleanupFailureDoesNotMaskSaveFailure()
        {
            using var directory = new TestDirectory();
            string path = Path.Join(
                directory.Path,
                "cleanup-failure.xmatcap");
            using SqLtDatabase database =
                CreateSavedDatabase(
                    path,
                    out byte[] before);
            string temporary = null;
            database.BackupFailureInjection =
                temporaryPath =>
                    temporary = temporaryPath;
            database.MoveFailureInjection =
                (_, _) => throw new IOException(
                    "Injected move failure.");
            database.TemporaryDelete =
                _ => throw new IOException(
                    "Injected cleanup failure.");

            CaptureSaveException exception =
                Assert.Throws<CaptureSaveException>(
                    () => database.SaveToFile(path));

            Assert.Equal(
                CaptureSaveFailure.Failed,
                exception.Failure);
            Assert.Contains(
                "Injected move failure.",
                exception.InnerException?.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                before,
                File.ReadAllBytes(path));
            Assert.NotNull(temporary);
            Assert.True(
                File.Exists(temporary));
            File.Delete(temporary);
        }

        [Theory]
        [InlineData(FileShare.Read)]
        [InlineData(FileShare.ReadWrite)]
        [InlineData(FileShare.None)]
        public void SaveToFile_LockedDestinationPreservesOriginalAndRemovesTemporaryFile(
            FileShare sharing)
        {
            using var directory = new TestDirectory();
            string path = Path.Join(
                directory.Path,
                "locked.xmatcap");
            using SqLtDatabase database =
                CreateSavedDatabase(
                    path,
                    out byte[] before);
            using (var locked =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    sharing))
            {
                CaptureSaveException exception =
                    Assert.Throws<CaptureSaveException>(
                        () => database.SaveToFile(path));
                Assert.Equal(
                    CaptureSaveFailure
                        .DestinationLocked,
                    exception.Failure);
            }

            AssertOriginalAndNoTemporaryFile(
                directory.Path,
                path,
                before);
        }

        private static SqLtDatabase CreateDatabase(
            out IDataTable table)
        {
            var database = new SqLtDatabase();
            database.Initialize();
            table = database.CreateTable(
                "SavedData",
                new Field<string>("Value"));
            return database;
        }

        // Saves a capture holding "original" to the path, then adds "replacement", so the next save replaces the file.
        private static SqLtDatabase CreateSavedDatabase(
            string path,
            out byte[] original)
        {
            SqLtDatabase database =
                CreateDatabase(
                    out IDataTable table);
            table.AddRow("original");
            database.SaveToFile(path);
            original = File.ReadAllBytes(path);
            table.AddRow("replacement");
            return database;
        }

        // The save draws its temporary names from these candidates, in order, and must ask for names of this length.
        private static Queue<string> UseTemporaryNames(
            SqLtDatabase database,
            int length,
            params string[] names)
        {
            var candidates = new Queue<string>(names);
            database.TemporaryHex = requested =>
            {
                Assert.Equal(length, requested);
                return candidates.Dequeue();
            };
            return candidates;
        }

        private static string[] ReadSavedValues(
            string path)
        {
            var connectionString =
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString();
            using var connection =
                new SqliteConnection(
                    connectionString);
            connection.Open();
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT Value FROM SavedData ORDER BY rowid";
            using SqliteDataReader reader =
                command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
            {
                values.Add(
                    reader.GetString(0));
            }
            return values.ToArray();
        }

        // The directory holds exactly these files and folders.
        private static void AssertEntries(
            string directory,
            params string[] expected) =>
            Assert.Equal(
                expected.OrderBy(
                    path => path,
                    StringComparer.Ordinal),
                Directory.GetFileSystemEntries(directory)
                    .OrderBy(
                        path => path,
                        StringComparer.Ordinal));

        private static void AssertOriginalAndNoTemporaryFile(
            string directory,
            string path,
            byte[] expected)
        {
            Assert.Equal(
                expected,
                File.ReadAllBytes(path));
            AssertEntries(
                directory,
                path);
        }

        private static (
            string Directory,
            string Destination)
            CreateDeepDestination(
                string root,
                string fileName,
                int destinationLength)
        {
            string deepDirectory = root;
            string segment =
                new('d', 20);
            while (Path.Join(
                    deepDirectory,
                    fileName).Length +
                segment.Length +
                1 <=
                destinationLength - 2)
            {
                deepDirectory = Path.Join(
                    deepDirectory,
                    segment);
                System.IO.Directory.CreateDirectory(
                    deepDirectory);
            }
            int remaining =
                destinationLength -
                Path.Join(
                    deepDirectory,
                    fileName).Length -
                1;
            Assert.InRange(
                remaining,
                1,
                segment.Length +
                1);
            deepDirectory = Path.Join(
                deepDirectory,
                new string(
                    'd',
                    remaining));
            System.IO.Directory.CreateDirectory(
                deepDirectory);
            return (
                deepDirectory,
                Path.Join(
                    deepDirectory,
                    fileName));
        }

        // A short path roots the folder in the temporary folder, so path-length tests do not depend on the length of
        // the build output folder.
        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory(
                bool shortPath = false)
            {
                Path = shortPath
                    ? System.IO.Path.Join(
                        System.IO.Path.GetTempPath(),
                        "xmat-path-" +
                        Guid.NewGuid().ToString("N"))
                    : System.IO.Path.Join(
                        AppContext.BaseDirectory,
                        "DatabaseSaveFixtures",
                        Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
        }
    }
}