// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
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

        [Theory]
        [InlineData("odd")]
        [InlineData("blob")]
        [InlineData("invalid-utf8")]
        public async Task LegacyOneLayer_RefusesValuesOutsideOnePassDomain(
            string problem)
        {
            using var directory = new CliTestDirectory();
            string input =
                CreateUnmarked(
                    directory.Root,
                    "invalid.xmatcap");
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
                        _ =>
                            "UPDATE WebProxyConnections SET RequestHost=CAST(X'80' AS TEXT)",
                    };
                command.ExecuteNonQuery();
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

        [Fact]
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
        {
            using var markOutput =
                new MarkCaptureTextOutput(
                    output.DuplicateStagingHandle(),
                    Capture);
            return await operation.ExecuteAsync(
                new MarkCaptureTextRequest(
                    plan,
                    markOutput),
                OperationContext.None,
                CancellationToken.None);
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
