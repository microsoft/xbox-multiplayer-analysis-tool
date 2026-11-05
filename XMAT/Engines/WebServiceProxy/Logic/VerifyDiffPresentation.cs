// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using XMAT.Operations.Replay;

namespace XMAT.WebServiceCapture
{
    // The en-us keys that word the codes and values of verification differences (VerifyDiffContracts), one for each; null
    // for a code or value that this version does not know, which is then shown with a fallback that names it.
    internal static class VerifyDiffWording
    {
        // A verdict's differences, such as "status code, body".
        internal static string Differences(ReplayVerification verification) => string.Join(
            ", ",
            verification.Differences.Select(difference => Localization.GetLocalizedString(
                difference == ReplayVerifier.StatusDifference ? "REPLAY_DIFFERENCE_STATUS" : "REPLAY_DIFFERENCE_BODY")));

        internal static string LimitationKey(string code) => code switch
        {
            VerifyDiffLimitation.OriginalIncomplete => "VERIFY_DIFF_LIMIT_ORIGINAL_INCOMPLETE",
            VerifyDiffLimitation.ActualIncomplete => "VERIFY_DIFF_LIMIT_ACTUAL_INCOMPLETE",
            VerifyDiffLimitation.OriginalEndUnproven => "VERIFY_DIFF_LIMIT_ORIGINAL_END_UNPROVEN",
            VerifyDiffLimitation.ActualEndUnproven => "VERIFY_DIFF_LIMIT_ACTUAL_END_UNPROVEN",
            VerifyDiffLimitation.ExaminedLimit => "VERIFY_DIFF_LIMIT_EXAMINED",
            VerifyDiffLimitation.LineLimit => "VERIFY_DIFF_LIMIT_LINES",
            VerifyDiffLimitation.JsonLimit => "VERIFY_DIFF_LIMIT_JSON",
            VerifyDiffLimitation.StepLimit => "VERIFY_DIFF_LIMIT_STEPS",
            VerifyDiffLimitation.WorkLimit => "VERIFY_DIFF_LIMIT_WORK",
            VerifyDiffLimitation.ScratchLimit => "VERIFY_DIFF_LIMIT_SCRATCH",
            VerifyDiffLimitation.ChangeLimit => "VERIFY_DIFF_LIMIT_CHANGES",
            VerifyDiffLimitation.HunkLimit => "VERIFY_DIFF_LIMIT_HUNKS",
            VerifyDiffLimitation.CharLimit => "VERIFY_DIFF_LIMIT_CHARS",
            VerifyDiffLimitation.ValueCut => "VERIFY_DIFF_LIMIT_VALUE_CUT",
            VerifyDiffLimitation.SerializedLimit => "VERIFY_DIFF_LIMIT_SERIALIZED",
            VerifyDiffLimitation.Internal => "VERIFY_DIFF_LIMIT_INTERNAL",
            _ => null,
        };

        internal static string ExplanationKey(string code) => code switch
        {
            VerifyJsonExplanation.Formatting => "VERIFY_DIFF_JSON_EXPLANATION_FORMATTING",
            VerifyJsonExplanation.MemberOrder => "VERIFY_DIFF_JSON_EXPLANATION_MEMBER_ORDER",
            VerifyJsonExplanation.Escapes => "VERIFY_DIFF_JSON_EXPLANATION_ESCAPES",
            _ => null,
        };

        internal static string JsonUnavailableKey(string code) => code switch
        {
            VerifyJsonUnavailable.Invalid => "VERIFY_DIFF_JSON_UNAVAILABLE_INVALID",
            VerifyJsonUnavailable.DuplicateKeys => "VERIFY_DIFF_JSON_UNAVAILABLE_DUPLICATE_KEYS",
            VerifyJsonUnavailable.MultipleDocuments => "VERIFY_DIFF_JSON_UNAVAILABLE_MULTIPLE_DOCUMENTS",
            VerifyJsonUnavailable.Charset => "VERIFY_DIFF_JSON_UNAVAILABLE_CHARSET",
            VerifyJsonUnavailable.NotWhole => "VERIFY_DIFF_JSON_UNAVAILABLE_NOT_WHOLE",
            VerifyJsonUnavailable.DepthLimit => "VERIFY_DIFF_JSON_UNAVAILABLE_DEPTH_LIMIT",
            VerifyJsonUnavailable.NodeLimit => "VERIFY_DIFF_JSON_UNAVAILABLE_NODE_LIMIT",
            VerifyJsonUnavailable.Limit => "VERIFY_DIFF_JSON_UNAVAILABLE_LIMIT",
            _ => null,
        };

