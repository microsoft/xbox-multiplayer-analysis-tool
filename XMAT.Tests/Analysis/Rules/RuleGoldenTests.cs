// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaptureAnalysisEngine;
using Xunit.Abstractions;
using XMAT.Tests.Proxy;

namespace XMAT.Tests.Analysis.Rules
{
    // The B1 envelope goldens: the product polling and repeated-calls rules must report exactly what they reported in
    // C7 production (04bbb98), compared as the SHA-256 of their canonical findings. The opt-in generator writes the
    // canonical findings of every envelope and capped workload and compares them with the pinned C7 goldens, and those
    // of a capped workload with the oracles' too. One hour of polling every second takes the C7 rules about a minute,
    // so the class runs alone.
    [Collection(IsolatedProxyTests.Name)]
    public class RuleGoldenTests
    {
        private const string GoldensVariable = "XMAT_RULE_GOLDENS_OUT";
        private const string WorkloadsVariable = "XMAT_RULE_GOLDENS_WORKLOADS";

        // The goldens.tsv that the generator wrote on C7 production (04bbb98, Release). The poll1s-1key-1h counts are
        // the ones that xmat analyze reported for the same capture on C7 production. A workload without lines here has
        // no C7 golden pinned yet.
        private static readonly (string Workload, string Rule, int Findings, string Sha256)[] C7Goldens =
        {
            ("ident-4000", "polling", 0,
                "be2d08531f545090535599a4468ed8376db8785189f5353abff5e8e91f54ade7"),
            ("ident-4000", "repeated-calls", 0,
                "27927ce81fa7fbf0ebf968eec2cfc17ff43d67e091209cd5545a6c849736aee3"),
            ("poll100-4000", "polling", 974,
                "3d6ddcf3bbdf4b444eeb993b224908691d4e7dae2a3de90245fe69960d2ce8b5"),
            ("poll100-4000", "repeated-calls", 80,
                "a9bc6b65cbd15e643e5ca893cddeee47de43e157180e711f10c7c5baab240ab2"),
            ("poll1s-1key-1h", "polling", 873,
                "202352baeb6feb13f2d9957d99a875a41e9bf3cb577eacbfdede3853576b313c"),
            ("poll1s-1key-1h", "repeated-calls", 720,
                "b9805a94630b9e870122625649e25817bfd4483bed59167884af5530686ea1df"),
            ("poll2s-3keys-1h", "polling", 1311,
                "b47a192fd0385dd8eac57afe1c01a6a1f7c61bd1fdc23df6abbd10ebd0ef2fa5"),
            ("poll2s-3keys-1h", "repeated-calls", 1800,
                "3f54929d542d6bbb0aa6de401439151d8d92b279b17924e14fc499e85ab5aa93"),
            ("poll1s-3keys-1h", "polling", 2619,
                "d47dafa29bfc605b1bfcc7b1eb1fbdfbfd3718eec4e93b815006712a3d566838"),
            ("poll1s-3keys-1h", "repeated-calls", 2160,
                "7cb447d75b8166ca913301c2c964d9f07ac79199e1e61a2bc8059fb075d241cc"),
            ("poll1s-1key-30m-swap900", "polling", 703,
                "16145e04b666ca29194b3f2d72f53489e08f9a8b411e4afa3391d592778406dc"),
            ("poll1s-1key-30m-swap900", "repeated-calls", 360,
                "6cb7321bc0645792db2db96d7e2b3428fdd9f8ff76b796969361b3144e239627"),
        };

        private static readonly UTF8Encoding Utf8 = new(false);

        private readonly ITestOutputHelper _output;

        public RuleGoldenTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // 3,600 calls of one key, one every second for an hour: the product rules must complete and report the C7
        // findings exactly.
        [Fact]
        public void OneKeyPollingEverySecondForAnHour_MatchesTheC7Goldens()
        {
            LinkedList<ServiceCallItem> calls =
                RuleRuns.EngineInput(EnvelopeWorkload.Find("poll1s-1key-1h").Calls());

            RuleResult polling = RuleRuns.ProductPolling(calls);
            RuleResult repeated = RuleRuns.ProductRepeated(calls);

            string pollingSha256 = CanonicalFindings.Sha256(CanonicalFindings.ToJson(polling));
            string repeatedSha256 = CanonicalFindings.Sha256(CanonicalFindings.ToJson(repeated));
            _output.WriteLine($"polling: {polling.ViolationCount} findings, sha256 {pollingSha256}");
            _output.WriteLine($"repeated-calls: {repeated.ViolationCount} findings, sha256 {repeatedSha256}");
            var pollingGolden = C7Golden("poll1s-1key-1h", "polling");
            var repeatedGolden = C7Golden("poll1s-1key-1h", "repeated-calls");
            Assert.Equal(3600, calls.Count);
            Assert.Equal(pollingGolden.Findings, polling.ViolationCount);
            Assert.Equal(repeatedGolden.Findings, repeated.ViolationCount);
            Assert.Equal(pollingGolden.Sha256, pollingSha256);
            Assert.Equal(repeatedGolden.Sha256, repeatedSha256);
        }

