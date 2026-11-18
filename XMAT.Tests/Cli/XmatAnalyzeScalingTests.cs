// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CaptureAnalysisEngine;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;
using XMAT.Tests.Analysis.Rules;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Cli
{
    // xmat analyze over captures of repeated identical calls, the input that makes the polling and repeated-calls
    // rules slow. Each run is a child process in a job object that caps the memory it commits and kills it when the
    // test ends, so a run that would exhaust the machine fails instead; XmatChild stops a run after two minutes. The
    // collection runs alone, so these runs never compete with other tests for memory.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatAnalyzeScalingTests
    {
        private const string BenchmarkVariable = "XMAT_SCALING_BENCHMARKS";
        private const ulong TestMemoryCap = 2UL << 30;
        private const ulong BenchmarkMemoryCap = 4UL << 30;
        private const long Millisecond = TimeSpan.TicksPerMillisecond;

        // The in-process oracles check completed benchmark runs up to this many calls: the rules that they copy need
        // about 90 s and 1.2 GB for 4,000 identical calls.
        private const int LargestOracleCheck = 4_000;

        private readonly ITestOutputHelper _output;

        public XmatAnalyzeScalingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // Within any sane work budget: 240 identical calls 100 ms apart and 48 identical calls to another URI 1 s
        // apart. The run completes and reports the polling and repeated-calls findings that the frozen oracles report
        // for the same calls.
        [Fact]
        public async Task Analyze_IdenticalCallsWithinTheWorkBudget_ReportsTheOracleFindings()
        {
            using var directory = new CliTestDirectory();
            var capture = IdenticalCallsCapture.Write(
                directory.Root,
                new CallGroup(Xuid: 1, FirstCall: 0, Interval: 100 * Millisecond, Calls: 240),
                new CallGroup(Xuid: 2, FirstCall: 50 * Millisecond, Interval: 1000 * Millisecond, Calls: 48));
            (int polling, int repeated) = OracleFindings(capture);

            CappedRun run = await RunCappedAsync(directory, capture.Input, TestMemoryCap);

            Report(
                "240 and 48 identical calls",
                run,
                $"{Outcome(run.Result)}; the oracles report {polling} polling and {repeated} repeated-calls findings");
            CliAssert.ExitCode(0, run.Result);
            JsonElement json = run.Result.Json();
            Assert.Equal("succeeded", json.GetProperty("status").GetString());
            Assert.Equal(capture.CallCount, json.GetProperty("totals").GetProperty("calls").GetInt32());
            Assert.True(polling > 0 && repeated > 0);
            Assert.Equal(polling, Findings(json, RuleRuns.PollingRuleName));
            Assert.Equal(repeated, Findings(json, RuleRuns.RepeatedRuleName));
            string published = Path.Combine(directory.Root, "results");
            Assert.True(File.Exists(Path.Combine(published, "summary.json")));
            CliAssert.NoLeaks(run.Result, directory, published);
            CliAssert.NoStagingFolders(directory.Root);
        }

        // Far beyond any sane work budget: 30,000 identical calls 100 ms apart, 450 million pairs within the polling
        // rule's one-hour window. The run stops with the data-free error analysis.rules.work-limit (exit code 3) and
        // publishes nothing, within the memory cap. The trait marks it red until C9 adds the work budget; the rules
        // before C9 exhaust the cap instead.
        [Fact]
        [Trait("RedUntil", "C9")]
        public async Task Analyze_IdenticalCallsBeyondTheWorkBudget_Returns3AndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            var capture = IdenticalCallsCapture.Write(
                directory.Root,
                new CallGroup(Xuid: 1, FirstCall: 0, Interval: 100 * Millisecond, Calls: 30_000));
            string[] before = directory.Entries();

            CappedRun run = await RunCappedAsync(directory, capture.Input, TestMemoryCap);

            Report("30,000 identical calls", run, Outcome(run.Result));
            CliAssert.ExitCode(3, run.Result);
            JsonElement json = run.Result.Json();
            Assert.Equal("failed", json.GetProperty("status").GetString());
            Assert.Equal(new[] { "analysis.rules.work-limit" }, run.Result.ErrorCodes());
            Assert.Empty(json.GetProperty("artifacts").EnumerateArray());
            Assert.Equal("completed", json.GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Equal(before, directory.Entries());
            CliAssert.NoLeaks(run.Result, directory);
        }

        // Opt-in measurements (set XMAT_SCALING_BENCHMARKS=1), each under a 4 GiB cap: a run either completes, with the
        // findings that the oracles report (checked up to 4,000 calls), or stops at the work limit and publishes
        // nothing. A crash, a run over the cap and a run over two minutes fail. The output shows the outcome, the time
        // and the peak memory of each run.
        [Theory]
        [InlineData(2_000)]
        [InlineData(4_000)]
        [InlineData(30_000)]
        public async Task Benchmark_IdenticalCalls_CompleteWithTheOracleFindingsOrStopAtTheWorkLimit(int calls)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BenchmarkVariable)))
            {
                return;
            }

            using var directory = new CliTestDirectory();
            var capture = IdenticalCallsCapture.Write(
                directory.Root,
                new CallGroup(Xuid: 1, FirstCall: 0, Interval: 100 * Millisecond, Calls: calls));

            CappedRun run = await RunCappedAsync(directory, capture.Input, BenchmarkMemoryCap);

            string results = Path.Combine(directory.Root, "results");
            if (run.Result.ExitCode == 0)
            {
                JsonElement json = run.Result.Json();
                int polling = Findings(json, RuleRuns.PollingRuleName);
                int repeated = Findings(json, RuleRuns.RepeatedRuleName);
                Report(
                    $"{calls} identical calls",
                    run,
                    $"completed with {polling} polling and {repeated} repeated-calls findings");
                Assert.True(File.Exists(Path.Combine(results, "summary.json")));
                if (calls <= LargestOracleCheck)
                {
                    Assert.Equal(OracleFindings(capture), (polling, repeated));
                }

                CliAssert.NoLeaks(run.Result, directory, results);
            }
            else
            {
                Report($"{calls} identical calls", run, Outcome(run.Result));
                CliAssert.ExitCode(3, run.Result);
                Assert.Equal(new[] { "analysis.rules.work-limit" }, run.Result.ErrorCodes());
                Assert.False(Directory.Exists(results));
                CliAssert.NoLeaks(run.Result, directory);
            }
        }

        private static (int Polling, int Repeated) OracleFindings(IdenticalCallsCapture capture)
        {
            LinkedList<ServiceCallItem> calls = RuleRuns.EngineInput(capture.EngineCalls());
            return (RuleRuns.OraclePolling(calls).ViolationCount, RuleRuns.OracleRepeated(calls).ViolationCount);
        }

        // The findings of one rule, over every device, console and service.
        private static int Findings(JsonElement json, string rule) =>
            json.GetProperty("devices").EnumerateArray()
                .SelectMany(device => device.GetProperty("consoles").EnumerateArray())
                .SelectMany(console => console.GetProperty("findings").EnumerateArray())
                .Where(finding => finding.GetProperty("rule").GetString() == rule)
                .Sum(finding => finding.GetProperty("count").GetInt32());

        private static string Outcome(XmatRun run)
        {
            try
            {
                string[] errors = run.ErrorCodes();
                return errors.Length == 0
                    ? $"exit code {run.ExitCode}"
                    : $"exit code {run.ExitCode} ({string.Join(", ", errors)})";
            }
            catch (Exception ex) when (
                ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException)
            {
                return $"exit code {run.ExitCode} without a JSON report";
            }
        }

        private void Report(string scenario, CappedRun run, string outcome) =>
            _output.WriteLine(
                $"{scenario}: {outcome}; {run.Elapsed.TotalSeconds:F1} s, peak memory {run.PeakMemory >> 20} MiB of " +
                $"{run.MemoryCap >> 20} MiB.");

        private static async Task<CappedRun> RunCappedAsync(CliTestDirectory directory, string input, ulong memoryCap)
        {
            string cli = XmatProcess.CliDirectory;
            using var job = new MemoryCappedJob(memoryCap);
            var elapsed = Stopwatch.StartNew();
            using XmatChild child = XmatProcess.Start(cli, directory.Root, "analyze", input, "--out", "results");
            job.Add(child.Id);
            XmatRun result = await child.CompleteAsync();
            return new CappedRun(result, elapsed.Elapsed, job.PeakMemory, memoryCap);
        }

        private sealed record CappedRun(XmatRun Result, TimeSpan Elapsed, ulong PeakMemory, ulong MemoryCap);

        // Calls to one URI, all with the same body, at a fixed interval (in ticks).
        private sealed record CallGroup(int Xuid, long FirstCall, long Interval, int Calls);

        // A Fiddler archive of identical calls to one Xbox Live endpoint, written the way SyntheticCaptures writes one,
        // with its frames in request time order. The canary in the URIs proves that no finding, error or published
        // file echoes them.
        private sealed class IdenticalCallsCapture
        {
            private const string Host = "userpresence.xboxlive.com";
            private const string Body = "{\"activity\":{}}";
            private const ulong BodyHash = 1;
            private static readonly DateTime Start = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
            private static readonly byte[] Response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 11\r\n\r\n{\"ok\":true}");

            private readonly List<(int Xuid, long Ticks)> _calls;

            private IdenticalCallsCapture(string input, List<(int Xuid, long Ticks)> calls)
            {
                Input = input;
                _calls = calls;
            }

            internal string Input { get; }

            internal int CallCount => _calls.Count;

            internal static IdenticalCallsCapture Write(string directory, params CallGroup[] groups)
            {
                var calls = groups
                    .SelectMany(group => Enumerable.Range(0, group.Calls)
                        .Select(call => (group.Xuid, Ticks: group.FirstCall + (call * group.Interval))))
                    .OrderBy(call => call.Ticks)
                    .ToList();
                var requests = groups.Select(group => group.Xuid).Distinct().ToDictionary(xuid => xuid, Request);
                string input = Path.Combine(directory, "capture.saz");
                using (var archive = new ZipArchive(File.Create(input), ZipArchiveMode.Create))
                {
                    for (var index = 0; index < calls.Count; index++)
                    {
                        int frame = index + 1;
                        DateTime started = Start.AddTicks(calls[index].Ticks);
                        WriteEntry(archive, $"raw/{frame:D5}_c.txt", requests[calls[index].Xuid]);
                        WriteEntry(archive, $"raw/{frame:D5}_s.txt", Response);
                        WriteEntry(archive, $"raw/{frame:D5}_m.xml", Metadata(frame, started));
                    }
                }

                return new IdenticalCallsCapture(input, calls);
            }

            // The calls as the analysis engine sees them: the ids are the frame numbers.
            internal List<ServiceCallItem> EngineCalls() =>
                _calls
                    .Select((call, index) => new ServiceCallItem((uint)(index + 1))
                    {
                        Host = Host,
                        Uri = $"https://{Host}{PathOf(call.Xuid)}",
                        ReqBodyHash = BodyHash,
                        ReqBody =
                            BodyHash.ToString(
                                CultureInfo.InvariantCulture),
                        ReqTimeUTC = (ulong)Start.AddTicks(call.Ticks).ToFileTimeUtc(),
                    })
                    .ToList();

            private static string PathOf(int xuid) =>
                $"/users/xuid({xuid})/devices/current/titles/{SyntheticCaptures.Canary}";

            private static byte[] Request(int xuid) => Encoding.ASCII.GetBytes(
                $"POST https://{Host}{PathOf(xuid)} HTTP/1.1\r\n" +
                $"Host: {Host}\r\n" +
                "User-Agent: XboxServicesAPI\r\n" +
                "x-xbl-contract-version: 1\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {Body.Length}\r\n" +
                "\r\n" +
                Body);

            private static byte[] Metadata(int frame, DateTime started) => Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                $"<Session SID=\"{frame}\" BitFlags=\"2100\">" +
                $"<SessionTimers ClientBeginRequest=\"{started:o}\" " +
                $"ClientDoneResponse=\"{started.AddMilliseconds(25):o}\" />" +
                "<SessionFlags><SessionFlag N=\"x-clientip\" V=\"10.0.0.1\" /></SessionFlags>" +
                "</Session>");

            private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
            {
                using Stream stream = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        // A job object that caps the memory its processes commit and kills them when it closes, so neither a run over
        // the cap nor a test that ends early leaves xmat running. A process over the cap fails to allocate; it is not
        // killed.
        private sealed class MemoryCappedJob : IDisposable
        {
            private const uint JobMemoryLimit = 0x200;
            private const uint DieOnUnhandledException = 0x400;
            private const uint ProcessSetQuota = 0x0100;
            private const uint ProcessTerminate = 0x0001;
            private readonly SafeWaitHandle _job;

            internal MemoryCappedJob(ulong memoryCap)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var limits = new RecoveryNativeMethods.JobExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags =
                    RecoveryNativeMethods.JobKillOnClose | JobMemoryLimit | DieOnUnhandledException;
                limits.JobMemoryLimit = (UIntPtr)memoryCap;
                if (_job.IsInvalid ||
                    !SetInformationJobObject(
                        _job,
                        RecoveryNativeMethods.JobExtendedLimitInformationClass,
                        ref limits,
                        Marshal.SizeOf(limits)))
                {
                    var error = new Win32Exception();
                    _job.Dispose();
                    throw error;
                }
            }

            internal ulong PeakMemory
            {
                get
                {
                    if (!QueryInformationJobObject(
                            _job,
                            RecoveryNativeMethods.JobExtendedLimitInformationClass,
                            out RecoveryNativeMethods.JobExtendedLimitInformation information,
                            Marshal.SizeOf<RecoveryNativeMethods.JobExtendedLimitInformation>(),
                            IntPtr.Zero))
                    {
                        throw new Win32Exception();
                    }

                    return information.PeakJobMemoryUsed;
                }
            }

            // xmat starts outside the job and joins it at once, long before it has read its input.
            internal void Add(int processId)
            {
                using SafeProcessHandle process =
                    RecoveryNativeMethods.OpenProcess(ProcessSetQuota | ProcessTerminate, false, processId);
                if (process.IsInvalid || !AssignProcessToJobObject(_job, process))
                {
                    throw new Win32Exception();
                }
            }

            public void Dispose() => _job.Dispose();

            [DllImport(
                "kernel32.dll",
                EntryPoint = "CreateJobObjectW",
                SetLastError = true,
                CharSet = CharSet.Unicode,
                ExactSpelling = true)]
            private static extern SafeWaitHandle CreateJobObject(IntPtr attributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetInformationJobObject(
                SafeWaitHandle job, int informationClass,
                ref RecoveryNativeMethods.JobExtendedLimitInformation information, int length);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool QueryInformationJobObject(
                SafeWaitHandle job, int informationClass,
                out RecoveryNativeMethods.JobExtendedLimitInformation information, int length, IntPtr returnLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool AssignProcessToJobObject(SafeWaitHandle job, SafeProcessHandle process);
        }
    }
}