        internal static string Key(VerifyDiffState value) => value switch
        {
            VerifyDiffState.Complete => "VERIFY_DIFF_STATE_COMPLETE",
            VerifyDiffState.Limited => "VERIFY_DIFF_STATE_LIMITED",
            VerifyDiffState.Unavailable => "VERIFY_DIFF_STATE_UNAVAILABLE",
            _ => null,
        };

        internal static string Key(VerifyBodyBasis value) => value switch
        {
            VerifyBodyBasis.Plain => "VERIFY_DIFF_BASIS_PLAIN",
            VerifyBodyBasis.Decoded => "VERIFY_DIFF_BASIS_DECODED",
            VerifyBodyBasis.UnsupportedCoding => "VERIFY_DIFF_BASIS_UNSUPPORTED_CODING",
            VerifyBodyBasis.MalformedCoding => "VERIFY_DIFF_BASIS_MALFORMED_CODING",
            VerifyBodyBasis.DecodedLimit => "VERIFY_DIFF_BASIS_DECODED_LIMIT",
            _ => null,
        };

        internal static string Key(VerifyFraming value) => value switch
        {
            VerifyFraming.None => "VERIFY_DIFF_FRAMING_NONE",
            VerifyFraming.Removed => "VERIFY_DIFF_FRAMING_REMOVED",
            VerifyFraming.NotRemoved => "VERIFY_DIFF_FRAMING_NOT_REMOVED",
            _ => null,
        };

        internal static string Key(VerifyDecodeEnd value) => value switch
        {
            VerifyDecodeEnd.NotApplicable => "VERIFY_DIFF_END_NOT_APPLICABLE",
            VerifyDecodeEnd.Complete => "VERIFY_DIFF_END_COMPLETE",
            VerifyDecodeEnd.Incomplete => "VERIFY_DIFF_END_INCOMPLETE",
            VerifyDecodeEnd.Unproven => "VERIFY_DIFF_END_UNPROVEN",
            _ => null,
        };

        internal static string Key(VerifyLineKind value) => value switch
        {
            VerifyLineKind.Context => "VERIFY_DIFF_LINE_CONTEXT",
            VerifyLineKind.Removed => "VERIFY_DIFF_LINE_REMOVED",
            VerifyLineKind.Added => "VERIFY_DIFF_LINE_ADDED",
            _ => null,
        };

        internal static string Key(VerifyJsonChangeKind value) => value switch
        {
            VerifyJsonChangeKind.Changed => "VERIFY_DIFF_JSON_CHANGED",
            VerifyJsonChangeKind.Added => "VERIFY_DIFF_JSON_ADDED",
            VerifyJsonChangeKind.Removed => "VERIFY_DIFF_JSON_REMOVED",
            _ => null,
        };

        // Whether the bodies are the same text (VerifyTextDiff.Equivalence).
        internal static string TextKey(VerifyEquivalence value) => value switch
        {
            VerifyEquivalence.Different => "VERIFY_DIFF_TEXT_DIFFERENT",
            VerifyEquivalence.Same => "VERIFY_DIFF_TEXT_SAME",
            VerifyEquivalence.Unknown => "VERIFY_DIFF_TEXT_UNKNOWN",
            _ => null,
        };

        // Whether the bodies are the same JSON values (VerifyJsonDiff.Values).
        internal static string JsonKey(VerifyEquivalence value) => value switch
        {
            VerifyEquivalence.Different => "VERIFY_DIFF_JSON_DIFFERENT",
            VerifyEquivalence.Same => "VERIFY_DIFF_JSON_SAME",
            VerifyEquivalence.Unknown => "VERIFY_DIFF_JSON_UNKNOWN",
            _ => null,
        };
    }

