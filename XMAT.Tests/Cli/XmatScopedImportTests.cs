// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Data.Sqlite;
using XMAT.Operations.Replay;
using XMAT.Tests.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Cli
{
    public class XmatScopedImportTests
    {
        [Theory]
        [InlineData("replay")]
        [InlineData("curl")]
        public async Task ScopedImportedZone_RefusesAMixedPlanBeforeOutputReservation(string command)
        {
            using var directory = new CliTestDirectory();
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "scoped.saz",
                new CapturedExchange("GET", "http://127.0.0.1:9/ordinary"),
                new CapturedExchange(
                    "GET",
                    "http://[fe80::1%0]:8080/scoped",
                    "Host: conflicting.example\r\n"));
            string output = Path.Join(directory.Root, "results");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Join(output, "kept.txt"), "kept");
            var arguments = new List<string> { command, input, "--request", "all" };
            if (command == "curl")
            {
                arguments.Add("--include-sensitive");
            }
            arguments.AddRange(new[] { "--out", "results" });

            XmatRun run = await XmatProcess.RunAsync(directory.Root, arguments.ToArray());

            CliAssert.ExitCode(3, run);
            JsonElement json = run.Json();
            Assert.Equal(new[] { ReplayDestination.ScopedIpv6Code }, run.ErrorCodes());
            Assert.Empty(json.GetProperty("artifacts").EnumerateArray());
            Assert.Equal("notNeeded", json.GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Equal("kept", File.ReadAllText(Path.Join(output, "kept.txt")));
            CliAssert.NoStagingFolders(directory.Root);
            CliAssert.NoLeaks(run, directory);
        }

        [Theory]
        [InlineData("b.test/", "8080")]
        [InlineData("evil.test?.xboxlive.com", "")]
        [InlineData("b.test/x", "")]
        [InlineData("fe80::1%a@[2001:db8::1", "")]
        [InlineData("[::1]:9", "")]
        [InlineData("@b.test", "8080")]
        [InlineData("@b.test", "")]
        [InlineData("[::1]:", "")]
        [InlineData("b.test/.", "")]
        public async Task StoredAuthorityThatChangesDestination_IsMalformedBeforeOutputReservation(
            string host,
            string port)
        {
            using var directory = new CliTestDirectory();
            string input = ReplayCaptures.WriteXmatcap(
                directory.Root,
                "capture.xmatcap",
                ("Device 1", new[] { new CapturedExchange("GET", "http://127.0.0.1:9/item") }));
            SetStoredAuthority(input, host, port);
            string output = Path.Join(directory.Root, "results");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Join(output, "kept.txt"), "kept");

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root,
                "replay",
                input,
                "--request",
                "1",
                "--out",
                "results");

            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { "replay.request.malformed" }, run.ErrorCodes());
            Assert.Empty(run.Json().GetProperty("artifacts").EnumerateArray());
            Assert.Equal("kept", File.ReadAllText(Path.Join(output, "kept.txt")));
            CliAssert.NoStagingFolders(directory.Root);
            CliAssert.NoLeaks(run, directory);
        }

        // A captured target's head is read up to MaxCaptureDestinationChars code units: one of that length is read, here a
        // scoped one that is refused as such. One code unit more, like a stored host beyond the limit, is a malformed request,
        // whatever the stored authority would give. Both are refused before any output is reserved.
        [Theory]
        [InlineData("replay", "head at the limit", ReplayDestination.ScopedIpv6Code)]
        [InlineData("replay", "head beyond the limit", ReplayOperation.RequestMalformedCode)]
        [InlineData("curl", "head beyond the limit", ReplayOperation.RequestMalformedCode)]
        [InlineData("replay", "stored host beyond the limit", ReplayOperation.RequestMalformedCode)]
        public async Task ADestinationIsReadWithinItsLimit_BeforeOutputReservation(
            string command,
            string destination,
            string code)
        {
            using var directory = new CliTestDirectory();
            string input = ReplayCaptures.WriteXmatcap(
                directory.Root,
                "capture.xmatcap",
                ("Device 1", new[] { new CapturedExchange("GET", "http://127.0.0.1:9/item") }));
            int zoneAtTheLimit = ReplayDestination.MaxCaptureDestinationChars - "http://[fe80::1%]/".Length;
            switch (destination)
            {
                case "head at the limit":
                    SetStoredPath(input, "http://[fe80::1%" + new string('z', zoneAtTheLimit) + "]/item");
                    break;
                case "head beyond the limit":
                    SetStoredPath(input, "http://[fe80::1%" + new string('z', zoneAtTheLimit + 1) + "]/item");
                    break;
                default:
                    SetStoredAuthority(
                        input, ReplayCaptures.LongDnsName(ReplayDestination.MaxCaptureDestinationChars + 1), "");
                    break;
            }

            string output = Path.Join(directory.Root, "results");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Join(output, "kept.txt"), "kept");
            var arguments = new List<string> { command, input, "--request", "1" };
            if (command == "curl")
            {
                arguments.Add("--include-sensitive");
            }

            arguments.AddRange(new[] { "--out", "results" });

            XmatRun run = await XmatProcess.RunAsync(directory.Root, arguments.ToArray());

            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { code }, run.ErrorCodes());
            Assert.Empty(run.Json().GetProperty("artifacts").EnumerateArray());
            Assert.Equal("kept", File.ReadAllText(Path.Join(output, "kept.txt")));
            CliAssert.NoStagingFolders(directory.Root);
            CliAssert.NoLeaks(run, directory);
        }

        private static void SetStoredPath(string path, string requestPath)
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE \"{WebServiceCaptureMethod.TableName}\" SET " +
                $"\"{WebServiceCaptureMethod.FieldKey_RequestPath}\" = $path";
            command.Parameters.AddWithValue("$path", requestPath);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        private static void SetStoredAuthority(string path, string host, string port)
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE \"{WebServiceCaptureMethod.TableName}\" SET " +
                $"\"{WebServiceCaptureMethod.FieldKey_RequestHost}\" = $host, " +
                $"\"{WebServiceCaptureMethod.FieldKey_RequestPort}\" = $port";
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$port", port);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
    }
}
