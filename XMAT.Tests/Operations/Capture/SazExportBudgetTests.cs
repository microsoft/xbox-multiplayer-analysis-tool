// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

// S2 RESOURCE/ZIP64 GATE PLAN
// --------------------------------
// The long T8 and T17 legs do no work unless XMAT_RUN_S2_RESOURCE_GATES=1. Run each one in its own
// `dotnet test --no-build --filter` child process, never in the baseline testhost. The T8 memory legs export at the
// production limits (250,000 rows) and assert an incremental peak private-bytes ceiling of 900,000,000 bytes. The T17
// legs cover the ZIP64 entry-count boundary (65,534 vs 65,535 entries), a 250,000-row archive read back through both
// importers, an archive over the 4 GiB budget that publishes nothing, and the largest archive under it, which keeps
// classic 32-bit offsets. The size legs write several GiB to the temporary folder. The ungated T8 test always proves
// that rows are read while the archive is written and released afterwards.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Operations.Capture
{
    public sealed class SazExportBudgetTests
    {
        private const string ResourceGate =
            "XMAT_RUN_S2_RESOURCE_GATES";

        [Fact]
        public void T14_RowLimitAcceptsTheLimitAndRefusesTheNextKey()
        {
            SazExportBudgets budgets =
                SazExportBudgets.Default with
                {
                    MaxRows = 3,
                };
            OperationResult<SazExportPlan> at =
                Prepare(
                    new RangeSource(3),
                    budgets);
            var overSource = new RangeSource(4);
            OperationResult<SazExportPlan> over =
                Prepare(overSource, budgets);

            Assert.True(at.Succeeded);
            Assert.Equal(3, at.Value.Rows);
            Assert.Equal(
                OperationOutcome.InvalidInput,
                over.Outcome);
            Assert.Null(over.Value);
            Assert.Equal(
                SazExportCodes.TooManyRows,
                Assert.Single(over.Diagnostics).Code);
            Assert.Equal(1, overSource.Pages);
        }

        [Fact]
        public void T14_StoredBytesAcceptTheExactLimitAndRefuseOneMore()
        {
            SazExportBudgets budgets =
                SazExportBudgets.Default with
                {
                    MaxTotalStoredBytes = 100,
                };

            OperationResult<SazExportPlan> at =
                Prepare(
                    new RangeSource(
                        2,
                        storedBytes: 50),
                    budgets);
            OperationResult<SazExportPlan> over =
                Prepare(
                    new RangeSource(
                        2,
                        storedBytes: 51),
                    budgets);

            Assert.True(at.Succeeded);
            Assert.Equal(100, at.Value.StoredBytes);
            Assert.Equal(
                SazExportCodes.TooLarge,
                Assert.Single(over.Diagnostics).Code);
        }

        [Fact]
        public void T14_ArchiveBudgetIncludesFinalizationAndRewrittenHeadersOnce()
        {
            StoredRow row = Row();
            long measured;
            using (var first = new MemoryStream())
            {
                OperationResult<SazExportResult> result =
                    Export(
                        new RangeSource(
                            1,
                            row: row),
                        SazExportBudgets.Default,
                        first);
                Assert.True(result.Succeeded);
                measured = result.Value.Summary.ArchiveBytes;
                Assert.Equal(first.Length, measured);
            }

            SazExportBudgets exact =
                SazExportBudgets.Default with
                {
                    MaxArchiveBytes = measured,
                };
            using var accepted = new MemoryStream();
            using var refused = new MemoryStream();
            OperationResult<SazExportResult> at =
                Export(
                    new RangeSource(1, row: row),
                    exact,
                    accepted);
            OperationResult<SazExportResult> below =
                Export(
                    new RangeSource(1, row: row),
                    exact with
                    {
                        MaxArchiveBytes =
                            measured - 1,
                    },
                    refused);

            Assert.True(at.Succeeded);
            Assert.Equal(
                SazExportCodes.TooLarge,
                below.Value.Code);
            Assert.True(refused.Length <= measured - 1);
        }

        [Fact]
        public void T14_HeadAndBodyMarkersCountTowardWrittenLimits()
        {
            StoredRow row = Row(
                requestHead:
                    "POST / HTTP/1.1\r\n" +
                    "Host: example.test\r\n",
                requestBody:
                    StoredBody.NotRead(
                        10,
                        undecodable: false));
            SazExportBudgets budgets =
                SazExportBudgets.Default with
                {
                    MaxHeadFieldLines = 1,
                };
            using var output = new MemoryStream();
            OperationResult<SazExportResult> result =
                Export(
                    new RangeSource(1, row: row),
                    budgets,
                    output);

            Assert.True(result.Succeeded);
            Assert.Equal(
                1,
                result.Value.Summary.RowsOversized);
            Assert.Equal(
                0,
                result.Value.Summary.Sessions);
        }

        [Fact]
        public void T17_AnalyzeInputCeilingTracksTheCliInputLimit()
        {
            Assert.Equal(
                (long)CliLimits.MaximumInputMib *
                    1024 *
                    1024,
                SazExportOperation
                    .AnalyzeInputCeilingBytes);
        }

        [Fact]
        public async Task T8_ExportReadsEachRowWhileWritingAndReleasesIt()
        {
            const int Rows = 6;
            var bodies = new WeakReference[Rows + 1];
            var writtenAtRead = new long[Rows + 1];
            var released = new bool[Rows + 1];
            using var destination =
                new CountingStream(new MemoryStream());
            var source = new FuncSource(
                Rows,
                id =>
                {
                    writtenAtRead[id] = destination.Written;
                    if (id >= 3)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        released[id - 2] =
                            !bodies[id - 2].IsAlive;
                    }

                    byte[] body = RandomBytes(
                        1 << 20,
                        (int)id);
                    bodies[id] = new WeakReference(body);
                    return Stored(
                        id,
                        Head(),
                        StoredBody.FromBytes(body),
                        OkHead,
                        StoredBody.Empty,
                        body.Length);
                },
                _ => 1 << 20);
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> prepared =
                await operation.PrepareAsync(
                    new(
                        source,
                        SazExportKind.Plain,
                        null,
                        SazExportBudgets.Default),
                    OperationContext.None,
                    CancellationToken.None);
            int readsDuringPrepare = source.Reads;
            Assert.True(prepared.Succeeded);
            OperationResult<SazExportResult> result =
                await operation.ExecuteAsync(
                    new(
                        prepared.Value,
                        new SazExportDestination.ToStream(
                            destination),
                        null),
                    OperationContext.None,
                    CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(
                Rows,
                result.Value.Summary.Sessions);
            Assert.Equal(0, readsDuringPrepare);
            Assert.Equal(Rows, source.Reads);
            for (int id = 2; id <= Rows; id++)
            {
                Assert.True(
                    writtenAtRead[id] > writtenAtRead[id - 1],
                    $"Nothing was written between reading rows {id - 1} and {id}.");
            }

            for (int id = 1; id <= Rows - 2; id++)
            {
                Assert.True(
                    released[id],
                    $"Row {id}'s body was still reachable two rows later.");
            }
        }

        [Fact]
        [Trait("Category", "S2ResourceGate")]
        public void T8_PlainExportAtProductionLimitsStaysUnderTheMemoryCeiling()
        {
            if (!GateEnabled())
            {
                return;
            }

            SazExportSummary summary =
                ExportAtProductionLimits(
                    SazExportKind.Plain,
                    29);

            Assert.Equal(0, summary.RowsOversized);
        }

        [Fact]
        [Trait("Category", "S2ResourceGate")]
        public void T8_SanitizedExportAtProductionLimitsStaysUnderTheMemoryCeiling()
        {
            if (!GateEnabled())
            {
                return;
            }

            ExportAtProductionLimits(
                SazExportKind.Sanitized,
                8);
        }

        [Fact]
        [Trait("Category", "S2ResourceGate")]
        public void T17_EntryCountBelowTheZip64BoundaryWritesClassicEndRecords()
        {
            if (!GateEnabled())
            {
                return;
            }

            const int Sessions = 21_845;
            string path = Temporary("classic");
            try
            {
                OperationResult<SazExportResult> result =
                    ExportToFile(
                        new FuncSource(
                            Sessions,
                            id => Stored(
                                id,
                                Head(),
                                StoredBody.Empty,
                                id == Sessions
                                    ? null
                                    : OkHead,
                                StoredBody.Empty,
                                0)),
                        path);

                Assert.True(result.Succeeded);
                Assert.Equal(
                    65_534,
                    result.Value.Summary.Entries);
                byte[] tail = Tail(path);
                Assert.True(
                    IndexOf(tail, Zip64EndRecord) < 0,
                    "A ZIP64 end-of-central-directory record was written below the boundary.");
                Assert.True(
                    IndexOf(tail, Zip64Locator) < 0,
                    "A ZIP64 locator was written below the boundary.");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        [Trait("Category", "S2ResourceGate")]
        public void T17_EntryCountAtTheZip64BoundaryWritesZip64EndRecords()
        {
            if (!GateEnabled())
            {
                return;
            }

            const int Sessions = 21_845;
            string path = Temporary("zip64");
            try
            {
                OperationResult<SazExportResult> result =
                    ExportToFile(
                        new FuncSource(
                            Sessions,
                            id => Stored(
                                id,
                                Head(),
                                StoredBody.Empty,
                                OkHead,
                                StoredBody.Empty,
                                0)),
                        path);

                Assert.True(result.Succeeded);
                Assert.Equal(
                    65_535,
                    result.Value.Summary.Entries);
                Assert.True(
                    IndexOf(Tail(path), Zip64EndRecord) >= 0,
                    "The ZIP64 end-of-central-directory record was absent.");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        [Trait("Category", "S2ResourceGate")]
        public void T17_ProductionRowLimitArchiveReadsBackThroughBothImporters()
        {
            if (!GateEnabled())
            {
                return;
            }

            string path = Temporary("rows");
            try
            {
                OperationResult<SazExportResult> result =
                    ExportToFile(
                        new FuncSource(
                            SazExportOperation.MaxRows,
                            id => Stored(
                                id,
                                Head(),
                                StoredBody.Empty,
                                OkHead,
                                StoredBody.Empty,
                                0)),
                        path);
                Assert.True(result.Succeeded);
                Assert.Equal(
                    SazExportOperation.MaxRows,
                    result.Value.Summary.Sessions);

                int bounded;
                using (var input = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    bounded = BoundedSazReader.Read(
                            input,
                            new BoundedSazLimits(
                                input.Length,
                                1 << 20,
                                8L << 30,
                                SazExportOperation.MaxRows,
                                BoundedSazReader
                                    .MaxArchiveEntriesCeiling,
                                BoundedSazReader
                                    .MaxCentralDirectoryCeiling),
                            CancellationToken.None)
                        .Records
                        .Count;
                }

                Assert.Equal(
                    SazExportOperation.MaxRows,
                    bounded);
                Assert.Equal(
                    SazExportOperation.MaxRows,
                    FiddlerSazHandler.ImportSazFile(
                            path,
                            CancellationToken.None)
                        .Count);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        [Trait("Category", "S2ResourceGate")]
        public void T17_ArchiveOverTheArchiveBudgetPublishesNothing()
        {
            if (!GateEnabled())
            {
                return;
            }

            byte[] shared = SharedBody();
            string folder = TemporaryFolder("over");
            string path = Path.Join(folder, "capture.saz");
            try
            {
                OperationResult<SazExportResult> result =
                    PublishToTarget(
                        HeavySource(33, 0, shared),
                        SazExportKind.Plain,
                        SazExportBudgets.Default with
                        {
                            MaxTotalStoredBytes = 8L << 30,
                        },
                        path);

                Assert.False(result.Succeeded);
                Assert.Equal(
                    OperationOutcome.InvalidInput,
                    result.Outcome);
                Assert.Equal(
                    SazExportCodes.TooLarge,
                    result.Value.Code);
                Assert.Equal(
                    SazExportPublication.NotPublished,
                    result.Value.Publication);
                Assert.Empty(
                    Directory.GetFileSystemEntries(folder));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        [Trait("Category", "S2ResourceGate")]
        public void T17_LargestArchiveUnderTheBudgetKeepsClassicOffsets()
        {
            if (!GateEnabled())
            {
                return;
            }

            byte[] shared = SharedBody();
            string folder = TemporaryFolder("under");
            string path = Path.Join(folder, "capture.saz");
            try
            {
                // Random bodies do not deflate, so they are stored as they
                // are: 4032 MiB stored lands just under the 4 GiB archive cap.
                OperationResult<SazExportResult> result =
                    PublishToTarget(
                        HeavySource(31, 1, shared),
                        SazExportKind.Plain,
                        SazExportBudgets.Default,
                        path);

                Assert.True(
                    result.Succeeded,
                    $"Export failed: {result.Outcome} {result.Value?.Code} {result.Value?.Summary}");
                Assert.Null(result.Value.Code);
                Assert.Equal(
                    SazExportPublication.Created,
                    result.Value.Publication);
                Assert.Equal(32, result.Value.Summary.Sessions);
                long length = new FileInfo(path).Length;
                Assert.True(
                    length > (4000L << 20),
                    $"The archive was only {length:N0} bytes.");
                Assert.True(
                    length <= SazExportBudgets.Default.MaxArchiveBytes,
                    $"The archive was {length:N0} bytes.");
                AssertClassicCentralDirectory(path, length);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private const string OkHead =
            "HTTP/1.1 200 OK\r\n\r\n";

        private static readonly byte[] Zip64EndRecord =
            { 0x50, 0x4b, 0x06, 0x06 };

        private static readonly byte[] Zip64Locator =
            { 0x50, 0x4b, 0x06, 0x07 };

        private static readonly byte[] EndRecord =
            { 0x50, 0x4b, 0x05, 0x06 };

        private static SazExportSummary ExportAtProductionLimits(
            SazExportKind kind,
            int heavyRows)
        {
            byte[] shared = SharedBody();
            string bigHead = ExactHead(
                checked((int)SazExportBudgets.Default.MaxHeadBytes));
            string manyHead = ManyFieldHead(
                SazExportBudgets.Default.MaxHeadFieldLines);
            var source = new FuncSource(
                SazExportOperation.MaxRows,
                id =>
                {
                    if (id <= heavyRows)
                    {
                        return HeavyRow(id, shared, true);
                    }

                    string head =
                        id == heavyRows + 1
                            ? bigHead
                            : id == heavyRows + 2
                                ? manyHead
                                : Head();
                    return Stored(
                        id,
                        head,
                        StoredBody.Empty,
                        OkHead,
                        StoredBody.Empty,
                        head.Length);
                },
                id => id <= heavyRows
                    ? 2L * shared.Length
                    : 0);
            string path = Temporary("memory-" + kind);
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                (long before, _) = PrivateBytes();
                OperationResult<SazExportResult> result =
                    ExportToFile(source, path, kind);
                (_, long peak) = PrivateBytes();
                long incremental = Math.Max(0, peak - before);

                Assert.True(result.Succeeded);
                Assert.Null(result.Value.Code);
                Assert.Equal(
                    SazExportOperation.MaxRows,
                    result.Value.Summary.Sessions);
                Assert.True(
                    incremental <= 900_000_000,
                    $"Incremental peak private bytes: {incremental:N0}.");
                return result.Value.Summary;
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string ExactHead(int total)
        {
            const string Start =
                "GET http://example.test/ HTTP/1.1";
            const string Host = "example.test";
            int pad =
                total -
                (Start.Length +
                    4 +
                    ("Host".Length + 2 + Host.Length + 2) +
                    ("X-Pad".Length + 4) +
                    EmptyBodyLength.Length);
            string head =
                Start + "\r\n" +
                "Host: " + Host + "\r\n" +
                "X-Pad: " + new string('a', pad) + "\r\n" +
                EmptyBodyLength + "\r\n";
            Assert.Equal(total, head.Length);
            return head;
        }

        private static string ManyFieldHead(int lines)
        {
            var head = new StringBuilder(
                "GET http://example.test/ HTTP/1.1\r\n" +
                "Host: example.test\r\n");
            for (int line = 1; line < lines - 1; line++)
            {
                head.Append("X-")
                    .Append(line)
                    .Append(": v\r\n");
            }

            return head.Append(EmptyBodyLength).Append("\r\n").ToString();
        }

        // Sanitized export replaces framing headers with an authoritative
        // Content-Length, so at-limit fixtures carry one to stay at the limit.
        private const string EmptyBodyLength = "Content-Length: 0\r\n";

        private static void AssertClassicCentralDirectory(
            string path,
            long length)
        {
            byte[] tail = Tail(path);
            Assert.True(IndexOf(tail, Zip64Locator) < 0);
            int end = LastIndexOf(tail, EndRecord);
            Assert.True(end >= 0, "No end-of-central-directory record.");
            uint size = BitConverter.ToUInt32(tail, end + 12);
            uint offset = BitConverter.ToUInt32(tail, end + 16);
            Assert.NotEqual(uint.MaxValue, offset);
            Assert.True((long)offset + size <= length);

            byte[] directory = new byte[size];
            using (var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                input.Seek(offset, SeekOrigin.Begin);
                input.ReadExactly(directory);
            }

            int entries = 0;
            for (int at = 0; at < directory.Length;)
            {
                Assert.Equal(
                    0x02014b50u,
                    BitConverter.ToUInt32(directory, at));
                int name = BitConverter.ToUInt16(directory, at + 28);
                int extra = BitConverter.ToUInt16(directory, at + 30);
                int comment = BitConverter.ToUInt16(directory, at + 32);
                for (int field = at + 46 + name;
                    field < at + 46 + name + extra;)
                {
                    Assert.NotEqual(
                        (ushort)0x0001,
                        BitConverter.ToUInt16(directory, field));
                    field += 4 +
                        BitConverter.ToUInt16(directory, field + 2);
                }

                at += 46 + name + extra + comment;
                entries++;
            }

            Assert.True(entries > 0);
        }

        private static byte[] Tail(string path)
        {
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            int count = (int)Math.Min(input.Length, 1 << 16);
            byte[] tail = new byte[count];
            input.Seek(-count, SeekOrigin.End);
            input.ReadExactly(tail);
            return tail;
        }

        private static int IndexOf(byte[] data, byte[] value) =>
            data.AsSpan().IndexOf(value);

        private static int LastIndexOf(byte[] data, byte[] value) =>
            data.AsSpan().LastIndexOf(value);

        // PROCESS_MEMORY_COUNTERS on x64: PagefileUsage at 56 and PeakPagefileUsage at 64 (private bytes).
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetProcessMemoryInfo(
            IntPtr process,
            [Out] byte[] counters,
            uint size);

        private static (long Current, long Peak) PrivateBytes()
        {
            Assert.Equal(8, IntPtr.Size);
            byte[] counters = new byte[72];
            BitConverter.TryWriteBytes(
                counters.AsSpan(0, 4),
                (uint)counters.Length);
            Assert.True(
                K32GetProcessMemoryInfo(
                    new IntPtr(-1),
                    counters,
                    (uint)counters.Length),
                $"K32GetProcessMemoryInfo failed: {Marshal.GetLastPInvokeError()}.");
            return (
                BitConverter.ToInt64(counters, 56),
                BitConverter.ToInt64(counters, 64));
        }

        private static OperationResult<SazExportResult> ExportToFile(
            SazExportSource source,
            string path,
            SazExportKind kind = SazExportKind.Plain)
        {
            using var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> prepared =
                operation.PrepareAsync(
                        new(
                            source,
                            kind,
                            null,
                            SazExportBudgets.Default),
                        OperationContext.None,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            Assert.True(prepared.Succeeded);
            return operation.ExecuteAsync(
                    new(
                        prepared.Value,
                        new SazExportDestination.ToStream(
                            output),
                        null),
                    OperationContext.None,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        internal static OperationResult<SazExportResult> PublishToTarget(
            SazExportSource source,
            SazExportKind kind,
            SazExportBudgets budgets,
            string path)
        {
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> prepared =
                operation.PrepareAsync(
                        new(
                            source,
                            kind,
                            null,
                            budgets),
                        OperationContext.None,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            Assert.True(prepared.Succeeded);
            using CaptureArchiveTarget target =
                operation.AdmitTargetAsync(
                        path,
                        OperationContext.None,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            return operation.ExecuteAsync(
                    new(
                        prepared.Value,
                        new SazExportDestination.ToTarget(
                            target,
                            null),
                        null),
                    OperationContext.None,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        // Rows 1..heavy carry a maximum request body and a maximum response body; then `requestOnly` rows carry
        // only a maximum request body.
        internal static FuncSource HeavySource(
            int heavy,
            int requestOnly,
            byte[] shared) =>
            new(
                heavy + requestOnly,
                id => HeavyRow(id, shared, id <= heavy),
                id => (id <= heavy ? 2L : 1L) * shared.Length);

        private static StoredRow HeavyRow(
            long id,
            byte[] shared,
            bool response) =>
            Stored(
                id,
                "POST / HTTP/1.1\r\n" +
                    "Host: example.test\r\n" +
                    $"Content-Length: {shared.Length}\r\n\r\n",
                StoredBody.FromBytes(shared),
                response
                    ? "HTTP/1.1 200 OK\r\n" +
                        $"Content-Length: {shared.Length}\r\n\r\n"
                    : null,
                response
                    ? StoredBody.FromBytes(shared)
                    : StoredBody.Empty,
                (response ? 2L : 1L) * shared.Length);

        internal static byte[] SharedBody() =>
            RandomBytes(
                checked((int)SazExportBudgets.Default.MaxBodyBytes),
                1);

        private static byte[] RandomBytes(int length, int seed)
        {
            byte[] bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        private static string Head() =>
            "GET / HTTP/1.1\r\n" +
            "Host: example.test\r\n\r\n";

        private static StoredRow Stored(
            long id,
            string requestHead,
            StoredBody requestBody,
            string responseHead,
            StoredBody responseBody,
            long bytesRead) =>
            new(
                id,
                id,
                StoredRowState.Available,
                bytesRead,
                requestHead,
                requestBody,
                responseHead,
                responseBody,
                "https",
                false,
                ProxyConnectionModel.ToDateTimeString(
                    DateTime.UnixEpoch),
                false,
                ProxyConnectionModel.ToDateTimeString(
                    DateTime.UnixEpoch),
                false,
                "10.0.0.1",
                false);

        private static string TemporaryFolder(string name)
        {
            string folder = Path.Join(
                Path.GetTempPath(),
                $"xmat-s2-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            return folder;
        }

        internal sealed class FuncSource :
            SazExportSource
        {
            private readonly int _count;
            private readonly Func<long, StoredRow> _row;
            private readonly Func<long, long> _storedBytes;

            internal FuncSource(
                int count,
                Func<long, StoredRow> row,
                Func<long, long> storedBytes = null)
                : base(CaptureTextSource.Literal)
            {
                _count = count;
                _row = row;
                _storedBytes = storedBytes ?? (_ => 0);
            }

            internal int Reads { get; private set; }

            internal override long? GetWatermark(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _count == 0
                    ? null
                    : _count;
            }

            internal override SazExportPage ReadPage(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long first =
                    (afterRowId ?? 0) + 1;
                if (first > _count ||
                    first > throughRowId)
                {
                    return new(
                        Array.Empty<SazExportKey>(),
                        afterRowId);
                }

                int take = checked(
                    (int)Math.Min(
                        scan,
                        Math.Min(
                            _count - first + 1,
                            throughRowId - first + 1)));
                var keys =
                    new SazExportKey[take];
                for (int index = 0;
                    index < keys.Length;
                    index++)
                {
                    long id = first + index;
                    keys[index] = new(
                        id,
                        id,
                        _storedBytes(id));
                }

                return new(
                    keys,
                    keys[^1].RowId);
            }

            internal override StoredRow ReadRow(
                SazExportKey key,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Reads++;
                return _row(key.RowId);
            }
        }

        private sealed class CountingStream : Stream
        {
            private readonly Stream _inner;

            internal CountingStream(Stream inner)
            {
                _inner = inner;
            }

            internal long Written { get; private set; }

            public override bool CanRead => _inner.CanRead;

            public override bool CanSeek => _inner.CanSeek;

            public override bool CanWrite => _inner.CanWrite;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();

            public override int Read(
                byte[] buffer,
                int offset,
                int count) =>
                _inner.Read(buffer, offset, count);

            public override long Seek(
                long offset,
                SeekOrigin origin) =>
                _inner.Seek(offset, origin);

            public override void SetLength(long value) =>
                _inner.SetLength(value);

            public override void Write(
                byte[] buffer,
                int offset,
                int count)
            {
                Written += count;
                _inner.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Written += buffer.Length;
                _inner.Write(buffer);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private static OperationResult<SazExportPlan> Prepare(
            RangeSource source,
            SazExportBudgets budgets)
        {
            return new SazExportOperation()
                .PrepareAsync(
                    new(
                        source,
                        SazExportKind.Plain,
                        null,
                        budgets),
                    OperationContext.None,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        private static OperationResult<SazExportResult> Export(
            SazExportSource source,
            SazExportBudgets budgets,
            Stream destination)
        {
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> prepared =
                operation.PrepareAsync(
                        new(
                            source,
                            SazExportKind.Plain,
                            null,
                            budgets),
                        OperationContext.None,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            Assert.True(prepared.Succeeded);
            return operation.ExecuteAsync(
                    new(
                        prepared.Value,
                        new SazExportDestination.ToStream(
                            destination),
                        null),
                    OperationContext.None,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        private static StoredRow Row(
            string requestHead =
                "GET / HTTP/1.1\r\n" +
                "Host: example.test\r\n\r\n",
            StoredBody requestBody = null) =>
            new(
                1,
                1,
                StoredRowState.Available,
                0,
                requestHead,
                requestBody ??
                    StoredBody.Empty,
                "HTTP/1.1 200 OK\r\n\r\n",
                StoredBody.Empty,
                "https",
                false,
                ProxyConnectionModel.ToDateTimeString(
                    DateTime.UnixEpoch),
                false,
                ProxyConnectionModel.ToDateTimeString(
                    DateTime.UnixEpoch),
                false,
                "10.0.0.1",
                false);

        internal static bool GateEnabled() =>
            string.Equals(
                Environment.GetEnvironmentVariable(
                    ResourceGate),
                "1",
                StringComparison.Ordinal);

        private static string Temporary(string name) =>
            Path.Join(
                Path.GetTempPath(),
                $"xmat-s2-{name}-{Guid.NewGuid():N}.saz");

        private sealed class RangeSource :
            SazExportSource
        {
            private readonly int _count;
            private readonly long _storedBytes;
            private readonly StoredRow _row;

            internal RangeSource(
                int count,
                long storedBytes = 0,
                StoredRow row = null)
                : base(CaptureTextSource.Literal)
            {
                _count = count;
                _storedBytes = storedBytes;
                _row = row ?? Row();
            }

            internal int Pages { get; private set; }

            internal override long? GetWatermark(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _count == 0
                    ? null
                    : _count;
            }

            internal override SazExportPage ReadPage(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Pages++;
                long first =
                    (afterRowId ?? 0) + 1;
                if (first > _count ||
                    first > throughRowId)
                {
                    return new(
                        Array.Empty<SazExportKey>(),
                        afterRowId);
                }

                int take = checked(
                    (int)Math.Min(
                        scan,
                        Math.Min(
                            _count - first + 1,
                            throughRowId - first + 1)));
                var keys =
                    new SazExportKey[take];
                for (int index = 0;
                    index < keys.Length;
                    index++)
                {
                    long id = first + index;
                    keys[index] = new(
                        id,
                        id,
                        _storedBytes);
                }

                return new(
                    keys,
                    keys[^1].RowId);
            }

            internal override StoredRow ReadRow(
                SazExportKey key,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new StoredRow(
                    key.RowId,
                    key.RequestNumber,
                    _row.State,
                    _row.BytesRead,
                    _row.RequestHead,
                    _row.RequestBody,
                    _row.ResponseHead,
                    _row.ResponseBody,
                    _row.Scheme,
                    _row.SchemeUnavailable,
                    _row.RequestTimestamp,
                    _row.RequestTimestampUnavailable,
                    _row.ResponseTimestamp,
                    _row.ResponseTimestampUnavailable,
                    _row.ClientAddress,
                    _row.ClientAddressUnavailable);
            }
        }
    }
}