    // What View verification differences (VerifyDiffWindow) shows of a row's recorded verification, as text: Summary, the
    // verdict and the status codes; Detail, whether the differences are complete, why not, and how verify read each body;
    // Body and Json, the text of the window's tabs; and Message, why there are no differences to show, or null. It is
    // worded from the record's verdict, codes and values only, and it holds what they hold, captured content included, so
    // it is shown and never logged or saved. The verdict is the record's, never derived from the differences.
    internal sealed record VerifyDiffPresentation(string Summary, string Detail, string Body, string Json, string Message)
    {
        private const string Indent = "    ";

        // The characters that a text box starts a new line at.
        private static readonly char[] LineBreakCharacters = { '\r', '\n', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029' };

        // The line breaks between the tokens of a JSON value's text.
        private static readonly string[] JsonLineBreaks = { "\r\n", "\r", "\n" };

        // The text of a record. The differences are bounded (VerifyDiffLimits: 64 Ki characters, 256 changes, 128 hunks,
        // a line or value cut at 1,024 characters), and so is their text.
        internal static VerifyDiffPresentation Of(RecordedVerification recorded)
        {
            ArgumentNullException.ThrowIfNull(recorded);
            string verdict = recorded.Verdict.Matches
                ? Word("VERIFY_DIFF_MATCHED")
                : Word("VERIFY_DIFF_DIFFERED", VerifyDiffWording.Differences(recorded.Verdict));
            VerifyDiff diff = recorded.Diff;
            if (diff == null)
            {
                return new VerifyDiffPresentation(
                    verdict,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    recorded.NotKeptBytes is int notKept
                        ? Word("VERIFY_DIFF_NOT_KEPT", notKept, recorded.BudgetBytes / (1024 * 1024))
                        : Word("VERIFY_DIFF_NOT_RECORDED"));
            }

            return new VerifyDiffPresentation(
                verdict + Environment.NewLine + Status(diff),
                DetailOf(diff),
                BodyOf(diff.Body),
                JsonOf(diff.Body),
                null);
        }

        // Shows a line break mark for each character that would start a new line in a text box, so that text never
        // passes for more lines than it is.
        internal static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOfAny(LineBreakCharacters) < 0)
            {
                return text ?? string.Empty;
            }

            var line = new StringBuilder(text.Length + 16);
            foreach (char character in text)
            {
                string mark = Mark(character);
                if (mark == null)
                {
                    line.Append(character);
                }
                else
                {
                    line.Append(mark);
                }
            }

            return line.ToString();
        }

        // The mark that ends a line of text for its break (VerifyTextLine.Break), so that lines that differ only in their
        // breaks show it; nothing for a last line without one.
        internal static string BreakMark(string lineBreak) => lineBreak switch
        {
            null or "" => string.Empty,
            "\r\n" => "\u240D\u240A",
            "\n" or "\r" or "\u2028" or "\u2029" => Mark(lineBreak[0]),
            _ => "\u27E8?\u27E9",
        };

        private static string Mark(char character) => character switch
        {
            '\r' => "\u240D",
            '\n' => "\u240A",
            '\u000B' => "\u240B",
            '\u000C' => "\u240C",
            '\u0085' => "\u27E8NEL\u27E9",
            '\u2028' => "\u27E8LS\u27E9",
            '\u2029' => "\u27E8PS\u27E9",
            _ => null,
        };

        private static string Status(VerifyDiff diff) => diff.OriginalStatus switch
        {
            null => Word("VERIFY_DIFF_STATUS_NONE", diff.ActualStatus),
            int original when original == diff.ActualStatus => Word("VERIFY_DIFF_STATUS_SAME", original),
            int original => Word("VERIFY_DIFF_STATUS", original, diff.ActualStatus),
        };

        // Whether the differences are complete, each limitation, then how verify read each body.
        private static string DetailOf(VerifyDiff diff)
        {
            var lines = new List<string> { Worded(VerifyDiffWording.Key(diff.State), diff.State) };
            foreach (string code in diff.Limitations ?? Array.Empty<string>())
            {
                lines.Add("\u2022 " + Worded(VerifyDiffWording.LimitationKey(code), code, "VERIFY_DIFF_LIMIT_OTHER"));
            }

            lines.Add(Side("VERIFY_DIFF_ORIGINAL", diff.Original));
            lines.Add(Side("VERIFY_DIFF_ACTUAL", diff.Actual));
            return string.Join(Environment.NewLine, lines);
        }

