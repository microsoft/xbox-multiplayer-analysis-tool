// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using XMAT.Operations.Processes;
using XMAT.Tests.Operations.Analysis;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    // Client-process metadata in SAZ archives: Fiddler's x-ProcessInfo ("name:pid") plus XMAT's start-time and status
    // flags. Metadata is written with XML APIs, an import never claims that a process ran on this PC, and a sanitized
    // export omits all three flags.
    public sealed class SazProcessMetadataTests : IDisposable
    {
        private const string ProcessInfoFlag = "x-ProcessInfo";
        private const string StartTimeFlag = "x-xmat-clientprocess-starttime";
        private const string StatusFlag = "x-xmat-clientprocess-status";
        private static readonly DateTimeOffset Started = new(2026, 2, 1, 8, 30, 15, TimeSpan.Zero);
        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory,
            "HeadlessTestArtifacts",
            "SazProcess-" + Guid.NewGuid().ToString("N"));

        public SazProcessMetadataTests()
        {
            Directory.CreateDirectory(_directory);
        }

        [Theory]
        [InlineData("x-ProcessInfo", "Game.exe:1234", "Game.exe", 1234)]
        [InlineData("X-PROCESSINFO", "game:1234", "game", 1234)]
        [InlineData("x-processinfo", "R&D tool.exe", "R&D tool.exe", 0)]
        [InlineData("x-ProcessInfo", ":4321", "", 4321)]
        public void Import_ReadsProcessInfoInAnyCaseAsImported(string flag, string value, string name, int processId)
        {
            ProxyConnectionModel model = ProcessCaptures.ImportFrame(Flags((flag, value)));

            Assert.Equal(new ClientProcessInfo(name, processId, null, ClientProcessStatus.Imported), model.ClientProcess);
        }

        // As in capture files, only the image name of a path is kept.
        [Theory]
        [InlineData(@"C:\Games\Game.exe:1234")]
        [InlineData("games/Game.exe:1234")]
        public void Import_KeepsOnlyTheImageNameOfAPath(string value)
        {
            ProxyConnectionModel model = ProcessCaptures.ImportFrame(Flags((ProcessInfoFlag, value)));

            Assert.Equal(new ClientProcessInfo("Game.exe", 1234, null, ClientProcessStatus.Imported), model.ClientProcess);
        }

        // Only a valid, positive process id is taken from the end; any other suffix stays in the name, which its colon
        // makes unusable, and an empty value or a zero process id alone names no process.
        [Theory]
        [InlineData("Game.exe:-12")]
        [InlineData("Game.exe:12:34")]
        [InlineData("Game.exe:99999999999")]
        [InlineData("Game.exe:4294967296")]
        [InlineData("Game.exe:12 ")]
        [InlineData(@"C:\Games\Game.exe:abc")]
        [InlineData(@"C:\Games\Game.exe:0")]
        [InlineData(@"C:\Games\Game.exe:-7")]
        [InlineData(":0")]
        [InlineData("")]
        public void Import_UnusableProcessInfoIsNotRecorded(string value)
        {
            ProxyConnectionModel model = ProcessCaptures.ImportFrame(Flags((ProcessInfoFlag, value)));

            Assert.Equal(ClientProcessInfo.NotRecorded, model.ClientProcess);
        }

        // The colon of a drive is not a process-id separator: a path without a process id keeps its image name.
        [Theory]
        [InlineData(@"C:\Game.exe")]
        [InlineData(@"C:\Games\Game.exe")]
        [InlineData("C:/Games/Game.exe")]
        public void Import_KeepsOnlyTheImageNameOfADrivePathWithoutAProcessId(string value)
        {
            ProxyConnectionModel model = ProcessCaptures.ImportFrame(Flags((ProcessInfoFlag, value)));

            Assert.Equal(new ClientProcessInfo("Game.exe", 0, null, ClientProcessStatus.Imported), model.ClientProcess);
        }

        // What an import keeps is what a later export writes: the image name and process id, never the path.
        [Theory]
        [InlineData(@"C:\Games\Game.exe", "Game.exe")]
        [InlineData(@"C:\Games\Game.exe:1234", "Game.exe:1234")]
        [InlineData(@"\\server\share\Game.exe:1234", "Game.exe:1234")]
        public void Import_PersistsNoPathWhenTheSessionIsExportedAgain(string value, string exported)
        {
            ProxyConnectionModel imported = ProcessCaptures.ImportFrame(Flags((ProcessInfoFlag, value)));
            ProxyConnectionModel record = ProcessCaptures.ExportRecord(1);
            record.SetClientProcess(imported.ClientProcess);
            string path = Path.Join(_directory, "reexported.saz");

            SazArchive.Write(path, new[] { record });

            Assert.Equal(exported, FlagsOf(path).GetValueOrDefault(ProcessInfoFlag));
            string all = string.Join("\n", ProcessCaptures.Entries(path).Values.Select(Encoding.UTF8.GetString));
            Assert.DoesNotContain(@"\Games\", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\share\", all, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Import_OverlongProcessNameIsNotRecorded()
        {
            ProxyConnectionModel model = ProcessCaptures.ImportFrame(
                Flags((ProcessInfoFlag, new string('a', 300) + ".exe:12")));

            Assert.Equal(ClientProcessInfo.NotRecorded, model.ClientProcess);
        }

        [Fact]
        public void Import_WithoutProcessInfoIsNotRecorded()
        {
            ProxyConnectionModel model = ProcessCaptures.ImportFrame(Flags());

            Assert.Equal(ClientProcessInfo.NotRecorded, model.ClientProcess);
        }

        [Fact]
        public void Import_ReadsTheStartTimeAndNeverClaimsALocalProcess()
        {
            ProxyConnectionModel model = ProcessCaptures.ImportFrame(Flags(
                (ProcessInfoFlag, "Game.exe:1234"),
                (StartTimeFlag, Started.ToString("o", CultureInfo.InvariantCulture)),
                (StatusFlag, "local")));

            Assert.Equal(
                new ClientProcessInfo("Game.exe", 1234, Started, ClientProcessStatus.Imported),
                model.ClientProcess);
        }

        [Theory]
        [InlineData("remote", ClientProcessStatus.Remote)]
        [InlineData("unresolved", ClientProcessStatus.Unresolved)]
        public void Import_KeepsRemoteAndUnresolvedSessionsWithoutAProcess(string token, ClientProcessStatus status)
        {
            ProxyConnectionModel model = ProcessCaptures.ImportFrame(Flags((StatusFlag, token)));

            Assert.Equal(new ClientProcessInfo(string.Empty, 0, null, status), model.ClientProcess);
        }

        [Fact]
        public void Export_WritesProcessInfoStartTimeAndStatusThatImportReadsBack()
        {
            ProxyConnectionModel record = ProcessCaptures.ExportRecord(1);
            record.SetClientProcess(new ClientProcessInfo("Game.exe", 1234, Started, ClientProcessStatus.Local));
            string path = Path.Join(_directory, "process.saz");

            SazArchive.Write(path, new[] { record });

            Dictionary<string, string> flags = FlagsOf(path);
            Assert.Equal("Game.exe:1234", flags.GetValueOrDefault(ProcessInfoFlag));
            Assert.Equal("local", flags.GetValueOrDefault(StatusFlag));
            Assert.True(flags.TryGetValue(StartTimeFlag, out string start), "The start-time flag is missing.");
            Assert.Equal(Started, DateTimeOffset.Parse(start, CultureInfo.InvariantCulture));
            ProxyConnectionModel imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal(
                new ClientProcessInfo("Game.exe", 1234, Started, ClientProcessStatus.Imported),
                imported.ClientProcess);
        }

        // Every XML-special character stays data: the client address holds all five and a valid image name holds & and '.
        // The metadata parses and each value reads back exactly.
        [Fact]
        public void Export_EscapesProcessNamesAndClientIpsInWellFormedMetadata()
        {
            const string clientIp = "R&D \"lab\" <pc> 'x'";
            const string name = "R&D's tool.exe";
            ProxyConnectionModel record = ProcessCaptures.ExportRecord(1, clientIp);
            record.SetClientProcess(new ClientProcessInfo(name, 77, null, ClientProcessStatus.Local));
            string path = Path.Join(_directory, "escaped.saz");

            SazArchive.Write(path, new[] { record });

            IReadOnlyList<KeyValuePair<string, string>> flags =
                ProcessCaptures.SessionFlags(ProcessCaptures.Entries(path)["raw/1_m.xml"]);
            Assert.Contains(new KeyValuePair<string, string>("x-clientip", clientIp), flags);
            Assert.Contains(new KeyValuePair<string, string>(ProcessInfoFlag, name + ":77"), flags);
            ProxyConnectionModel imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal(clientIp, imported.ClientIP);
        }

        [Fact]
        public void Export_ClientIpCannotInjectProcessFlags()
        {
            const string forged = "10.0.0.9\" /><SessionFlag N=\"x-ProcessInfo\" V=\"forged.exe:66";
            ProxyConnectionModel record = ProcessCaptures.ExportRecord(1, forged);
            string path = Path.Join(_directory, "forged.saz");

            SazArchive.Write(path, new[] { record });

            IReadOnlyList<KeyValuePair<string, string>> flags =
                ProcessCaptures.SessionFlags(ProcessCaptures.Entries(path)["raw/1_m.xml"]);
            Assert.Contains(new KeyValuePair<string, string>("x-clientip", forged), flags);
            Assert.DoesNotContain(flags, flag => IsProcessFlag(flag.Key));
            ProxyConnectionModel imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal(ClientProcessInfo.NotRecorded, imported.ClientProcess);
        }

        [Theory]
        [InlineData("", 4321)]
        [InlineData("Tool.exe", 0)]
        [InlineData("R&D's tool.exe", 77)]
        public void Export_IdentitiesRoundTripAsImported(string name, int processId)
        {
            ProxyConnectionModel record = ProcessCaptures.ExportRecord(1);
            record.SetClientProcess(new ClientProcessInfo(name, processId, null, ClientProcessStatus.Local));
            string path = Path.Join(_directory, "partial.saz");

            SazArchive.Write(path, new[] { record });

            ProxyConnectionModel imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal(
                new ClientProcessInfo(name, processId, null, ClientProcessStatus.Imported),
                imported.ClientProcess);
        }

        [Theory]
        [InlineData(ClientProcessStatus.Remote, "remote")]
        [InlineData(ClientProcessStatus.Unresolved, "unresolved")]
        public void Export_SessionsWithoutAProcessKeepTheirStatus(ClientProcessStatus status, string token)
        {
            ProxyConnectionModel record = ProcessCaptures.ExportRecord(1);
            record.SetClientProcess(new ClientProcessInfo(string.Empty, 0, null, status));
            string path = Path.Join(_directory, "status.saz");

            SazArchive.Write(path, new[] { record });

            Dictionary<string, string> flags = FlagsOf(path);
            Assert.False(flags.ContainsKey(ProcessInfoFlag));
            Assert.Equal(token, flags.GetValueOrDefault(StatusFlag));
            ProxyConnectionModel imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal(new ClientProcessInfo(string.Empty, 0, null, status), imported.ClientProcess);
        }

        [Fact]
        public void Export_SessionWithoutProcessInformationWritesNoProcessFlags()
        {
            ProxyConnectionModel record = ProcessCaptures.ExportRecord(1);
            string path = Path.Join(_directory, "unknown.saz");

            SazArchive.Write(path, new[] { record });

            Assert.DoesNotContain(FlagsOf(path).Keys, IsProcessFlag);
        }

        [Fact]
        public void SanitizedExport_OmitsProcessIdentityAndCountsOneRedactionPerSession()
        {
            ProxyConnectionModel plain = ProcessCaptures.ExportRecord(1);
            ProxyConnectionModel identified = ProcessCaptures.ExportRecord(1);
            identified.SetClientProcess(
                new ClientProcessInfo("Game.exe", 98765, Started, ClientProcessStatus.Local));
            string plainPath = Path.Join(_directory, "plain-sanitized.saz");
            string identifiedPath = Path.Join(_directory, "identified-sanitized.saz");

            SanitizedExportSummary baseline = SazArchive.Write(plainPath, new[] { plain }, sanitized: true);
            SanitizedExportSummary summary = SazArchive.Write(identifiedPath, new[] { identified }, sanitized: true);

            Dictionary<string, byte[]> entries = ProcessCaptures.Entries(identifiedPath);
            string all = string.Join("\n", entries.Values.Select(Encoding.UTF8.GetString));
            Assert.DoesNotContain("Game.exe", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("98765", all, StringComparison.Ordinal);
            Assert.DoesNotContain(
                ProcessCaptures.SessionFlags(entries["raw/1_m.xml"]),
                flag => flag.Key.Contains("process", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(baseline.RedactedValues + 1, summary.RedactedValues);
            ProxyConnectionModel imported = Assert.Single(
                FiddlerSazHandler.ImportSazFile(identifiedPath, CancellationToken.None));
            Assert.Equal(ClientProcessInfo.NotRecorded, imported.ClientProcess);
        }

        // Whenever an ordinary export writes process metadata for a session, a sanitized export omits it and counts
        // the omitted group once; a session without process information has nothing to omit.
        [Theory]
        [InlineData(ClientProcessStatus.Local, true)]
        [InlineData(ClientProcessStatus.Imported, true)]
        [InlineData(ClientProcessStatus.Remote, true)]
        [InlineData(ClientProcessStatus.Unresolved, true)]
        [InlineData(ClientProcessStatus.NotRecorded, false)]
        public void SanitizedExport_CountsEachOmittedProcessMetadataGroupOnce(ClientProcessStatus status, bool written)
        {
            ClientProcessInfo process = status is ClientProcessStatus.Local or ClientProcessStatus.Imported
                ? new ClientProcessInfo("Game.exe", 98765, Started, status)
                : new ClientProcessInfo(string.Empty, 0, null, status);
            ProxyConnectionModel plain = ProcessCaptures.ExportRecord(1);
            ProxyConnectionModel record = ProcessCaptures.ExportRecord(1);
            record.SetClientProcess(process);
            string ordinaryPath = Path.Join(_directory, "ordinary.saz");
            string baselinePath = Path.Join(_directory, "baseline-sanitized.saz");
            string sanitizedPath = Path.Join(_directory, "status-sanitized.saz");
            SazArchive.Write(ordinaryPath, new[] { record });
            Assert.Equal(written, FlagsOf(ordinaryPath).Keys.Any(IsProcessFlag));

            SanitizedExportSummary baseline = SazArchive.Write(baselinePath, new[] { plain }, sanitized: true);
            SanitizedExportSummary summary = SazArchive.Write(sanitizedPath, new[] { record }, sanitized: true);

            Assert.DoesNotContain(FlagsOf(sanitizedPath).Keys, IsProcessFlag);
            Assert.Equal(baseline.RedactedValues + (written ? 1 : 0), summary.RedactedValues);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> Flags(params (string Name, string Value)[] processFlags)
        {
            yield return new KeyValuePair<string, string>("x-clientip", ProcessCaptures.SazClientIp);
            foreach ((string name, string value) in processFlags)
            {
                yield return new KeyValuePair<string, string>(name, value);
            }
        }

        private static Dictionary<string, string> FlagsOf(string path) =>
            ProcessCaptures.SessionFlags(ProcessCaptures.Entries(path)["raw/1_m.xml"])
                .ToDictionary(flag => flag.Key, flag => flag.Value, StringComparer.Ordinal);

        private static bool IsProcessFlag(string flag) =>
            flag.Equals(ProcessInfoFlag, StringComparison.OrdinalIgnoreCase) ||
            flag.Equals(StartTimeFlag, StringComparison.OrdinalIgnoreCase) ||
            flag.Equals(StatusFlag, StringComparison.OrdinalIgnoreCase);
    }
}
