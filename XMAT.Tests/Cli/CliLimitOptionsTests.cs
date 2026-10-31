// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Analysis;
using XMAT.Operations.Capabilities;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Cli
{
    // The four limit options of xmat analyze, in process: the values the parser accepts and rejects, the effective
    // limits it gives the operation and reports in its JSON (an internal error included), the archive reader's metadata
    // ceilings, and analyze's command line in the capability registry.
    public class CliLimitOptionsTests
    {
        private const long Mib = 1024 * 1024;

        // SQLite's hard value limit (SQLITE_MAX_LENGTH). The .xmatcap reader sets SQLITE_LIMIT_LENGTH to the record
        // limit plus 1 MiB and refuses the file when SQLite reads back less.
        private const long SqliteMaxLength = 1_000_000_000;

        public static TheoryData<string> LimitOptions() => new()
        {
            CliLimits.InputOption,
            CliLimits.DataOption,
            CliLimits.RecordOption,
            CliLimits.RecordsOption,
        };

        [Fact]
        public void Parse_WithoutLimitOptions_UsesTheOperationDefaults()
        {
            CliLimits limits = Parse("analyze a.saz --out results").Request.Limits;

            Assert.Equal(new CliLimits(512, 256, 32, 100_000), limits);
            Assert.Equal(AnalysisLimits.Default, limits.ToAnalysisLimits());
        }

        [Fact]
        public void Parse_ReadsEveryLimitInEitherForm_AndConvertsMibToBytes()
        {
            CliLimits spaced = Parse(
                "analyze a.saz --out results --max-input-mib 1024 --max-data-mib 512 --max-record-mib 64 " +
                "--max-records 250000").Request.Limits;
            CliLimits joined = Parse(
                "analyze a.saz --out results --max-input-mib=1024 --max-data-mib=512 --max-record-mib=64 " +
                "--max-records=250000").Request.Limits;

            Assert.Equal(new CliLimits(1024, 512, 64, 250_000), spaced);
            Assert.Equal(spaced, joined);
            Assert.Equal(new AnalysisLimits(1024 * Mib, 64 * Mib, 512 * Mib, 250_000), spaced.ToAnalysisLimits());
        }

        // A limit that is not given keeps its default. Leading zeros are allowed: the value is still decimal.
        [Theory]
        [InlineData("--max-input-mib 1", 1, 256, 32, 100_000)]
        [InlineData("--max-data-mib 32", 512, 32, 32, 100_000)]
        [InlineData("--max-record-mib 1", 512, 256, 1, 100_000)]
        [InlineData("--max-records 1", 512, 256, 32, 1)]
        [InlineData("--max-records 0100", 512, 256, 32, 100)]
        [InlineData("--max-data-mib 48 --max-record-mib 48", 512, 48, 48, 100_000)]
        public void Parse_KeepsTheDefaultOfEveryLimitNotGiven(
            string options,
            int input,
            int data,
            int record,
            int records)
        {
            CliLimits limits = Parse("analyze a.saz --out results " + options).Request.Limits;

            Assert.Equal(new CliLimits(input, data, record, records), limits);
        }

        // All four maximums together are a valid command line, and their bytes fit the readers: the record maximum
        // stays within the data maximum and, with SQLite's 1 MiB row overhead, within SQLite's hard value limit, and an
        // archive at the request maximum stays within the reader's metadata ceilings.
        [Fact]
        public void Parse_AcceptsEveryMaximumTogether_AndTheReadersCanApplyThem()
        {
            CliLimits limits = Parse(
                "analyze a.saz --out results" +
                Option(CliLimits.InputOption, CliLimits.MaximumInputMib) +
                Option(CliLimits.DataOption, CliLimits.MaximumDataMib) +
                Option(CliLimits.RecordOption, CliLimits.MaximumRecordMib) +
                Option(CliLimits.RecordsOption, CliLimits.MaximumRecords))
                .Request.Limits;
            AnalysisLimits bytes = limits.ToAnalysisLimits();
            SqLtSchemaProfile profile = SqLtSchemaProfile.CreateCli(bytes.MaxInputBytes, bytes.MaxEntryBytes);

            Assert.Equal(
                new CliLimits(
                    CliLimits.MaximumInputMib,
                    CliLimits.MaximumDataMib,
                    CliLimits.MaximumRecordMib,
                    CliLimits.MaximumRecords),
                limits);
            Assert.Equal(CliLimits.MaximumInputMib * Mib, bytes.MaxInputBytes);
            Assert.Equal(CliLimits.MaximumDataMib * Mib, bytes.MaxTotalRetainedBytes);
            Assert.Equal(CliLimits.MaximumRecordMib * Mib, bytes.MaxEntryBytes);
            Assert.Equal(CliLimits.MaximumRecords, bytes.MaxRecords);
            Assert.Equal(bytes.MaxInputBytes, profile.MaxDatabaseBytes);
            Assert.Equal(bytes.MaxEntryBytes + Mib, (long)profile.MaxValueBytes.Value);
            Assert.InRange(bytes.MaxEntryBytes + Mib, 1L, SqliteMaxLength);
            Assert.Equal(
                BoundedSazReader.MaxArchiveEntriesCeiling,
                BoundedSazReader.EntryLimitForRecords(bytes.MaxRecords));
            Assert.Equal(
                BoundedSazReader.MaxCentralDirectoryCeiling,
                BoundedSazReader.CentralDirectoryLimitForRecords(bytes.MaxRecords));
        }

        // Only a whole number of ASCII digits from 1 to the maximum: no sign, space, separator, decimal point,
        // exponent, hexadecimal or other digits, and nothing that overflows.
        [Theory]
        [InlineData("0")]
        [InlineData("00")]
        [InlineData("-1")]
        [InlineData("+1")]
        [InlineData(" 1")]
        [InlineData("1 ")]
        [InlineData("1.0")]
        [InlineData("1e3")]
        [InlineData("0x10")]
        [InlineData("1,000")]
        [InlineData("1_000")]
        [InlineData("ten")]
        [InlineData("\u0661")] // ARABIC-INDIC DIGIT ONE
        [InlineData("\uFF11")] // FULLWIDTH DIGIT ONE
        [InlineData("2147483648")]
        [InlineData("99999999999999999999999999999999")]
        public void Parse_RejectsEveryOtherValue(string value)
        {
            foreach (CliLimitOption option in CliLimits.Options)
            {
                CliParseResult result = CliCommandLine.Parse(
                    new[] { "analyze", "a.saz", "--out", "results", option.Name, value });

                AssertUsageError(result, "cli.usage.invalid-value", option.RangeMessage);
            }
        }

        // The message names the option and its range.
        [Theory]
        [MemberData(nameof(LimitOptions))]
        public void Parse_AcceptsTheMaximum_ButNotOneMore(string name)
        {
            CliLimitOption option = CliLimits.Options.Single(item => item.Name == name);
            string maximum = option.Maximum.ToString(CultureInfo.InvariantCulture);
            string oneMore = (option.Maximum + 1L).ToString(CultureInfo.InvariantCulture);
            string[] dataAtItsMaximum = name == CliLimits.RecordOption
                ? new[] { CliLimits.DataOption, CliLimits.MaximumDataMib.ToString(CultureInfo.InvariantCulture) }
                : Array.Empty<string>();

            CliParseResult accepted = CliCommandLine.Parse(
                new[] { "analyze", "a.saz", "--out", "results", name, maximum }.Concat(dataAtItsMaximum).ToArray());
            CliParseResult rejected = CliCommandLine.Parse(
                new[] { "analyze", "a.saz", "--out", "results", name, oneMore }.Concat(dataAtItsMaximum).ToArray());

            Assert.True(accepted.Succeeded, accepted.Error?.Message);
            AssertUsageError(rejected, "cli.usage.invalid-value", option.RangeMessage);
            Assert.Equal(
                name + " takes a whole number" + (name == CliLimits.RecordsOption ? string.Empty : " of MiB") +
                    " from 1 to " + option.Maximum.ToString("N0", CultureInfo.InvariantCulture) + ".",
                option.RangeMessage);
        }

        [Theory]
        [MemberData(nameof(LimitOptions))]
        public void Parse_RejectsALimitGivenTwice(string name)
        {
            CliParseResult result = CliCommandLine.Parse(
                new[] { "analyze", "a.saz", "--out", "results", name, "8", name + "=8" });

            AssertUsageError(result, "cli.usage.duplicate-option", $"The {name} option was given more than once.");
        }

        [Theory]
        [MemberData(nameof(LimitOptions))]
        public void Parse_RejectsALimitWithoutAValue(string name)
        {
            AssertUsageError(
                CliCommandLine.Parse(new[] { "analyze", "a.saz", "--out", "results", name }),
                "cli.usage.missing-value",
                $"The {name} option needs a value.");
            AssertUsageError(
                CliCommandLine.Parse(new[] { "analyze", "a.saz", "--out", "results", name + "=" }),
                "cli.usage.missing-value",
                $"The {name} option needs a value.");
        }

        // The record limit never exceeds the data limit, the default of an option that was not given included.
        [Theory]
        [InlineData("--max-data-mib 40 --max-record-mib 48")]
        [InlineData("--max-data-mib 16")]
        [InlineData("--max-record-mib 120 --max-data-mib 100")]
        public void Parse_RejectsARecordLimitAboveTheDataLimit(string options)
        {
            CliParseResult result = Parse("analyze a.saz --out results " + options);

            AssertUsageError(
                result,
                "cli.usage.invalid-value",
                "--max-record-mib must not exceed --max-data-mib (32 and 256 by default).");
        }

        // A value is never repeated: it could be a path or a secret.
        [Theory]
        [InlineData("--max-input-mib s3cretcanary9")]
        [InlineData("--max-data-mib=s3cretcanary9")]
        [InlineData("--max-record-mib 1s3cretcanary9")]
        [InlineData("--max-records=-s3cretcanary9")]
        public void Parse_NeverRepeatsALimitValue(string options)
        {
            CliParseResult result = Parse("analyze a.saz --out results " + options);
            CliReport report = CliReport.ForParseError("run", result);

            Assert.Equal("cli.usage.invalid-value", result.Error.Code);
            Assert.Equal(CliExitCodes.Usage, report.ExitCode);
            Assert.DoesNotContain("s3cretcanary9", result.Error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "s3cretcanary9",
                Encoding.UTF8.GetString(CliJson.Serialize(report)),
                StringComparison.OrdinalIgnoreCase);
        }

        // The JSON output and summary.json report the effective limits right after the input, the defaults or the
        // values given. A run whose command line was invalid has no effective limits.
        [Fact]
        public void Json_ReportsTheEffectiveLimits()
        {
            var defaults = new CliReport(CliCommands.Analyze, "run")
            {
                Analysis = new CliAnalysisReport { Limits = CliLimits.Default },
            };
            var given = new CliReport(CliCommands.Analyze, "run")
            {
                Analysis = new CliAnalysisReport { Limits = new CliLimits(1024, 512, 48, 7) },
            };
            CliReport usageError = CliReport.ForParseError("run", Parse("analyze a.saz --out results --max-records 0"));

            using JsonDocument stdout = JsonDocument.Parse(CliJson.Serialize(defaults));
            using JsonDocument summary = JsonDocument.Parse(CliJson.SerializeSummary(given));
            using JsonDocument rejected = JsonDocument.Parse(CliJson.Serialize(usageError));

            Assert.Equal(new[] { 512L, 256L, 32L, 100_000L }, Limits(stdout.RootElement));
            Assert.Equal(new[] { 1024L, 512L, 48L, 7L }, Limits(summary.RootElement));
            Assert.Equal(
                new[] { "schema", "command", "runId", "version", "input", "limits", "devices", "totals", "warnings" },
                summary.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(
                "input",
                stdout.RootElement.EnumerateObject().Select(property => property.Name)
                    .TakeWhile(name => name != "limits").Last());
            Assert.False(rejected.RootElement.TryGetProperty("limits", out _));
        }

        // analyze's command line in the capability registry, which help and docs/cli.md show, names exactly the options
        // that the parser accepts.
        [Fact]
        public void AnalyzeCommandLine_NamesExactlyTheOptionsThatTheParserAccepts()
        {
            Capability analyze = CapabilityRegistry.All.Single(capability => capability.Id == "analysis.xbox-live");

            IEnumerable<string> named = analyze.CliRoute
                .Split(new[] { ' ', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.StartsWith("--", StringComparison.Ordinal));

            Assert.Equal(
                CliCommandLine.AnalyzeOptionNames.OrderBy(name => name, StringComparer.Ordinal),
                named.OrderBy(name => name, StringComparer.Ordinal));
            Assert.All(
                CliLimits.Options,
                option => Assert.Contains($"[{option.Name} N]", analyze.CliRoute, StringComparison.Ordinal));
        }

        // The central directory grows with the allowed requests at 32 MiB per 100,000 (rounded up), never below 32 MiB
        // and never above its fixed ceiling, whatever the request limit.
        [Theory]
        [InlineData(1, 32 * Mib)]
        [InlineData(100_000, 32 * Mib)]
        [InlineData(100_001, (32 * Mib) + 336)]
        [InlineData(250_000, 80 * Mib)]
        [InlineData(250_001, 80 * Mib)]
        [InlineData(int.MaxValue, 80 * Mib)]
        public void CentralDirectoryLimit_FollowsTheRequestLimitUpToItsCeiling(int records, long expected)
        {
            Assert.Equal(expected, BoundedSazReader.CentralDirectoryLimitForRecords(records));
        }

        // The reader's ceilings on the archive metadata hold whatever limits its caller passes: a declared entry count
        // or central directory size above them is refused before the directory is walked, and one at them is not (these
        // archives are then refused as malformed, since their directories hold no entries).
        [Theory]
        [InlineData(0L, BoundedSazReader.MaxArchiveEntriesCeiling, "Malformed")]
        [InlineData(0L, BoundedSazReader.MaxArchiveEntriesCeiling + 1, "TooManyEntries")]
        [InlineData(BoundedSazReader.MaxCentralDirectoryCeiling, 1L, "Malformed")]
        [InlineData(BoundedSazReader.MaxCentralDirectoryCeiling + 1, 1L, "CentralDirectoryTooLarge")]
        public void ArchiveMetadataCeilings_HoldWhateverLimitsTheCallerPasses(
            long directoryBytes,
            long entries,
            string expected)
        {
            using Stream archive = Zip64Directory(directoryBytes, (ulong)entries);
            var unlimited = new BoundedSazLimits(
                MaxInputBytes: long.MaxValue,
                MaxEntryBytes: 32 * Mib,
                MaxTotalUncompressedBytes: 256 * Mib,
                MaxRecords: int.MaxValue,
                MaxArchiveEntries: long.MaxValue,
                MaxCentralDirectoryBytes: long.MaxValue);

            BoundedSazException exception = Assert.Throws<BoundedSazException>(
                () => BoundedSazReader.Read(archive, unlimited, CancellationToken.None));

            Assert.Equal(expected, exception.Failure.ToString());
        }

        // An internal error after analyze's command line was accepted still reports the effective limits; one in
        // another command, after a usage error or while the command line was parsed reports none.
        [Fact]
        public void InternalError_ReportsTheEffectiveLimitsOnceAnalyzeCommandLineIsValid()
        {
            var failure = new InvalidOperationException("s3cretcanary9");
            CliReport analyze = CliReport.ForInternalError(
                "run", Parse("analyze a.saz --out results --max-records 7"), failure);
            CliReport[] withoutLimits =
            {
                CliReport.ForInternalError("run", Parse("analyze a.saz --out results --max-records 0"), failure),
                CliReport.ForInternalError("run", Parse("doctor --for analyze"), failure),
                CliReport.ForInternalError("run", null, failure),
            };

            using JsonDocument document = JsonDocument.Parse(CliJson.Serialize(analyze));

            Assert.Equal(CliExitCodes.Internal, analyze.ExitCode);
            Assert.Equal("analyze", document.RootElement.GetProperty("command").GetString());
            Assert.Equal(new[] { 512L, 256L, 32L, 7L }, Limits(document.RootElement));
            Assert.Equal("cli.internal", Assert.Single(analyze.Errors).Code);
            Assert.DoesNotContain(
                "s3cretcanary9",
                Encoding.UTF8.GetString(CliJson.Serialize(analyze)),
                StringComparison.Ordinal);
            Assert.All(withoutLimits, report =>
            {
                using JsonDocument other = JsonDocument.Parse(CliJson.Serialize(report));
                Assert.Equal(CliExitCodes.Internal, report.ExitCode);
                Assert.False(other.RootElement.TryGetProperty("limits", out _));
            });
        }

        private static CliParseResult Parse(string commandLine) =>
            CliCommandLine.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // A ZIP64 archive whose central directory is directoryBytes of zeros at its start, followed by the ZIP64 end
        // record that declares the directory and the entry count, its locator and the end record. The zeros are never
        // allocated.
        private static Stream Zip64Directory(long directoryBytes, ulong entries)
        {
            var tail = new byte[56 + 20 + 22];
            Span<byte> record = tail.AsSpan(0, 56);
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(4), 44);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(12), 45);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(14), 45);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(24), entries);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(32), entries);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(40), (ulong)directoryBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(48), 0);
            Span<byte> locator = tail.AsSpan(56, 20);
            BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(locator.Slice(8), (ulong)directoryBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(locator.Slice(16), 1);
            Span<byte> end = tail.AsSpan(76, 22);
            BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
            BinaryPrimitives.WriteUInt16LittleEndian(end.Slice(8), ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(end.Slice(10), ushort.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(end.Slice(12), uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(end.Slice(16), uint.MaxValue);
            return new ZeroPrefixedStream(directoryBytes, tail);
        }

        private static string Option(string name, int value) =>
            " " + name + " " + value.ToString(CultureInfo.InvariantCulture);

        private static long[] Limits(JsonElement root)
        {
            JsonElement limits = root.GetProperty("limits");
            Assert.Equal(
                new[] { "maxInputMib", "maxDataMib", "maxRecordMib", "maxRecords" },
                limits.EnumerateObject().Select(property => property.Name));
            return limits.EnumerateObject().Select(property => property.Value.GetInt64()).ToArray();
        }

        private static void AssertUsageError(CliParseResult result, string code, string message)
        {
            Assert.False(result.Succeeded);
            Assert.Null(result.Request);
            Assert.Equal(code, result.Error.Code);
            Assert.Equal(message, result.Error.Message);
            Assert.Equal(CliExitCodes.Usage, CliReport.ForParseError("run", result).ExitCode);
        }

        // A read-only stream of prefixLength zero bytes followed by tail.
        private sealed class ZeroPrefixedStream : Stream
        {
            private readonly long _prefixLength;
            private readonly byte[] _tail;

            internal ZeroPrefixedStream(long prefixLength, byte[] tail)
            {
                _prefixLength = prefixLength;
                _tail = tail;
            }

            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => _prefixLength + _tail.Length;

            public override long Position { get; set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = (int)Math.Clamp(Length - Position, 0, count);
                for (int index = 0; index < read; index++)
                {
                    long at = Position + index;
                    buffer[offset + index] = at < _prefixLength ? (byte)0 : _tail[at - _prefixLength];
                }

                Position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) =>
                Position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => Position + offset,
                    _ => Length + offset,
                };

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