        private static string Side(string labelKey, VerifyDiffSide side) => side == null
            ? Word(labelKey)
            : Word(
                "VERIFY_DIFF_SIDE",
                Word(labelKey),
                side.ComparedBytes,
                side.ExaminedBytes,
                Worded(VerifyDiffWording.Key(side.Basis), side.Basis),
                Worded(VerifyDiffWording.Key(side.Framing), side.Framing),
                Worded(VerifyDiffWording.Key(side.End), side.End),
                side.Charset == null ? Word("VERIFY_DIFF_NOT_TEXT") : Word("VERIFY_DIFF_TEXT_CHARSET", OneLine(side.Charset)));

        private static string BodyOf(VerifyBodyDiff body)
        {
            if (body == null)
            {
                return Word("VERIFY_DIFF_BODY_UNAVAILABLE");
            }

            if (body.Equal)
            {
                return Word("VERIFY_DIFF_BODY_EQUAL");
            }

            if (body.Text != null)
            {
                return TextOf(body.Text);
            }

            return body.Binary != null ? BytesOf(body.Binary) : Word("VERIFY_DIFF_BODY_UNAVAILABLE");
        }

        // The hunks as in a unified diff: "@@ -start,count +start,count @@", then each line after "- " (only captured),
        // "+ " (only replayed) or two spaces (both), its cut mark and the mark of its break.
        private static string TextOf(VerifyTextDiff text)
        {
            var output = new StringBuilder(Worded(VerifyDiffWording.TextKey(text.Equivalence), text.Equivalence));
            IReadOnlyList<VerifyTextHunk> hunks = text.Hunks ?? Array.Empty<VerifyTextHunk>();
            if (hunks.Count == 0)
            {
                if (text.Equivalence == VerifyEquivalence.Different)
                {
                    output.AppendLine().Append(Word("VERIFY_DIFF_NO_LINES"));
                }

                return output.ToString();
            }

            output.AppendLine().Append(Word(
                "VERIFY_DIFF_TEXT_LEGEND",
                Worded(VerifyDiffWording.Key(VerifyLineKind.Removed), VerifyLineKind.Removed),
                Worded(VerifyDiffWording.Key(VerifyLineKind.Added), VerifyLineKind.Added),
                Worded(VerifyDiffWording.Key(VerifyLineKind.Context), VerifyLineKind.Context)));
            output.AppendLine().Append(Word("VERIFY_DIFF_BREAK_LEGEND", Word("VERIFY_DIFF_CUT")));
            foreach (VerifyTextHunk hunk in hunks)
            {
                output.AppendLine().AppendLine().Append(CultureInfo.InvariantCulture,
                    $"@@ -{hunk.OriginalStart},{hunk.OriginalCount} +{hunk.ActualStart},{hunk.ActualCount} @@");
                foreach (VerifyTextLine line in hunk.Lines ?? Array.Empty<VerifyTextLine>())
                {
                    output.AppendLine()
                        .Append(line.Kind switch
                        {
                            VerifyLineKind.Removed => "- ",
                            VerifyLineKind.Added => "+ ",
                            _ => "  ",
                        })
                        .Append(OneLine(line.Text))
                        .Append(line.Cut ? Word("VERIFY_DIFF_CUT") : string.Empty)
                        .Append(BreakMark(line.Break));
                }
            }

            return output.ToString();
        }

        // Both lengths, then each run of differing bytes: its offset, its length on each side and the hex excerpt of each
        // side, with "..." when the run is longer than its excerpt.
        private static string BytesOf(VerifyByteDiff bytes)
        {
            var output = new StringBuilder(Word("VERIFY_DIFF_BYTES", bytes.OriginalLength, bytes.ActualLength));
            IReadOnlyList<VerifyByteRange> ranges = bytes.Ranges ?? Array.Empty<VerifyByteRange>();
            if (ranges.Count == 0)
            {
                output.AppendLine().Append(Word("VERIFY_DIFF_NO_RANGES"));
            }

            foreach (VerifyByteRange range in ranges)
            {
                output.AppendLine().Append(Word("VERIFY_DIFF_RANGE", range.Offset, range.OriginalLength, range.ActualLength));
                AppendExcerpt(output, "VERIFY_DIFF_CAPTURED", range.Original, range.OriginalLength);
                AppendExcerpt(output, "VERIFY_DIFF_REPLAYED", range.Actual, range.ActualLength);
            }

            return output.ToString();
        }

