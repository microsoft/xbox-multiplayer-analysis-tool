// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using XMAT.DataAbstractionLayer;
using XMAT.Tests.Fixtures;

namespace XMAT.Tests
{
    public class SqLtSchemaPreflightTests
    {
        private const string NativeSha256 =
            "3061B1C0E66BE7BA1A3223B5B3AF90AFF353C9BCA07BD0A02AEFBE2DFBACB81F";

        [Fact]
        public void Preflight_AcceptsSchemaWrittenByTheProductDatabase()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "product-schema.xmatcap");

            SqLtValidatedSchema schema =
                ValidateGui(path);

            Assert.Equal(
                new[]
                {
                    "CaptureDevices",
                    "WebProxyConnections",
                },
                schema.Tables.Keys.OrderBy(
                    name => name,
                    StringComparer.Ordinal));
        }

        [Fact]
        public void Preflight_AcceptsKnownTablesFormattingExtensionsAndOverflow()
        {
            using var directory = new TestDirectory();
            string quotedDevices =
                "CREATE TABLE [CaptureDevices] (\"id\" INTEGER PRIMARY KEY," +
                "[DeviceType] INTEGER,[DeviceName] TEXT,[CaptureType] INTEGER," +
                "ExtraText TEXT,ExtraNumber INTEGER,ExtraUntyped)";
            string path = HostileSqliteSchemas.CreateDatabase(
                directory.Path,
                "valid.xmatcap",
                quotedDevices,
                HostileSqliteSchemas.LongExtensionDdl(
                    SqLtSchemaProfile.MaxExtensionColumns),
                HostileSqliteSchemas.NetworkTracePacketsDdl);

            SqLtValidatedSchema schema =
                ValidateGui(path);

            Assert.Equal(
                new[]
                {
                    "CaptureDevices",
                    "NetworkTracePackets",
                    "WebProxyConnections",
                },
                schema.Tables.Keys.OrderBy(
                    name => name,
                    StringComparer.Ordinal));
            Assert.Equal(
                16 + SqLtSchemaProfile.MaxExtensionColumns,
                schema.Tables["WebProxyConnections"]
                    .Columns.Count);
        }

        [Theory]
        [InlineData(
            "CREATE INDEX ExtraIndex ON WebProxyConnections(RequestNumber)")]
        [InlineData(
            "CREATE VIEW ExtraView AS SELECT RequestNumber FROM WebProxyConnections")]
        [InlineData(
            "CREATE TRIGGER ExtraTrigger AFTER INSERT ON WebProxyConnections BEGIN SELECT 1; END")]
        [InlineData(
            "CREATE TABLE UnknownTable(id INTEGER PRIMARY KEY,Value TEXT)")]
        public void Preflight_RejectsUnknownSchemaObjects(string extraObject)
        {
            using var directory = new TestDirectory();
            string path = HostileSqliteSchemas.CreateDatabase(
                directory.Path,
                "unknown-object.xmatcap",
                HostileSqliteSchemas.CaptureDevicesDdl,
                HostileSqliteSchemas.WebProxyConnectionsDdl,
                extraObject);

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => ValidateGui(path));

            Assert.Equal(
                SqLtSchemaFailure.Unsupported,
                exception.Failure);
        }

        [Fact]
        public void Preflight_RejectsGeneratedColumnsBeforeSqliteOpensTheSchema()
        {
            using var directory = new TestDirectory();
            string path =
                HostileSqliteSchemas.CreateGeneratedColumnCapture(
                directory.Path,
                "generated.xmatcap");

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => ValidateGui(path));

            Assert.Equal(
                SqLtSchemaFailure.Unsupported,
                exception.Failure);
        }

        [Fact]
        public void Preflight_BoundsSchemaRecordCount()
        {
            using var directory = new TestDirectory();
            var statements = new List<string>
            {
                HostileSqliteSchemas.CaptureDevicesDdl,
                HostileSqliteSchemas.WebProxyConnectionsDdl,
            };
            for (var index = 0;
                index < SqLtSchemaProfile.MaxSchemaRecords;
                index++)
            {
                statements.Add(
                    "CREATE TABLE Extra" +
                    index.ToString(
                        "D2",
                        CultureInfo.InvariantCulture) +
                    "(id INTEGER PRIMARY KEY,Value TEXT)");
            }
            string path = HostileSqliteSchemas.CreateDatabase(
                directory.Path,
                "many-tables.xmatcap",
                statements.ToArray());

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => ValidateGui(path));

            Assert.Equal(
                SqLtSchemaFailure.Limit,
                exception.Failure);
        }

        [Fact]
        public void Preflight_BoundsSchemaRecordPayload()
        {
            using var directory = new TestDirectory();
            string path = HostileSqliteSchemas.CreateOrdinaryCapture(
                directory.Path,
                "huge-ddl.xmatcap");
            HostileSqliteSchemas.ReplaceSchemaSql(
                path,
                "WebProxyConnections",
                "CREATE TABLE WebProxyConnections(" +
                new string(
                    'x',
                    SqLtSchemaProfile.MaxSchemaRecordBytes * 2) +
                ")");

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => ValidateGui(path));

            Assert.Equal(
                SqLtSchemaFailure.Limit,
                exception.Failure);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Preflight_TinyFilesAreInvalid(
            bool cliProfile)
        {
            using var stream =
                new MemoryStream(new byte[100]);

            // The CLI profile's database limit is smaller than the file, but a file too small to be a database is
            // reported as invalid rather than as over the limit.
            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => SqLtSchemaPreflight.Validate(
                        stream,
                        cliProfile
                            ? SqLtSchemaProfile.CreateCli(
                                64,
                                1024 * 1024)
                            : SqLtSchemaProfile.CreateGui()));

            Assert.Equal(
                SqLtSchemaFailure.Invalid,
                exception.Failure);
        }

        [Fact]
        public void Preflight_UsesTrustedHeaderPageCountWhenCountersMatch()
        {
            using var directory = new TestDirectory();
            string path =
                HostileSqliteSchemas.CreateOrdinaryCapture(
                    directory.Path,
                    "trusted-page-count.xmatcap");
            byte[] header = File.ReadAllBytes(path);
            uint declaredPages =
                BinaryPrimitives.ReadUInt32BigEndian(
                    header.AsSpan(
                        28,
                        4));
            HostileSqliteSchemas.SetHeaderPageCount(
                path,
                declaredPages,
                countersMatch: true);
            HostileSqliteSchemas.AppendUnusedPage(path);

            SqLtValidatedSchema schema =
                ValidateGui(path);

            Assert.Equal(2, schema.Tables.Count);
        }

        [Fact]
        public void Preflight_UsesPhysicalPageCountWhenCountersDiffer()
        {
            using var directory = new TestDirectory();
            string path =
                HostileSqliteSchemas.CreateOrdinaryCapture(
                    directory.Path,
                    "physical-page-count.xmatcap",
                    HostileSqliteSchemas.LongExtensionDdl(
                        SqLtSchemaProfile.MaxExtensionColumns));
            HostileSqliteSchemas.SetHeaderPageCount(
                path,
                1,
                countersMatch: false);

            SqLtValidatedSchema schema =
                ValidateGui(path);

            Assert.Equal(2, schema.Tables.Count);
        }

        [Fact]
        public void Preflight_RejectsTrustedPageCountBeyondPhysicalFile()
        {
            using var directory = new TestDirectory();
            string path =
                HostileSqliteSchemas.CreateOrdinaryCapture(
                    directory.Path,
                    "truncated.xmatcap");
            byte[] header = File.ReadAllBytes(path);
            uint physicalPages = checked(
                (uint)(
                    header.Length /
                    BinaryPrimitives.ReadUInt16BigEndian(
                        header.AsSpan(
                            16,
                            2))));
            HostileSqliteSchemas.SetHeaderPageCount(
                path,
                physicalPages + 1,
                countersMatch: true);

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => ValidateGui(path));

            Assert.Equal(
                SqLtSchemaFailure.Invalid,
                exception.Failure);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Preflight_RejectsOverflowPageCyclesAndInvalidPointers(
            bool cycle)
        {
            using var directory = new TestDirectory();
            string path = HostileSqliteSchemas.CreateOrdinaryCapture(
                directory.Path,
                "overflow.xmatcap",
                HostileSqliteSchemas.LongExtensionDdl(
                    SqLtSchemaProfile.MaxExtensionColumns));
            HostileSqliteSchemas.CorruptFirstOverflowPage(
                path,
                cycle);

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => ValidateGui(path));

            Assert.Equal(
                SqLtSchemaFailure.Invalid,
                exception.Failure);
        }

        [Fact]
        public void Profile_MaterializesEveryKnownTableAndReadsBackLimits()
        {
            using var directory = new TestDirectory();
            string path = HostileSqliteSchemas.CreateOrdinaryCapture(
                directory.Path,
                "profiles.xmatcap",
                HostileSqliteSchemas.LongExtensionDdl(
                    SqLtSchemaProfile.MaxExtensionColumns),
                includeNetworkTrace: true);

            using var database =
                new SqLtReadonlyDatabase(
                    path,
                    SqLtSchemaProfile.CreateGui());

            Assert.Equal(
                new[]
                {
                    "CaptureDevices",
                    "NetworkTracePackets",
                    "WebProxyConnections",
                },
                database.TableNames().OrderBy(
                    name => name,
                    StringComparer.Ordinal));
            Assert.Equal(
                1_000_000_000,
                database.AppliedLimits.Length);
            Assert.Equal(
                SqLtSchemaProfile.MaxSqlLength,
                database.AppliedLimits.SqlLength);
            Assert.Equal(
                SqLtSchemaProfile.MaxColumns,
                database.AppliedLimits.Columns);
            Assert.Equal(
                SqLtSchemaProfile.MaxExpressionDepth,
                database.AppliedLimits.ExpressionDepth);
            Assert.Equal(
                "3.50.3",
                database.ReadSqliteVersion());
        }

        [Fact]
        public void CliProfile_MaterializesOnlyItsTablesAndReadsBackLimits()
        {
            using var directory = new TestDirectory();
            string path = HostileSqliteSchemas.CreateOrdinaryCapture(
                directory.Path,
                "profiles.xmatcap",
                HostileSqliteSchemas.LongExtensionDdl(
                    SqLtSchemaProfile.MaxExtensionColumns),
                includeNetworkTrace: true);

            using var database =
                new SqLtReadonlyDatabase(
                    path,
                    SqLtSchemaProfile.CreateCli(
                        32L * 1024 * 1024));

            Assert.Equal(
                new[]
                {
                    "CaptureDevices",
                    "WebProxyConnections",
                },
                database.TableNames().OrderBy(
                    name => name,
                    StringComparer.Ordinal));
            Assert.Null(
                database.TableByName(
                    "NetworkTracePackets"));
            Assert.Equal(
                33 * 1024 * 1024,
                database.AppliedLimits.Length);
            Assert.Equal(
                SqLtSchemaProfile.MaxSqlLength,
                database.AppliedLimits.SqlLength);
            Assert.Equal(
                SqLtSchemaProfile.MaxColumns,
                database.AppliedLimits.Columns);
            Assert.Equal(
                SqLtSchemaProfile.MaxExpressionDepth,
                database.AppliedLimits.ExpressionDepth);

            SqLtSchemaProfile gui =
                SqLtSchemaProfile.CreateGui();
            Assert.Null(
                gui.MaxDatabaseBytes);
            Assert.Null(
                gui.MaxValueBytes);
        }

        [Fact]
        public void QuotedKeywordExtensionColumn_RemainsReadable()
        {
            using var directory = new TestDirectory();
            string webDdl =
                HostileSqliteSchemas
                    .WebProxyConnectionsDdl
                    .Insert(
                        HostileSqliteSchemas
                            .WebProxyConnectionsDdl.Length - 1,
                        ",\"ORDER\" TEXT");
            string path =
                HostileSqliteSchemas.CreateOrdinaryCapture(
                    directory.Path,
                    "quoted-keyword.xmatcap",
                    webDdl);

            using var database =
                new SqLtReadonlyDatabase(
                    path,
                    SqLtSchemaProfile.CreateGui());

            Assert.Empty(
                database.TableByName(
                        "WebProxyConnections")
                    .Dataset.DataRecords);
        }

        [Fact]
        public void Open_RefusesACaptureWhoseWriteAheadLogChangesItsSchema()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 3));
            File.WriteAllBytes(
                path + "-wal",
                HostileSqliteSchemas.CreateWriteAheadLog(
                    path,
                    "ALTER TABLE WebProxyConnections ADD COLUMN WalProbe TEXT " +
                    "GENERATED ALWAYS AS (printf('%.*c',300000000,'x')) VIRTUAL"));
            // The capture file alone passes the preflight, but SQLite would read the generated column from the log.
            _ = ValidateGui(path);
            Assert.Contains(
                "GENERATED ALWAYS",
                HostileSqliteSchemas.ReadSchemaSqlWithWriteAheadLog(
                    path,
                    "WebProxyConnections"),
                StringComparison.Ordinal);
            string[] before = DirectoryState(directory.Path);

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => new SqLtReadonlyDatabase(
                        path,
                        SqLtSchemaProfile.CreateGui()));

            Assert.Equal(
                SqLtSchemaFailure.Sidecar,
                exception.Failure);
            Assert.Equal(
                before,
                DirectoryState(directory.Path));
        }

        [Theory]
        [InlineData("-journal", false)]
        [InlineData("-shm", false)]
        [InlineData("-wal", true)]
        public void Open_RefusesACaptureWithSqliteSidecarsAndLeavesFilesUnchanged(
            string suffix,
            bool sidecarIsDirectory)
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap");
            if (sidecarIsDirectory)
            {
                Directory.CreateDirectory(
                    path + suffix);
            }
            else
            {
                File.WriteAllBytes(
                    path + suffix,
                    new byte[] { 1 });
            }
            string[] before = DirectoryState(directory.Path);

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => new SqLtReadonlyDatabase(
                        path,
                        SqLtSchemaProfile.CreateGui()));

            Assert.Equal(
                SqLtSchemaFailure.Sidecar,
                exception.Failure);
            Assert.Equal(
                before,
                DirectoryState(directory.Path));
        }

        [Fact]
        public void Open_RefusesAWriteAheadCaptureWithoutCreatingFiles()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap");
            HostileSqliteSchemas.SwitchToWriteAheadLogging(path);
            string[] before = DirectoryState(directory.Path);
            _ = Assert.Single(before);

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => new SqLtReadonlyDatabase(
                        path,
                        SqLtSchemaProfile.CreateGui()));

            Assert.Equal(
                SqLtSchemaFailure.WriteAheadLog,
                exception.Failure);
            Assert.Equal(
                "CAPTURE_OPEN_SCHEMA_ERROR",
                MainWindow.CaptureOpenErrorKey(
                    exception.Failure));
            Assert.Equal(
                before,
                DirectoryState(directory.Path));
        }

        [Fact]
        public void Open_KeepsReadingTheCheckedFileWhenAWriteAheadLogAppearsLater()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 3));
            // Setting user_version also puts the database header page in the log.
            byte[] log = HostileSqliteSchemas.CreateWriteAheadLog(
                path,
                "UPDATE WebProxyConnections SET RequestPath='/wal-applied'",
                "PRAGMA user_version=7");
            using var database =
                new SqLtReadonlyDatabase(
                    path,
                    SqLtSchemaProfile.CreateGui());

            File.WriteAllBytes(
                path + "-wal",
                log);
            string[] paths = database
                .TableByName("WebProxyConnections")
                .Dataset.DataRecords
                .Select(record => record.Str("RequestPath"))
                .ToArray();

            Assert.Equal(3, paths.Length);
            Assert.DoesNotContain(
                "/wal-applied",
                paths);
            Assert.False(
                File.Exists(path + "-shm"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Open_RefusesACaptureThatAnotherProgramCanWrite(
            bool mappedViewOnly)
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap");
            string[] before = DirectoryState(directory.Path);
            var writer = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            MemoryMappedFile mapping = null;
            MemoryMappedViewAccessor view = null;
            try
            {
                if (mappedViewOnly)
                {
                    mapping = MemoryMappedFile.CreateFromFile(
                        writer,
                        null,
                        0,
                        MemoryMappedFileAccess.ReadWrite,
                        HandleInheritability.None,
                        leaveOpen: true);
                    view = mapping.CreateViewAccessor(
                        0,
                        0,
                        MemoryMappedFileAccess.ReadWrite);
                    writer.Dispose();
                }

                Assert.Throws<IOException>(
                    () => new SqLtReadonlyDatabase(
                        path,
                        SqLtSchemaProfile.CreateGui()));
            }
            finally
            {
                view?.Dispose();
                mapping?.Dispose();
                writer.Dispose();
            }

            Assert.Equal(
                before,
                DirectoryState(directory.Path));
        }

        [Theory]
        [InlineData("-wal")]
        [InlineData("-journal")]
        [InlineData("-wal created and removed")]
        public void Open_ReadsOnlyTheCheckedFileWhenSidecarsChangeBeforeSqliteOpens(
            string change)
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 3));
            byte[] log = HostileSqliteSchemas.CreateWriteAheadLog(
                path,
                "UPDATE WebProxyConnections SET RequestPath='/wal-applied'",
                "ALTER TABLE WebProxyConnections ADD COLUMN WalProbe TEXT " +
                "GENERATED ALWAYS AS (printf('%.*c',300000000,'x')) VIRTUAL");
            string checkedFile =
                Assert.Single(
                    DirectoryState(directory.Path));
            string[] changed = null;

            using var database =
                new SqLtReadonlyDatabase(
                    path,
                    SqLtSchemaProfile.CreateGui(),
                    databasePath =>
                    {
                        // Between the schema check and SQLite's first access, the checked file cannot be written,
                        // renamed or deleted.
                        Assert.Throws<IOException>(
                            () => new FileStream(
                                databasePath,
                                FileMode.Open,
                                FileAccess.Write,
                                FileShare.ReadWrite).Dispose());
                        Assert.Throws<IOException>(
                            () => File.Move(
                                databasePath,
                                databasePath + ".moved"));
                        Assert.Throws<IOException>(
                            () => File.Delete(databasePath));
                        string sidecar =
                            databasePath +
                            (change == "-journal"
                                ? "-journal"
                                : "-wal");
                        File.WriteAllBytes(
                            sidecar,
                            change == "-journal"
                                ? new byte[] { 1 }
                                : log);
                        if (change == "-wal created and removed")
                        {
                            File.Delete(sidecar);
                        }
                        changed = DirectoryState(directory.Path);
                    });
            string[] paths = database
                .TableByName("WebProxyConnections")
                .Dataset.DataRecords
                .Select(record => record.Str("RequestPath"))
                .ToArray();

            Assert.Equal(3, paths.Length);
            Assert.DoesNotContain(
                "/wal-applied",
                paths);
            Assert.Contains(
                checkedFile,
                changed);
            Assert.Equal(
                changed,
                DirectoryState(directory.Path));
        }

        [Theory]
        [InlineData("\u00fcn\u00efc\u00f8d\u00e9 \u65e5\u672c\u8a9e", "capture \u00fc.xmatcap", null)]
        [InlineData("hash", "capture#1.xmatcap", "capture")]
        [InlineData("percent", "capture%41.xmatcap", "captureA.xmatcap")]
        public void Open_ReadsTheCheckedFileWhateverItsName(
            string folderName,
            string fileName,
            string decoyName)
        {
            using var directory = new TestDirectory();
            string folder = System.IO.Path.Combine(
                directory.Path,
                folderName);
            string path = SyntheticCaptures.WriteXmatcap(
                folder,
                fileName,
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 3));
            // A name that SQLite misread as a URI would open the decoy, which has five requests.
            if (decoyName != null)
            {
                _ = SyntheticCaptures.WriteXmatcap(
                    folder,
                    decoyName,
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 5));
            }
            string[] before = DirectoryState(folder);

            using (var database =
                new SqLtReadonlyDatabase(
                    path,
                    SqLtSchemaProfile.CreateGui()))
            {
                Assert.Equal(
                    3,
                    database
                        .TableByName("WebProxyConnections")
                        .Dataset.DataRecords
                        .Count());
            }

            Assert.Equal(
                before,
                DirectoryState(folder));
        }

        [Fact]
        public void Open_ReadsACaptureWhosePathIsLongerThanMaxPath()
        {
            // The test binaries' directory is about 130 characters long on CI, so the long path starts at a short root.
            using var directory = new TestDirectory(
                System.IO.Path.GetTempPath());
            string written = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 3));
            string folder = directory.Path;
            while (folder.Length < 280)
            {
                folder = System.IO.Path.Combine(
                    folder,
                    new string('d', 60));
            }
            Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(
                folder,
                "capture.xmatcap");
            File.Move(
                written,
                path);
            string[] before = DirectoryState(folder);

            using (var database =
                new SqLtReadonlyDatabase(
                    path,
                    SqLtSchemaProfile.CreateGui()))
            {
                Assert.Equal(
                    3,
                    database
                        .TableByName("WebProxyConnections")
                        .Dataset.DataRecords
                        .Count());
            }

            Assert.True(path.Length > 260);
            Assert.Equal(
                before,
                DirectoryState(folder));
        }

        [Fact]
        public void Open_ReadsTheCheckedFileWhenALinkInItsPathIsRetargeted()
        {
            using var directory = new TestDirectory();
            string checkedFolder = System.IO.Path.Combine(
                directory.Path,
                "checked");
            string otherFolder = System.IO.Path.Combine(
                directory.Path,
                "other");
            string checkedCapture = SyntheticCaptures.WriteXmatcap(
                checkedFolder,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 3));
            string other = SyntheticCaptures.WriteXmatcap(
                otherFolder,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 5));
            string link = System.IO.Path.Combine(
                directory.Path,
                "link");
            CreateJunction(
                link,
                checkedFolder);
            string path = System.IO.Path.Combine(
                link,
                "capture.xmatcap");

            try
            {
                using var database =
                    new SqLtReadonlyDatabase(
                        path,
                        SqLtSchemaProfile.CreateGui(),
                        _ =>
                        {
                            // The folder that holds the checked file cannot be renamed while the file is open.
                            Exception moved = Record.Exception(
                                () => Directory.Move(
                                    checkedFolder,
                                    checkedFolder + "-moved"));
                            Assert.True(
                                moved is IOException or UnauthorizedAccessException,
                                moved?.ToString());
                            Directory.Delete(link);
                            CreateJunction(
                                link,
                                otherFolder);
                            // Opening the given path again would now read the other capture.
                            Assert.Equal(
                                File.ReadAllBytes(other),
                                File.ReadAllBytes(path));
                        });

                Assert.Equal(
                    3,
                    database
                        .TableByName("WebProxyConnections")
                        .Dataset.DataRecords
                        .Count());
                // The hash is taken through the guarded handle, not the path that now leads to the other capture.
                Assert.Equal(
                    SHA256.HashData(
                        File.ReadAllBytes(checkedCapture)),
                    database.ComputeSha256());
            }
            finally
            {
                // Without administrator rights, a recursive delete cannot remove a junction; deleting the link itself
                // removes only the reparse point.
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
            }

            Assert.False(
                Directory.Exists(link));
            Assert.True(
                File.Exists(checkedCapture));
            Assert.True(
                File.Exists(other));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Open_RequiresANormalizedFinalPath(
            bool dosFails,
            bool guidFails)
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 3));
            string[] before = DirectoryState(directory.Path);
            var requested = new List<uint>();
            var sqliteOpens = false;

            SqLtReadonlyDatabase Open() =>
                new SqLtReadonlyDatabase(
                    path,
                    SqLtSchemaProfile.CreateGui(),
                    _ => sqliteOpens = true,
                    flags =>
                    {
                        requested.Add(flags);
                        return flags switch
                        {
                            0x0 => dosFails,
                            0x1 => guidFails,
                            _ => false,
                        };
                    });

            if (guidFails)
            {
                SqLtSchemaException exception =
                    Assert.Throws<SqLtSchemaException>(
                        () => Open());

                Assert.Equal(
                    SqLtSchemaFailure.UnsupportedLocation,
                    exception.Failure);
                Assert.False(sqliteOpens);
                using (var writer = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    Assert.True(writer.CanWrite);
                }
            }
            else
            {
                using SqLtReadonlyDatabase database = Open();

                Assert.True(sqliteOpens);
                Assert.Equal(
                    3,
                    database
                        .TableByName("WebProxyConnections")
                        .Dataset.DataRecords
                        .Count());
            }

            Assert.Equal(
                dosFails
                    ? new uint[] { 0x0, 0x1 }
                    : new uint[] { 0x0 },
                requested);
            Assert.Equal(
                before,
                DirectoryState(directory.Path));
            File.Delete(path);
            Assert.False(
                File.Exists(path));
        }

        [Fact]
        public void Open_RefusesADatabaseLargerThanTheCliProfileAllows()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap");
            long length = new FileInfo(path).Length;

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => new SqLtReadonlyDatabase(
                        path,
                        SqLtSchemaProfile.CreateCli(
                            length - 1,
                            1024 * 1024)));

            Assert.Equal(
                SqLtSchemaFailure.Limit,
                exception.Failure);
            using var database =
                new SqLtReadonlyDatabase(
                    path,
                    SqLtSchemaProfile.CreateCli(
                        length,
                        1024 * 1024));
            Assert.NotNull(
                database.TableByName(
                    "WebProxyConnections"));
        }

        [Fact]
        public void ComputeSha256_HashesTheGuardedFileWithoutDisturbingReads()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 1000));
            // Larger than one 64 KiB read, so the hash spans several reads.
            Assert.True(
                new FileInfo(path).Length > 64 * 1024);
            byte[] expected =
                SHA256.HashData(
                    File.ReadAllBytes(path));
            var database =
                new SqLtReadonlyDatabase(path);
            try
            {
                Assert.Equal(
                    expected,
                    database.ComputeSha256());
                Assert.Equal(
                    1000,
                    database
                        .TableByName("WebProxyConnections")
                        .Dataset.DataRecords
                        .Count());
                Assert.Equal(
                    expected,
                    database.ComputeSha256());
                using var cancellation =
                    new CancellationTokenSource();
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(
                    () => database.ComputeSha256(
                        cancellation.Token));
            }
            finally
            {
                database.Dispose();
            }

            Assert.Throws<ObjectDisposedException>(
                () => database.ComputeSha256());
        }

        [Fact]
        public void PackagedSqliteBinary_HasThePinnedIdentity()
        {
            string path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "e_sqlite3.dll");

            Assert.True(
                File.Exists(path));
            Assert.Equal(
                1_910_272L,
                new FileInfo(path).Length);
            Assert.Equal(
                NativeSha256,
                Convert.ToHexString(
                    SHA256.HashData(
                        File.ReadAllBytes(path))));
        }

        private static SqLtValidatedSchema ValidateGui(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return SqLtSchemaPreflight.Validate(
                stream,
                SqLtSchemaProfile.CreateGui());
        }

        // mklink /J needs no privilege, unlike a symbolic link.
        private static void CreateJunction(
            string link,
            string target)
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in
                new[] { "/c", "mklink", "/J", link, target })
            {
                start.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(start);
            string output =
                process.StandardOutput.ReadToEnd() +
                process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                output);
        }

        private static string[] DirectoryState(string path) =>
            Directory.GetFileSystemEntries(path)
                .OrderBy(
                    entry => entry,
                    StringComparer.Ordinal)
                .Select(entry =>
                    System.IO.Path.GetFileName(entry) +
                    (Directory.Exists(entry)
                        ? "/"
                        : ":" + Convert.ToHexString(
                            SHA256.HashData(
                                File.ReadAllBytes(entry)))))
                .ToArray();

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory()
                : this(
                    System.IO.Path.Combine(
                        AppContext.BaseDirectory,
                        "SchemaTestArtifacts"))
            {
            }

            internal TestDirectory(string root)
            {
                Path = System.IO.Path.Combine(
                    root,
                    $"XMAT-schema-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(
                        Path,
                        true);
                }
            }
        }
    }
}