        // Opt-in (set XMAT_RULE_GOLDENS_OUT to a folder, and XMAT_RULE_GOLDENS_WORKLOADS to a comma-separated list of
        // workload names to run only those): writes <workload>.<rule>.json with the canonical findings of the product
        // rules for each workload, goldens.tsv with their finding counts and SHA-256 (the same as the files'), and
        // timings.tsv. The test fails, listing them, when a rule fails, when its findings differ from a pinned C7
        // golden, or, for a capped workload, when they differ from the oracle's, which are then written to
        // <workload>.<rule>.oracle.json. Findings without a pinned C7 golden are only reported.
        [Fact]
        public void WriteEnvelopeGoldens_WhenRequested()
        {
            string folder = Environment.GetEnvironmentVariable(GoldensVariable);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            string[] selected = (Environment.GetEnvironmentVariable(WorkloadsVariable) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<EnvelopeWorkload> workloads = EnvelopeWorkload.All.Concat(EnvelopeWorkload.Capped)
                .Where(workload => selected.Length == 0 || selected.Contains(workload.Name))
                .ToList();
            var failures = selected
                .Where(name => !workloads.Any(workload => workload.Name == name))
                .Select(name => $"{name}: no such workload")
                .ToList();
            Directory.CreateDirectory(folder);
            var goldens = new StringBuilder("workload\tcalls\trule\tfindings\tsha256\n");
            var timings = new StringBuilder("workload\trule\tmilliseconds\n");
            var rules = new (string Name, Func<LinkedList<ServiceCallItem>, RuleResult> Run,
                Func<LinkedList<ServiceCallItem>, RuleResult> Oracle)[]
            {
                ("polling", calls => RuleRuns.ProductPolling(calls), calls => RuleRuns.OraclePolling(calls)),
                ("repeated-calls", calls => RuleRuns.ProductRepeated(calls), calls => RuleRuns.OracleRepeated(calls)),
            };
            try
            {
                foreach (EnvelopeWorkload workload in workloads)
                {
                    LinkedList<ServiceCallItem> calls = RuleRuns.EngineInput(workload.Calls());
                    foreach (var (name, run, oracle) in rules)
                    {
                        string json = null;
                        var elapsed = Stopwatch.StartNew();
                        try
                        {
                            RuleResult result = run(calls);
                            elapsed.Stop();
                            json = CanonicalFindings.ToJson(result);
                            string sha256 = CanonicalFindings.Sha256(json);
                            File.WriteAllText(Path.Combine(folder, $"{workload.Name}.{name}.json"), json, Utf8);
                            goldens.Append(
                                $"{workload.Name}\t{calls.Count}\t{name}\t{result.ViolationCount}\t{sha256}\n");
                            var golden = C7Golden(workload.Name, name);
                            if (golden.Sha256 == null)
                            {
                                _output.WriteLine($"{workload.Name} {name}: no C7 golden pinned");
                            }
                            else if (result.ViolationCount != golden.Findings || sha256 != golden.Sha256)
                            {
                                failures.Add(
                                    $"{workload.Name} {name}: {result.ViolationCount} findings, sha256 {sha256}; " +
                                    $"C7 {golden.Findings} findings, sha256 {golden.Sha256}");
                            }
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            elapsed.Stop();
                            failures.Add($"{workload.Name} {name}: {ex.GetType().FullName}");
                            goldens.Append(
                                $"{workload.Name}\t{calls.Count}\t{name}\tfailed\t{ex.GetType().FullName}\n");
                        }

                        timings.Append($"{workload.Name}\t{name}\t{elapsed.ElapsedMilliseconds}\n");
                        _output.WriteLine($"{workload.Name} {name}: {elapsed.ElapsedMilliseconds} ms");
                        if (json != null && EnvelopeWorkload.Capped.Contains(workload))
                        {
                            string oracleJson = CanonicalFindings.ToJson(oracle(calls));
                            if (oracleJson != json)
                            {
                                File.WriteAllText(
                                    Path.Combine(folder, $"{workload.Name}.{name}.oracle.json"),
                                    oracleJson,
                                    Utf8);
                                failures.Add(
                                    $"{workload.Name} {name}: sha256 {CanonicalFindings.Sha256(json)}; " +
                                    $"oracle sha256 {CanonicalFindings.Sha256(oracleJson)}");
                            }
                        }
                    }
                }
            }
            finally
            {
                File.WriteAllText(Path.Combine(folder, "goldens.tsv"), goldens.ToString(), Utf8);
                File.WriteAllText(Path.Combine(folder, "timings.tsv"), timings.ToString(), Utf8);
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        // The pinned C7 golden of a workload's rule, or a default (null SHA-256) when none is pinned.
        private static (string Workload, string Rule, int Findings, string Sha256) C7Golden(
            string workload,
            string rule) =>
            C7Goldens.SingleOrDefault(golden => golden.Workload == workload && golden.Rule == rule);
    }

    // The B1 envelope workloads as the analysis engine sees them, matching files\scripts\make-polling-envelope-saz.py:
    // one endpoint and the same body for every call; key k calls xuid(10001 + k), first at k * 37 ms and then every
    // interval; the ids are the frame numbers, which follow request time (ties keep the key order). An interval of 0
    // makes every call of a key identical, timestamp included. SwappedId exchanges the ids of the calls with ids
    // SwappedId and SwappedId + 1, so that one pair of frame numbers runs against request time.
    internal sealed record EnvelopeWorkload(string Name, int Keys, int CallsPerKey, int IntervalMs, int SwappedId = 0)
    {
        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static IReadOnlyList<EnvelopeWorkload> All { get; } = new EnvelopeWorkload[]
        {
            new("ident-4000", 1, 4000, 0),
            new("poll100-4000", 1, 4000, 100),
            new("poll1s-1key-1h", 1, 3600, 1000),
            new("poll2s-3keys-1h", 3, 1800, 2000),
            new("poll1s-3keys-1h", 3, 3600, 1000),
        };

        // Workloads small enough for the oracles, which the generator also compares with them. In
        // poll1s-1key-30m-swap900 the polling calls' ids rise except at one pair, which C7's subset check stops at.
        internal static IReadOnlyList<EnvelopeWorkload> Capped { get; } = new EnvelopeWorkload[]
        {
            new("poll1s-1key-30m-swap900", 1, 1800, 1000, SwappedId: 900),
        };

        internal static EnvelopeWorkload Find(string name) =>
            All.Concat(Capped).Single(workload => workload.Name == name);

        internal List<ServiceCallItem> Calls()
        {
            List<ServiceCallItem> calls = Enumerable.Range(0, Keys)
                .SelectMany(key => Enumerable.Range(0, CallsPerKey)
                    .Select(call => (Milliseconds: (key * 37L) + ((long)call * IntervalMs), Key: key)))
                .OrderBy(call => call.Milliseconds)
                .ThenBy(call => call.Key)
                .Select((call, index) => new ServiceCallItem((uint)(index + 1))
                {
                    Host = RuleRuns.Endpoint,
                    Uri = $"https://{RuleRuns.Endpoint}/users/xuid({10001 + call.Key:D8})/devices/current",
                    ReqBodyHash = 1,
                    ReqBody = "1",
                    ReqTimeUTC = (ulong)Start.AddTicks(call.Milliseconds * TimeSpan.TicksPerMillisecond)
                        .ToFileTimeUtc(),
                })
                .ToList();
            if (SwappedId > 0)
            {
                (calls[SwappedId - 1].Id, calls[SwappedId].Id) = (calls[SwappedId].Id, calls[SwappedId - 1].Id);
            }

            return calls;
        }
    }

    // The complete findings of one rule run as canonical JSON: the rule name, endpoint and description, the result data
    // in the order the rule added it (key, value type and invariant value), and every violation in order with its
    // level, endpoint, summary and call ids. Each violation is on a line of its own, so two runs compare with a line
    // diff. The SHA-256 is over the UTF-8 bytes, which are exactly the bytes of the generator's files.
    internal static class CanonicalFindings
    {
        internal const string Schema = "xmat-rule-findings/1";

        internal static string ToJson(RuleResult result)
        {
            var text = new StringBuilder();
            text.Append("{\"schema\":").Append(Value(writer => writer.WriteStringValue(Schema)))
                .Append(",\"rule\":").Append(Value(writer => writer.WriteStringValue(result.RuleName)))
                .Append(",\"endpoint\":").Append(Value(writer => writer.WriteStringValue(result.Endpoint)))
                .Append(",\"description\":").Append(Value(writer => writer.WriteStringValue(result.RuleDescription)))
                .Append(",\"data\":").Append(Value(writer => WriteData(writer, result.Results)))
                .Append(",\"violations\":[");
            for (var index = 0; index < result.Violations.Count; index++)
            {
                RuleViolation violation = result.Violations[index];
                text.Append(index == 0 ? "\n" : ",\n").Append(Value(writer => WriteViolation(writer, violation)));
            }

            return text.Append("\n]}\n").ToString();
        }

        internal static string Sha256(string json) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        private static void WriteData(Utf8JsonWriter writer, Dictionary<string, object> data)
        {
            writer.WriteStartArray();
            foreach (KeyValuePair<string, object> entry in data)
            {
                writer.WriteStartObject();
                writer.WriteString("key", entry.Key);
                writer.WriteString("type", entry.Value == null ? "null" : entry.Value.GetType().Name);
                writer.WriteString("value", Format(entry.Value));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void WriteViolation(Utf8JsonWriter writer, RuleViolation violation)
        {
            writer.WriteStartObject();
            writer.WriteString("level", violation.m_level.ToString());
            writer.WriteString("endpoint", violation.m_endpoint);
            writer.WriteString("summary", violation.m_summary);
            writer.WriteStartArray("calls");
            foreach (ServiceCallItem call in violation.m_violatingCalls)
            {
                writer.WriteNumberValue(call.Id);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static string Format(object value) => value switch
        {
            null => "null",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        private static string Value(Action<Utf8JsonWriter> write)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                write(writer);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