        // One side's excerpt of a run ("XX XX ..."): nothing for a side without bytes there.
        private static void AppendExcerpt(StringBuilder output, string labelKey, string excerpt, long run)
        {
            if (run <= 0)
            {
                return;
            }

            string hex = OneLine(excerpt);
            output.AppendLine().Append(Indent).Append(Word(labelKey)).Append(' ');
            if (hex.Length == 0)
            {
                output.Append(Word("VERIFY_DIFF_NOT_EXAMINED"));
                return;
            }

            output.Append(hex);
            if ((hex.Length + 1) / 3 < run)
            {
                output.Append(" \u2026");
            }
        }

        private static string JsonOf(VerifyBodyDiff body)
        {
            if (body == null)
            {
                return Word("VERIFY_DIFF_BODY_UNAVAILABLE");
            }

            if (body.Equal)
            {
                return Word("VERIFY_DIFF_BODY_EQUAL");
            }

            if (body.Json == null)
            {
                return body.JsonUnavailable == null
                    ? Word("VERIFY_DIFF_JSON_NONE")
                    : Word(
                        "VERIFY_DIFF_JSON_UNAVAILABLE",
                        Worded(
                            VerifyDiffWording.JsonUnavailableKey(body.JsonUnavailable),
                            body.JsonUnavailable,
                            "VERIFY_DIFF_JSON_UNAVAILABLE_OTHER"));
            }

            VerifyJsonDiff json = body.Json;
            var output = new StringBuilder(Worded(VerifyDiffWording.JsonKey(json.Values), json.Values));
            foreach (string code in json.Explanations ?? Array.Empty<string>())
            {
                output.AppendLine().Append("\u2022 ").Append(
                    Worded(VerifyDiffWording.ExplanationKey(code), code, "VERIFY_DIFF_JSON_EXPLANATION_OTHER"));
            }

            foreach (VerifyJsonChange change in json.Changes ?? Array.Empty<VerifyJsonChange>())
            {
                output.AppendLine().AppendLine().Append(Word(
                    "VERIFY_DIFF_JSON_CHANGE",
                    Worded(VerifyDiffWording.Key(change.Kind), change.Kind),
                    OneLine(change.Path) + (change.PathCut ? Word("VERIFY_DIFF_CUT") : string.Empty)));
                AppendValue(output, "VERIFY_DIFF_CAPTURED", change.Original, change.OriginalCut);
                AppendValue(output, "VERIFY_DIFF_REPLAYED", change.Actual, change.ActualCut);
            }

            return output.ToString();
        }

        // One side's JSON text of a changed value, as written: its line breaks continue it on lines indented under its
        // start. Nothing for the side that does not have the value.
        private static void AppendValue(StringBuilder output, string labelKey, string value, bool cut)
        {
            if (value == null)
            {
                return;
            }

            string label = Word(labelKey);
            string continued = Environment.NewLine + Indent + new string(' ', label.Length + 1);
            output.AppendLine().Append(Indent).Append(label).Append(' ')
                .Append(string.Join(continued, value.Split(JsonLineBreaks, StringSplitOptions.None).Select(OneLine)))
                .Append(cut ? Word("VERIFY_DIFF_CUT") : string.Empty);
        }

        private static string Word(string key, params object[] arguments) =>
            Localization.GetLocalizedString(key, arguments);

        // A code or value worded by its key, or, when this version has no key for it, by the fallback, which names it.
        private static string Worded(string key, object value, string fallbackKey = "VERIFY_DIFF_UNKNOWN") => key == null
            ? Word(fallbackKey, OneLine(Convert.ToString(value, CultureInfo.InvariantCulture)))
            : Word(key);
    }
}
