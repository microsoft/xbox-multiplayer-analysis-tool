// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Globalization;
using XMAT.Operations.Analysis;

namespace XMAT.Cli
{
    // One limit option of xmat analyze: a whole number from 1 to Maximum, Default when the option is absent.
    internal sealed record CliLimitOption(string Name, string Unit, int Default, int Maximum)
    {
        // Names the option and its range, never the value that was given.
        internal string RangeMessage =>
            Name + " takes a whole number" + Unit + " from 1 to " +
            Maximum.ToString("N0", CultureInfo.InvariantCulture) + ".";
    }

    // The input limits of xmat analyze as its options take them: whole MiB (1,048,576 bytes) and a number of records.
    // The defaults are the analysis operation's (AnalysisLimits.Default); a run reports the effective values.
    internal sealed record CliLimits(int MaxInputMib, int MaxDataMib, int MaxRecordMib, int MaxRecords)
    {
        internal const string InputOption = "--max-input-mib";
        internal const string DataOption = "--max-data-mib";
        internal const string RecordOption = "--max-record-mib";
        internal const string RecordsOption = "--max-records";

        // Policy ceilings, confirmed by measurement after the rule and memory fixes: the options accept nothing
        // larger, but a maximum is not a guaranteed capacity or a bound on memory use. The input is streamed and
        // hashed, never held whole, while the data and record limits bound the capture data that xmat holds, so their
        // maxima are lower. The record maximum plus SQLite's 1 MiB row overhead also stays far below SQLite's hard
        // value limit of 1,000,000,000 bytes, which the .xmatcap reader sets and reads back. The records maximum
        // matches the archive metadata ceilings: three entries per request plus 64 is
        // BoundedSazReader.MaxArchiveEntriesCeiling, and its central directory allowance is
        // BoundedSazReader.MaxCentralDirectoryCeiling.
        internal const int MaximumInputMib = 2048;
        internal const int MaximumDataMib = 512;
        internal const int MaximumRecordMib = 128;
        internal const int MaximumRecords = 250_000;

        internal static CliLimits Default { get; } = new(
            AnalysisLimits.DefaultInputMib,
            AnalysisLimits.DefaultDataMib,
            AnalysisLimits.DefaultRecordMib,
            AnalysisLimits.DefaultRecords);

        // In the order of the CliLimits values.
        internal static IReadOnlyList<CliLimitOption> Options { get; } = new[]
        {
            new CliLimitOption(InputOption, " of MiB", AnalysisLimits.DefaultInputMib, MaximumInputMib),
            new CliLimitOption(DataOption, " of MiB", AnalysisLimits.DefaultDataMib, MaximumDataMib),
            new CliLimitOption(RecordOption, " of MiB", AnalysisLimits.DefaultRecordMib, MaximumRecordMib),
            new CliLimitOption(RecordsOption, string.Empty, AnalysisLimits.DefaultRecords, MaximumRecords),
        };

        // Names both options and their defaults, never the values that were given.
        internal static string RecordOverDataMessage { get; } =
            RecordOption + " must not exceed " + DataOption + " (" +
            AnalysisLimits.DefaultRecordMib.ToString(CultureInfo.InvariantCulture) + " and " +
            AnalysisLimits.DefaultDataMib.ToString(CultureInfo.InvariantCulture) + " by default).";

        internal AnalysisLimits ToAnalysisLimits() =>
            AnalysisLimits.FromMib(MaxInputMib, MaxDataMib, MaxRecordMib, MaxRecords);

        // Reads the limit options that were given; the others keep their defaults. Returns the limits, or null and the
        // option whose value is invalid.
        internal static CliLimits Read(IReadOnlyDictionary<string, string> options, out CliLimitOption invalid)
        {
            invalid = null;
            var values = new int[Options.Count];
            for (int index = 0; index < Options.Count; index++)
            {
                CliLimitOption option = Options[index];
                if (!options.TryGetValue(option.Name, out string text))
                {
                    values[index] = option.Default;
                }
                else if (!TryParse(text, option.Maximum, out values[index]))
                {
                    invalid = option;
                    return null;
                }
            }

            return new CliLimits(values[0], values[1], values[2], values[3]);
        }

        // A whole number of ASCII digits from 1 to maximum: no sign, space, separator, exponent or other digit. Reading
        // stops once the number exceeds the maximum, so no run of digits can overflow.
        internal static bool TryParse(string text, int maximum, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            long number = 0;
            foreach (char character in text)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }

                number = (number * 10) + (character - '0');
                if (number > maximum)
                {
                    return false;
                }
            }

            if (number == 0)
            {
                return false;
            }

            value = (int)number;
            return true;
        }
    }
}
