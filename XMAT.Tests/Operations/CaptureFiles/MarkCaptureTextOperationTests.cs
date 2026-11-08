// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using SQLitePCL;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.CaptureFiles;
using XMAT.Tests.Cli;
using XMAT.Tests.Fixtures;

namespace XMAT.Tests.Operations.CaptureFiles
{
    public class MarkCaptureTextOperationTests
    {
        private const long MaxInputBytes =
            512L * 1024 * 1024;
        private const string Capture =
            "capture.xmatcap";

        [Fact]
        public async Task AsStored_CopiesExactTextThenMarksTheCopy()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "source.xmatcap");
            const string Text =
                "If-None-Match: \"v1\"\r\nX-Adjacent: \"\"\r\n";
            UpdateValue(
                input,
                "RequestLineAndHeaders",
                Text);
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.AsStored);
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);
            byte[] sourceBytes =
                File.ReadAllBytes(input);
            operation.Checkpoint = point =>
            {
                if (point == "copy-verified")
                {
                    string copied =
                        Path.Join(
                            output.StagingPath,
                            Capture);
                    Assert.Equal(
                        sourceBytes,
                        ReadAllBytesShared(copied));
                    Assert.Equal(
                        CaptureTextFormat.AmbiguousUserVersion,
                        ReadUserVersion(copied));
                }
            };

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output);

            Assert.True(
                result.Succeeded,
                Describe(result));
            string repaired =
                Path.Join(
                    output.StagingPath,
                    Capture);
            Assert.Equal(
                Text,
                ReadValue(
                    repaired,
                    "RequestLineAndHeaders"));
            Assert.Equal(
                CaptureTextFormat.LiteralUserVersion,
                ReadUserVersion(repaired));
            Assert.Equal(
                0,
                result.Value.Counts.ValuesChanged);
            AssertExclusive(repaired);
            Assert.True(output.TryDiscard());
        }

        [Fact]
        public async Task SourceHashIsCheckedAgainAfterRepair()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "source.xmatcap");
            string changed =
                CreateUnmarked(
                    directory.Root,
                    "changed.xmatcap");
            UpdateValue(
                changed,
                "RequestHost",
                "changed.example");
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.AsStored);
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);
            FieldInfo snapshotField =
                Assert.IsAssignableFrom<FieldInfo>(
                    typeof(SqLtReadonlyDatabase).GetField(
                        "_snapshot",
                        BindingFlags.Instance |
                            BindingFlags.NonPublic));
            FileStream original =
                Assert.IsType<FileStream>(
                    snapshotField.GetValue(
                        plan.Database));
            using var replacement =
                new FileStream(
                    changed,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            operation.Checkpoint = point =>
            {
                if (point != "repair-closed")
                {
                    return;
                }

                // Windows excludes real writers while the source snapshot is held. Swap only the test's guarded stream at
                // the final-hash checkpoint to exercise the source-change refusal without weakening that production share.
                snapshotField.SetValue(
                    plan.Database,
                    replacement);
            };

            OperationResult<MarkCaptureTextResult> result;
            try
            {
                result =
                    await Execute(
                        operation,
                        plan,
                        output);
            }
            finally
            {
                snapshotField.SetValue(
                    plan.Database,
                    original);
            }

            Assert.False(
                result.Succeeded);
            Assert.Equal(
                "analysis.input.unreadable",
                Assert.Single(
                    result.Diagnostics)
                    .Code);
            Assert.True(output.TryDiscard());
        }

        [Fact]
        public async Task CopyUsesTheHeldSourceWhenItsInputAliasIsRetargeted()
        {
            using var directory = new CliTestDirectory();
            string first =
                Path.Join(
                    directory.Root,
                    "first");
            string second =
                Path.Join(
                    directory.Root,
                    "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            string original =
                CreateUnmarked(
                    first,
                    "capture.xmatcap");
            string replacement =
                CreateUnmarked(
                    second,
                    "capture.xmatcap");
            UpdateValue(
                original,
                "RequestHost",
                "held.example");
            UpdateValue(
                replacement,
                "RequestHost",
                "retargeted.example");
            string link =
                Path.Join(
                    directory.Root,
                    "alias");
            Links.Junction(
                link,
                first);
            try
            {
                var operation =
                    new MarkCaptureTextOperation();
                using MarkCaptureTextPlan plan =
                    await Prepare(
                        operation,
                        Path.Join(
                            link,
                            "capture.xmatcap"),
                        CaptureTextInterpretation.AsStored);
                Links.Remove(
                    link);
                Links.Junction(
                    link,
                    second);
                using CliOutputFolder output =
                    CliTestOutput.Reserve(
                        Path.Join(
                            directory.Root,
                            "results"),
                        "run");
                CreateEmpty(output);

                OperationResult<MarkCaptureTextResult> result =
                    await Execute(
                        operation,
                        plan,
                        output);

                Assert.True(
                    result.Succeeded,
                    Describe(
                        result));
                Assert.Equal(
                    "held.example",
                    ReadValue(
                        Path.Join(
                            output.StagingPath,
                            Capture),
                        "RequestHost"));
                Assert.Equal(
                    Convert.ToHexString(
                            SHA256.HashData(
                                File.ReadAllBytes(
                                    original)))
                        .ToLowerInvariant(),
                    result.Value.InputSha256);
                Assert.True(output.TryDiscard());
            }
            finally
            {
                Links.Remove(
                    link);
            }
        }

        [Fact]
        public async Task LegacyOneLayer_DecodesExactlyOnceAndLeavesExtensionsAlone()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "legacy.xmatcap");
            UpdateValue(
                input,
                "RequestHost",
                "a\"\"b\"\"\"\"c");
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.LegacyOneLayer);
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output);

            Assert.True(
                result.Succeeded,
                Describe(result));
            string repaired =
                Path.Join(
                    output.StagingPath,
                    Capture);
            Assert.Equal(
                "a\"b\"\"c",
                ReadValue(
                    repaired,
                    "RequestHost"));
            Assert.True(
                result.Value.Counts.ValuesExamined > 0);
            Assert.True(
                result.Value.Counts.ValuesChanged > 0);
            Assert.True(output.TryDiscard());
        }

        [Fact]
        public async Task LegacyOneLayer_RewritesEveryPinnedColumnAndNoExtension()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "columns.xmatcap");
            const string Encoded =
                "a\"\"b'\u2018\u2019\u201c\u201d";
            using (SqliteConnection connection =
                Open(
                    input,
                    SqliteOpenMode.ReadWrite))
            {
                using SqliteCommand command =
                    connection.CreateCommand();
                command.CommandText =
                    "ALTER TABLE CaptureDevices ADD ExtensionText TEXT;" +
                    "ALTER TABLE CaptureDevices ADD ExtensionInteger INTEGER;" +
                    "UPDATE CaptureDevices SET DeviceName=$device,ExtensionText='ext\"\"keep',ExtensionInteger=42;" +
                    "ALTER TABLE WebProxyConnections ADD ExtensionText TEXT;" +
                    "ALTER TABLE WebProxyConnections ADD ExtensionInteger INTEGER;" +
                    "UPDATE WebProxyConnections SET " +
                    string.Join(
                        ",",
                        new[]
                        {
                            "DeviceName=$device",
                            "RequestTimestamp=$value",
                            "ResponseTimestamp=$value",
                            "RequestScheme=$value",
                            "RequestHost=$value",
                            "RequestPort=$value",
                            "RequestMethod=$value",
                            "RequestPath=$value",
                            "RequestStatus=$value",
                            "RequestLineAndHeaders=$value",
                            "RequestBody=$value",
                            "ResponseLineAndHeaders=$value",
                            "ResponseBody=$value",
                            "ClientIP=$value",
                            "ExtensionText='web\"\"keep'",
                            "ExtensionInteger=43",
                        }) +
                    ";" +
                    "CREATE TABLE NetworkTracePackets(" +
                    "id INTEGER PRIMARY KEY," +
                    "DeviceName TEXT,ProcessId INTEGER,ThreadId INTEGER,Timestamp TEXT,MediaType TEXT," +
                    "StartPacket INTEGER,EndPacket INTEGER,Fragment INTEGER,Send INTEGER,Receive INTEGER,Payload TEXT," +
                    "ExtensionText TEXT,ExtensionInteger INTEGER);" +
                    "INSERT INTO NetworkTracePackets(" +
                    "DeviceName,ProcessId,ThreadId,Timestamp,MediaType,StartPacket,EndPacket,Fragment,Send,Receive,Payload," +
                    "ExtensionText,ExtensionInteger) VALUES(" +
                    "$value,1,2,$value,$value,1,1,0,1,0,$value,'trace\"\"keep',44)";
                command.Parameters.AddWithValue(
                    "$device",
                    "dev\"\"ice");
                command.Parameters.AddWithValue(
                    "$value",
                    Encoded);
                command.ExecuteNonQuery();
            }
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.LegacyOneLayer);
            Assert.Equal(
                new MarkCaptureTextCounts(
                    3,
                    3,
                    19,
                    19),
                plan.Counts);
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output);

            Assert.True(
                result.Succeeded,
                Describe(
                    result));
            Assert.Equal(
                plan.Counts,
                result.Value.Counts);
            string repaired =
                Path.Join(
                    output.StagingPath,
                    Capture);
            using SqliteConnection read =
                Open(
                    repaired,
                    SqliteOpenMode.ReadOnly);
            Assert.Equal(
                "dev\"ice",
                Scalar<string>(
                    read,
                    "SELECT DeviceName FROM CaptureDevices"));
            Assert.Equal(
                Encoded.Replace(
                    "\"\"",
                    "\"",
                    StringComparison.Ordinal),
                Scalar<string>(
                    read,
                    "SELECT RequestHost FROM WebProxyConnections"));
            Assert.Equal(
                Encoded.Replace(
                    "\"\"",
                    "\"",
                    StringComparison.Ordinal),
                Scalar<string>(
                    read,
                    "SELECT Payload FROM NetworkTracePackets"));
            Assert.Equal(
                new[]
                {
                    "ext\"\"keep",
                    "web\"\"keep",
                    "trace\"\"keep",
                },
                new[]
                {
                    Scalar<string>(
                        read,
                        "SELECT ExtensionText FROM CaptureDevices"),
                    Scalar<string>(
                        read,
                        "SELECT ExtensionText FROM WebProxyConnections"),
                    Scalar<string>(
                        read,
                        "SELECT ExtensionText FROM NetworkTracePackets"),
                });
            Assert.Equal(
                new long[]
                {
                    42,
                    43,
                    44,
                },
                new[]
                {
                    Scalar<long>(
                        read,
                        "SELECT ExtensionInteger FROM CaptureDevices"),
                    Scalar<long>(
                        read,
                        "SELECT ExtensionInteger FROM WebProxyConnections"),
                    Scalar<long>(
                        read,
                        "SELECT ExtensionInteger FROM NetworkTracePackets"),
                });
            Assert.Equal(
                1L,
                Scalar<long>(
                    read,
                    "SELECT COUNT(*) FROM WebProxyConnections WHERE DeviceName='dev\"ice'"));
            read.Dispose();
            Assert.True(output.TryDiscard());
        }

        [Fact]
        public async Task Progress_UsesThePinnedPhasesAndUnits()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "progress.xmatcap");
            UpdateValue(
                input,
                "RequestHost",
                new string(
                    'a',
                    200_000) +
                    "\"\"");
            var progress =
                new RecordingProgress();
            var context =
                new OperationContext(
                    progress:
                        progress);
            var operation =
                new MarkCaptureTextOperation();
            OperationResult<MarkCaptureTextPlan> prepared =
                await operation.PrepareAsync(
                    input,
                    CaptureTextInterpretation.LegacyOneLayer,
                    MaxInputBytes,
                    context,
                    CancellationToken.None);
            Assert.True(
                prepared.Succeeded,
                Describe(
                    prepared));
            using MarkCaptureTextPlan plan =
                prepared.Value;
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output,
                    context,
                    CancellationToken.None);

            Assert.True(
                result.Succeeded,
                Describe(
                    result));
            double[] hashing =
                progress.For(
                    "capture-text.hashing");
            double[] copying =
                progress.For(
                    "capture-text.copying");
            Assert.True(
                hashing.Length >
                    1);
            Assert.Equal(
                hashing,
                copying);
            Assert.Equal(
                1d,
                hashing[^1]);
            Assert.All(
                hashing[..^1],
                fraction =>
                    Assert.InRange(
                        fraction,
                        double.Epsilon,
                        1d -
                            double.Epsilon));
            double[] validating =
                progress.For(
                    "capture-text.validating");
            Assert.Equal(
                plan.Counts.ValuesExamined +
                    1,
                validating.LongLength);
            Assert.Equal(
                1d /
                    plan.Counts.ValuesExamined,
                validating[0],
                12);
            Assert.Equal(
                1d,
                validating[^1]);
            double[] rewriting =
                progress.For(
                    "capture-text.rewriting");
            Assert.Equal(
                plan.Columns.Count,
                rewriting.Length);
            Assert.Equal(
                1d /
                    plan.Columns.Count,
                rewriting[0],
                12);
            Assert.Equal(
                1d,
                rewriting[^1]);
            Assert.True(output.TryDiscard());
        }

        [Theory]
        [InlineData("capture-text.hashing")]
        [InlineData("capture-text.validating")]
        [InlineData("capture-text.copying")]
        [InlineData("capture-text.rewriting")]
        public async Task CancellationInEveryProgressPhase_StopsTheOperation(
            string phase)
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "cancel.xmatcap");
            UpdateValue(
                input,
                "RequestHost",
                "a\"\"b");
            byte[] before =
                File.ReadAllBytes(
                    input);
            using var cancellation =
                new CancellationTokenSource();
            var progress =
                new RecordingProgress(
                    report =>
                    {
                        if (report.Phase ==
                            phase)
                        {
                            cancellation.Cancel();
                        }
                    });
            var context =
                new OperationContext(
                    progress:
                        progress);
            var operation =
                new MarkCaptureTextOperation();
            OperationResult<MarkCaptureTextPlan> prepared =
                await operation.PrepareAsync(
                    input,
                    CaptureTextInterpretation.LegacyOneLayer,
                    MaxInputBytes,
                    phase is "capture-text.hashing" or
                        "capture-text.validating"
                        ? context
                        : OperationContext.None,
                    cancellation.Token);
            if (!prepared.Succeeded)
            {
                Assert.Equal(
                    OperationOutcome.Cancelled,
                    prepared.Outcome);
                Assert.Equal(
                    "capture.repair.cancelled",
                    Assert.Single(
                        prepared.Diagnostics)
                        .Code);
                Assert.Equal(
                    before,
                    File.ReadAllBytes(
                        input));
                return;
            }

            using MarkCaptureTextPlan plan =
                prepared.Value;
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);
            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output,
                    context,
                    cancellation.Token);

            Assert.Equal(
                OperationOutcome.Cancelled,
                result.Outcome);
            Assert.Equal(
                "capture.repair.cancelled",
                Assert.Single(
                    result.Diagnostics)
                    .Code);
            if (phase ==
                "capture-text.rewriting")
            {
                Assert.Single(
                    progress.For(
                        phase));
            }
            Assert.Equal(
                before,
                File.ReadAllBytes(
                    input));
            Assert.True(output.TryDiscard());
        }

        [Fact]
        public async Task CancellationAfterTheLastRewriteStopsBeforeTheMarker()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "cancel-before-marker.xmatcap");
            UpdateValue(
                input,
                "RequestHost",
                "a\"\"b");
            byte[] before =
                File.ReadAllBytes(
                    input);
            using var cancellation =
                new CancellationTokenSource();
            var progress =
                new RecordingProgress(
                    report =>
                    {
                        if (report.Phase ==
                                "capture-text.rewriting" &&
                            report.Fraction ==
                                1d)
                        {
                            cancellation.Cancel();
                        }
                    });
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.LegacyOneLayer);
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output,
                    new OperationContext(
                        progress:
                            progress),
                    cancellation.Token);

            Assert.Equal(
                OperationOutcome.Cancelled,
                result.Outcome);
            Assert.Equal(
                "capture.repair.cancelled",
                Assert.Single(
                    result.Diagnostics)
                    .Code);
            string staged =
                Path.Join(
                    output.StagingPath,
                    Capture);
            Assert.Equal(
                CaptureTextFormat.AmbiguousUserVersion,
                ReadUserVersion(
                    staged));
            Assert.Equal(
                "a\"\"b",
                ReadValue(
                    staged,
                    "RequestHost"));
            Assert.Equal(
                before,
                File.ReadAllBytes(
                    input));
            Assert.True(output.TryDiscard());
        }

        [Fact]
        public async Task BothSqliteConnectionsUseAndEnforceTheRepairLengthLimit()
        {
            const int Limit =
                1024 * 1024;
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "limits.xmatcap");
            var operation =
                new MarkCaptureTextOperation();
            OperationResult<MarkCaptureTextPlan> prepared =
                await operation.PrepareAsync(
                    input,
                    CaptureTextInterpretation.AsStored,
                    Limit,
                    OperationContext.None,
                    CancellationToken.None);
            Assert.True(
                prepared.Succeeded,
                Describe(
                    prepared));
            using MarkCaptureTextPlan plan =
                prepared.Value;
            AssertLengthLimit(
                plan.Database.Connection,
                Limit);
            int repairChecks = 0;
            operation.RepairConnectionOpened =
                connection =>
                {
                    repairChecks++;
                    AssertLengthLimit(
                        connection,
                        Limit);
                };
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output);

            Assert.True(
                result.Succeeded,
                Describe(
                    result));
            Assert.Equal(
                1,
                repairChecks);
            Assert.True(output.TryDiscard());
        }

        [Theory]
        [InlineData("odd")]
        [InlineData("blob")]
        [InlineData("integer")]
        [InlineData("real")]
        [InlineData("null")]
        [InlineData("invalid-utf8")]
        public async Task LegacyOneLayer_RefusesValuesOutsideOnePassDomain(
            string problem)
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "invalid.xmatcap");
            bool numericStorage =
                problem is
                    "integer" or
                    "real";
            if (numericStorage)
            {
                // TEXT affinity would convert these values to text. Store them under NUMERIC affinity, then restore the
                // validated XMAT declaration without rewriting the value.
                HostileSqliteSchemas.ReplaceSchemaSql(
                    input,
                    "WebProxyConnections",
                    HostileSqliteSchemas.WebProxyConnectionsDdl.Replace(
                        "RequestHost TEXT",
                        "RequestHost NUMERIC",
                        StringComparison.Ordinal));
            }
            using (var connection =
                Open(
                    input,
                    SqliteOpenMode.ReadWrite))
            {
                using var command =
                    connection.CreateCommand();
                command.CommandText =
                    problem switch
                    {
                        "odd" =>
                            "UPDATE WebProxyConnections SET RequestHost='odd\"'",
                        "blob" =>
                            "UPDATE WebProxyConnections SET RequestHost=X'22'",
                        "integer" =>
                            "UPDATE WebProxyConnections SET RequestHost=7",
                        "real" =>
                            "UPDATE WebProxyConnections SET RequestHost=1.5",
                        "null" =>
                            "UPDATE WebProxyConnections SET RequestHost=NULL",
                        _ =>
                            "UPDATE WebProxyConnections SET RequestHost=CAST(X'80' AS TEXT)",
                    };
                command.ExecuteNonQuery();
            }
            if (numericStorage)
            {
                HostileSqliteSchemas.ReplaceSchemaSql(
                    input,
                    "WebProxyConnections",
                    HostileSqliteSchemas.WebProxyConnectionsDdl);
            }
            var operation =
                new MarkCaptureTextOperation();

            OperationResult<MarkCaptureTextPlan> result =
                await operation.PrepareAsync(
                    input,
                    CaptureTextInterpretation.LegacyOneLayer,
                    MaxInputBytes,
                    OperationContext.None,
                    CancellationToken.None);
            using MarkCaptureTextPlan unexpectedPlan =
                result.Value;

            Assert.False(result.Succeeded);
            Assert.Equal(
                "capture.repair.not-legacy-encoding",
                Assert.Single(
                    result.Diagnostics)
                    .Code);
            Assert.DoesNotContain(
                directory.Entries(),
                entry =>
                    entry.StartsWith(
                        CliOutputFolder.StagingPrefix,
                        StringComparison.Ordinal));
        }

        [Fact]
        public async Task LegacyValidation_CarriesQuoteAndUtf8StateAcrossSlices()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "slices.xmatcap");
            string value =
                new string(
                    'a',
                    (64 * 1024) -
                        1) +
                "\"\"" +
                new string(
                    'b',
                    (64 * 1024) -
                        2) +
                "€tail";
            UpdateValue(
                input,
                "RequestHost",
                value);
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.LegacyOneLayer);
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output);

            Assert.True(
                result.Succeeded,
                Describe(
                    result));
            Assert.Equal(
                value.Replace(
                    "\"\"",
                    "\"",
                    StringComparison.Ordinal),
                ReadValue(
                    Path.Join(
                        output.StagingPath,
                        Capture),
                    "RequestHost"));
            Assert.True(output.TryDiscard());
        }

        [Fact]
        public async Task LegacyValidation_RefusesAnOddQuoteRunAcrossSlices()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "odd-slice.xmatcap");
            UpdateValue(
                input,
                "RequestHost",
                new string(
                    'a',
                    (64 * 1024) -
                        1) +
                    "\"b");
            var operation =
                new MarkCaptureTextOperation();

            OperationResult<MarkCaptureTextPlan> result =
                await operation.PrepareAsync(
                    input,
                    CaptureTextInterpretation.LegacyOneLayer,
                    MaxInputBytes,
                    OperationContext.None,
                    CancellationToken.None);

            Assert.False(
                result.Succeeded);
            Assert.Equal(
                "capture.repair.not-legacy-encoding",
                Assert.Single(
                    result.Diagnostics)
                    .Code);
        }

        [Fact]
        public async Task LegacyValidation_RefusesAPotentialDeviceNameCollision()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "device-collision.xmatcap");
            using (SqliteConnection connection =
                Open(
                    input,
                    SqliteOpenMode.ReadWrite))
            {
                using SqliteCommand update =
                    connection.CreateCommand();
                update.CommandText =
                    "UPDATE CaptureDevices SET DeviceName='a\"\"b';" +
                    "INSERT INTO CaptureDevices(DeviceType,DeviceName,CaptureType) VALUES(0,'a\"b',0)";
                update.ExecuteNonQuery();
            }
            var operation =
                new MarkCaptureTextOperation();

            OperationResult<MarkCaptureTextPlan> result =
                await operation.PrepareAsync(
                    input,
                    CaptureTextInterpretation.LegacyOneLayer,
                    MaxInputBytes,
                    OperationContext.None,
                    CancellationToken.None);

            Assert.False(
                result.Succeeded);
            Assert.Equal(
                "capture.repair.not-legacy-encoding",
                Assert.Single(
                    result.Diagnostics)
                    .Code);
        }

        [Theory]
        [InlineData(
            CaptureTextFormat.LiteralUserVersion,
            "capture.repair.already-marked")]
        [InlineData(2, "capture.format.future")]
        public async Task MarkedAndFutureInputsAreRefusedBeforeOutput(
            int userVersion,
            string code)
        {
            using var directory = new CliTestDirectory();
            string input =
                SyntheticCaptures.WriteXmatcap(
                    directory.Root,
                    "input.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1,
                        UserVersion:
                            checked((uint)userVersion)));
            var operation =
                new MarkCaptureTextOperation();

            OperationResult<MarkCaptureTextPlan> result =
                await operation.PrepareAsync(
                    input,
                    CaptureTextInterpretation.AsStored,
                    MaxInputBytes,
                    OperationContext.None,
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(
                code,
                Assert.Single(
                    result.Diagnostics)
                    .Code);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("nonempty")]
        [InlineData("directory")]
        [InlineData("hardlink")]
        public async Task Execute_RefusesAnythingButTheEmptyReservedFile(
            string entry)
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "input.xmatcap");
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.AsStored);
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            string path =
                Path.Join(
                    output.StagingPath,
                    Capture);
            if (entry == "nonempty")
            {
                File.WriteAllText(
                    path,
                    "not empty");
            }
            else if (entry == "directory")
            {
                Directory.CreateDirectory(path);
            }
            else if (entry == "hardlink")
            {
                CreateEmpty(output);
                Assert.True(
                    NativeMethods.CreateHardLinkW(
                        Path.Join(
                            output.StagingPath,
                            "other.xmatcap"),
                        path,
                        IntPtr.Zero),
                    $"{Marshal.GetLastWin32Error()}");
            }

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output);

            Assert.False(result.Succeeded);
            Assert.Equal(
                MarkCaptureTextOperation.OutputInvalidCode,
                Assert.Single(
                    result.Diagnostics)
                    .Code);
            if (entry == "nonempty")
            {
                Assert.Equal(
                    "not empty",
                    File.ReadAllText(path));
            }
            Assert.True(output.TryDiscard());
        }

        [SymbolicLinkFact]
        public async Task Execute_RefusesAFileSymbolicLinkWithoutFollowingIt()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "input.xmatcap");
            string target =
                Path.Join(
                    directory.Root,
                    "target.xmatcap");
            File.WriteAllBytes(
                target,
                Array.Empty<byte>());
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.AsStored);
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            string link =
                Path.Join(
                    output.StagingPath,
                    Capture);
            _ = File.CreateSymbolicLink(
                link,
                target);

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output);

            Assert.False(result.Succeeded);
            Assert.Equal(
                MarkCaptureTextOperation.OutputInvalidCode,
                Assert.Single(
                    result.Diagnostics)
                    .Code);
            Assert.Equal(
                0,
                new FileInfo(
                    target)
                    .Length);
            Assert.True(output.TryDiscard());
        }

        [Theory]
        [InlineData("output-opened", "delete")]
        [InlineData("output-opened", "move-out")]
        [InlineData("output-opened", "replace")]
        [InlineData("repair-closed", "delete")]
        [InlineData("repair-closed", "move-out")]
        [InlineData("repair-closed", "replace")]
        public async Task Guard_BlocksDeletionRenameAndPosixReplacementAtBothCheckpoints(
            string checkpoint,
            string action)
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "input.xmatcap");
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.AsStored);
            using CliOutputFolder output =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "results"),
                    "run");
            CreateEmpty(output);
            string target =
                Path.Join(
                    output.StagingPath,
                    Capture);
            string outside =
                Path.Join(
                    directory.Root,
                    "outside");
            Directory.CreateDirectory(outside);
            string sibling =
                Path.Join(
                    output.StagingPath,
                    "replacement.xmatcap");
            File.WriteAllText(
                sibling,
                "replacement");
            Exception pathFailure = null;
            int? replaceStatus = null;
            operation.Checkpoint = point =>
            {
                if (point != checkpoint)
                {
                    return;
                }

                if (action == "delete")
                {
                    pathFailure =
                        Record.Exception(
                            () => File.Delete(target));
                }
                else if (action == "move-out")
                {
                    pathFailure =
                        Record.Exception(
                            () => File.Move(
                                target,
                                Path.Join(
                                    outside,
                                    Capture)));
                }
                else
                {
                    replaceStatus =
                        PosixReplace(
                            sibling,
                            Capture);
                }
            };

            OperationResult<MarkCaptureTextResult> result =
                await Execute(
                    operation,
                    plan,
                    output);

            Assert.True(
                result.Succeeded,
                Describe(result));
            if (action == "replace")
            {
                Assert.NotNull(
                    replaceStatus);
                Assert.True(
                    replaceStatus.Value < 0,
                    $"NtSetInformationFile returned {replaceStatus}.");
            }
            else
            {
                Assert.True(
                    pathFailure is IOException or
                        UnauthorizedAccessException,
                    pathFailure?.GetType().FullName);
            }
            Assert.True(output.TryDiscard());
        }

        [Fact]
        public async Task APlanCanBeExecutedOnlyOnce()
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "input.xmatcap");
            var operation =
                new MarkCaptureTextOperation();
            using MarkCaptureTextPlan plan =
                await Prepare(
                    operation,
                    input,
                    CaptureTextInterpretation.AsStored);
            using CliOutputFolder first =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "first"),
                    "one");
            CreateEmpty(first);
            Assert.True(
                (await Execute(
                    operation,
                    plan,
                    first))
                .Succeeded);
            Assert.True(first.TryDiscard());
            using CliOutputFolder second =
                CliTestOutput.Reserve(
                    Path.Join(
                        directory.Root,
                        "second"),
                    "two");
            CreateEmpty(second);

            OperationResult<MarkCaptureTextResult> again =
                await Execute(
                    operation,
                    plan,
                    second);

            Assert.False(again.Succeeded);
            Assert.Equal(
                MarkCaptureTextOperation.PlanUsedCode,
                Assert.Single(
                    again.Diagnostics)
                    .Code);
            Assert.True(second.TryDiscard());
        }

        private static string CreateUnmarked(
            string directory,
            string fileName) =>
            SyntheticCaptures.WriteXmatcap(
                directory,
                fileName,
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 1,
                    UserVersion:
                        CaptureTextFormat.AmbiguousUserVersion));

        private static async Task<MarkCaptureTextPlan> Prepare(
            MarkCaptureTextOperation operation,
            string input,
            CaptureTextInterpretation interpretation)
        {
            OperationResult<MarkCaptureTextPlan> result =
                await operation.PrepareAsync(
                    input,
                    interpretation,
                    MaxInputBytes,
                    OperationContext.None,
                    CancellationToken.None);
            Assert.True(
                result.Succeeded,
                Describe(result));
            return result.Value;
        }

        private static async Task<OperationResult<MarkCaptureTextResult>> Execute(
            MarkCaptureTextOperation operation,
            MarkCaptureTextPlan plan,
            CliOutputFolder output)
            =>
            await Execute(
                operation,
                plan,
                output,
                OperationContext.None,
                CancellationToken.None);

        private static async Task<OperationResult<MarkCaptureTextResult>> Execute(
            MarkCaptureTextOperation operation,
            MarkCaptureTextPlan plan,
            CliOutputFolder output,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            using var markOutput =
                new MarkCaptureTextOutput(
                    output.DuplicateStagingHandle(),
                    Capture);
            return await operation.ExecuteAsync(
                new MarkCaptureTextRequest(
                    plan,
                    markOutput),
                context,
                cancellationToken);
        }

        private static void AssertLengthLimit(
            SqliteConnection connection,
            int limit)
        {
            Assert.Equal(
                limit,
                raw.sqlite3_limit(
                    connection.Handle,
                    raw.SQLITE_LIMIT_LENGTH,
                    -1));
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT length(zeroblob($length))";
            command.Parameters.AddWithValue(
                "$length",
                limit);
            Assert.Equal(
                limit,
                (long)command.ExecuteScalar());
            command.Parameters["$length"].Value =
                limit +
                1;
            SqliteException exception =
                Assert.Throws<SqliteException>(
                    () =>
                        command.ExecuteScalar());
            Assert.Equal(
                raw.SQLITE_TOOBIG,
                exception.SqliteErrorCode);
        }

        private static void CreateEmpty(
            CliOutputFolder output)
        {
            using (output.CreateFile(Capture))
            {
            }
        }

        private static void UpdateValue(
            string path,
            string column,
            object value)
        {
            using var connection =
                Open(
                    path,
                    SqliteOpenMode.ReadWrite);
            using var command =
                connection.CreateCommand();
            command.CommandText =
                $"UPDATE WebProxyConnections SET \"{column}\"=$value";
            command.Parameters.AddWithValue(
                "$value",
                value);
            command.ExecuteNonQuery();
        }

        private static string ReadValue(
            string path,
            string column)
        {
            using var connection =
                Open(
                    path,
                    SqliteOpenMode.ReadOnly);
            using var command =
                connection.CreateCommand();
            command.CommandText =
                $"SELECT \"{column}\" FROM WebProxyConnections";
            return command.ExecuteScalar() as string;
        }

        private static T Scalar<T>(
            SqliteConnection connection,
            string sql)
        {
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                sql;
            return (T)command.ExecuteScalar();
        }

        private static byte[] ReadAllBytesShared(
            string path)
        {
            using var stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
            var bytes =
                new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static int ReadUserVersion(
            string path)
        {
            using var connection =
                Open(
                    path,
                    SqliteOpenMode.ReadOnly);
            using var command =
                connection.CreateCommand();
            command.CommandText =
                "PRAGMA user_version";
            return Convert.ToInt32(
                command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static SqliteConnection Open(
            string path,
            SqliteOpenMode mode)
        {
            var connection =
                new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = mode,
                        Pooling = false,
                    }.ToString());
            connection.Open();
            return connection;
        }

        private static void AssertExclusive(
            string path)
        {
            Exception failure = null;
            for (int attempt = 1;
                attempt <= 20;
                attempt++)
            {
                try
                {
                    using var stream =
                        new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.ReadWrite,
                            FileShare.None);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or
                        UnauthorizedAccessException)
                {
                    failure = exception;
                    if (attempt < 20)
                    {
                        Thread.Sleep(100);
                    }
                }
            }

            throw failure;
        }

        private static string Describe<T>(
            OperationResult<T> result) =>
            string.Join(
                ", ",
                result.Diagnostics.Select(
                    diagnostic =>
                        diagnostic.Code +
                        ": " +
                        diagnostic.Message));

        private sealed class RecordingProgress(
            Action<OperationProgress> report = null) :
            IProgress<OperationProgress>
        {
            private readonly List<OperationProgress> _reports =
                new();

            public void Report(
                OperationProgress value)
            {
                _reports.Add(
                    value);
                report?.Invoke(
                    value);
            }

            internal double[] For(
                string phase) =>
                _reports
                    .Where(
                        item =>
                            item.Phase ==
                            phase)
                    .Select(
                        item =>
                            item.Fraction ??
                            double.NaN)
                    .ToArray();
        }

        private static int PosixReplace(
            string source,
            string targetName)
        {
            const uint Delete = 0x00010000;
            const uint ShareAll = 0x7;
            const uint OpenExisting = 3;
            const int FileRenameInformationEx = 65;
            const uint ReplaceIfExists = 0x1;
            const uint PosixSemantics = 0x2;
            using SafeFileHandle handle =
                NativeMethods.CreateFileW(
                    source,
                    Delete,
                    ShareAll,
                    IntPtr.Zero,
                    OpenExisting,
                    0,
                    IntPtr.Zero);
            Assert.False(
                handle.IsInvalid);
            byte[] name =
                Encoding.Unicode.GetBytes(
                    targetName);
            int header =
                (int)Marshal.OffsetOf<FileRenameInformation>(
                    nameof(FileRenameInformation.FileName));
            IntPtr buffer =
                Marshal.AllocHGlobal(
                    header +
                    name.Length +
                    sizeof(char));
            try
            {
                for (int index = 0;
                    index < header +
                        name.Length +
                        sizeof(char);
                    index++)
                {
                    Marshal.WriteByte(
                        buffer,
                        index,
                        0);
                }
                Marshal.WriteInt32(
                    buffer,
                    (int)Marshal.OffsetOf<FileRenameInformation>(
                        nameof(FileRenameInformation.Flags)),
                    checked(
                        (int)(ReplaceIfExists |
                            PosixSemantics)));
                Marshal.WriteIntPtr(
                    buffer,
                    (int)Marshal.OffsetOf<FileRenameInformation>(
                        nameof(FileRenameInformation.RootDirectory)),
                    IntPtr.Zero);
                Marshal.WriteInt32(
                    buffer,
                    (int)Marshal.OffsetOf<FileRenameInformation>(
                        nameof(FileRenameInformation.FileNameLength)),
                    name.Length);
                Marshal.Copy(
                    name,
                    0,
                    buffer +
                        header,
                    name.Length);
                return NativeMethods.NtSetInformationFile(
                    handle,
                    out IoStatusBlock _,
                    buffer,
                    checked(
                        (uint)(header +
                            name.Length +
                            sizeof(char))),
                    FileRenameInformationEx);
            }
            finally
            {
                Marshal.FreeHGlobal(
                    buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            internal IntPtr Status;
            internal IntPtr Information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileRenameInformation
        {
            internal uint Flags;
            internal IntPtr RootDirectory;
            internal uint FileNameLength;
            internal char FileName;
        }

        private static class NativeMethods
        {
            [DllImport(
                "kernel32.dll",
                CharSet = CharSet.Unicode,
                SetLastError = true,
                ExactSpelling = true)]
            [DefaultDllImportSearchPaths(
                DllImportSearchPath.System32)]
            internal static extern SafeFileHandle CreateFileW(
                string name,
                uint access,
                uint share,
                IntPtr security,
                uint disposition,
                uint flags,
                IntPtr template);

            [DllImport(
                "kernel32.dll",
                CharSet = CharSet.Unicode,
                SetLastError = true,
                ExactSpelling = true)]
            [DefaultDllImportSearchPaths(
                DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CreateHardLinkW(
                string fileName,
                string existingFileName,
                IntPtr securityAttributes);

            [DllImport(
                "ntdll.dll",
                ExactSpelling = true)]
            [DefaultDllImportSearchPaths(
                DllImportSearchPath.System32)]
            internal static extern int NtSetInformationFile(
                SafeFileHandle file,
                out IoStatusBlock ioStatusBlock,
                IntPtr information,
                uint length,
                int informationClass);
        }
    }
}
