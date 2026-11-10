// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Operations.Replay
{
    // How verify got the bytes that it compared for one side (ReplayVerifier). Plain and Decoded are the body's content; the
    // other bases are the stored bytes, which verify compares when it does not remove the content coding, and which are
    // never shown as the content.
    internal enum VerifyBodyBasis
    {
        // No content coding, identity, or an empty body: the body as stored, without its chunked framing when that was
        // removed.
        Plain,

        // The content coding (gzip, x-gzip, deflate or br) was removed.
        Decoded,

        // A content coding that verify does not decode, or more than one: the stored bytes.
        UnsupportedCoding,

        // The content coding did not decode: the stored bytes.
        MalformedCoding,

        // The decoded content would be larger than the verify decode limit: the stored bytes.
        DecodedLimit,
    }

    // What verify did with the chunked framing of a captured body. A replay's response is never framed (the HTTP stack
    // removes its framing), so its framing is always None.
    internal enum VerifyFraming
    {
        // Transfer-Encoding did not end with chunked, or the body did not look framed.
        None,

        // The framing was removed.
        Removed,

        // The body looked framed, but its framing was not removed: it did not parse, or its data is larger than the verify
        // decode limit. The bytes were compared with their framing.
        NotRemoved,
    }

    // Whether a Decoded side's content is known to be all of the content, as the body preview's decoder checks it within its
    // ceilings (BodyPreviewOperation.InspectDecode): verify's own decoders end quietly on truncated input. It says nothing
    // about the HTTP message. NotApplicable for the other bases, whose compared bytes are the stored ones.
    internal enum VerifyDecodeEnd
    {
        NotApplicable,

        // The decoded content is all of the content.
        Complete,

        // The compressed content ends early: only its start was compared.
        Incomplete,

        // Where the content ends is not proven: something follows a first gzip member, the check reached a ceiling, or it
        // did not decode the content as verify did.
        Unproven,
    }

    // One side of a verification as ReplayVerifier compared it. Compared is exactly what the verdict compared; Stored and the
    // header values are the message's own, which the end check and the text rules read. Borrowed while the comparison runs
    // and never kept: the original's bytes belong to the capture.
    internal sealed record VerifyBody(
        ReadOnlyMemory<byte> Compared,
        VerifyBodyBasis Basis,
        VerifyFraming Framing,
        ReadOnlyMemory<byte> Stored,
        string ContentType,
        string ContentEncoding,
        string TransferEncoding);

    // What one verification compared: its verdict, each side's status and body. OriginalStatus is the captured status as a
    // number, null when it is not one. ReplayVerifier.CompareSides builds it, and VerifyDiffOperation reads it while it
    // builds the detail; neither keeps it.
    internal sealed record ReplayComparison(
        ReplayVerification Verification,
        int? OriginalStatus,
        int ActualStatus,
        VerifyBody Original,
        VerifyBody Actual);

    // The ceilings of one VerifyDiffOperation; a request may lower them, as tests do, and nothing raises them.
    // - MaxExaminedBytes: the most of each side's compared bytes that the detail examines (the body preview's decoded
    //   ceiling). MaxLines and MaxJsonNodes: the most text lines and JSON values of each side that it compares.
    // - MaxChars: every character of the detail together (text lines, JSON paths and values, byte excerpts). MaxValueChars:
    //   one line, path or value, which is then cut and marked so.
    // - MaxChanges: the JSON changes, and separately the byte ranges. MaxHunks and ContextLines: the text hunks.
    // - MaxSteps: line and JSON comparisons (each line compared, JSON token read, JSON value compared and member name compared
    //   is a step). MaxWork: the bytes and characters
    //   read, decoded, hashed and compared, the end checks included. MaxScratchBytes: what the comparisons allocate (the
    //   decoded text of each side, line tables, diff frontiers, JSON value tables, names and strings), reserved before each
    //   allocation: a side whose text does not fit is shown as bytes.
    // - MaxSerializedBytes: one detail as VerifyDiffJson writes it, at least MinSerializedBytes, which the metadata-only
    //   fallback (Unavailable, without Body) always fits. MaxRetainedBytes: the details that the GUI keeps for the rows of
    //   its Replays tab, earlier runs included, counted as VerifyDiff.SerializedBytes: a payload budget, not a measure of
    //   memory.
    internal sealed record VerifyDiffLimits(
        int MaxExaminedBytes = 4 * 1024 * 1024,
        int MaxLines = 4096,
        int MaxJsonNodes = 2048,
        int MaxJsonDepth = 64,
        int MaxChars = 64 * 1024,
        int MaxValueChars = 1024,
        int MaxChanges = 256,
        int MaxHunks = 128,
        int ContextLines = 3,
        long MaxSteps = 1_000_000,
        long MaxWork = 64L * 1024 * 1024,
        long MaxScratchBytes = 16L * 1024 * 1024,
        int MaxSerializedBytes = 256 * 1024)
    {
        internal const long MaxRetainedBytes = 16L * 1024 * 1024;

        // The least MaxSerializedBytes: the largest metadata-only fallback fits it.
        internal const int MinSerializedBytes = 2048;

        // The bytes of each side that a byte range shows.
        internal const int ExcerptBytes = 16;

        internal static VerifyDiffLimits Default { get; } = new();

        // Every limit between its least useful value and its default.
        internal VerifyDiffLimits Clamped() => new(
            Math.Clamp(MaxExaminedBytes, 1, Default.MaxExaminedBytes),
            Math.Clamp(MaxLines, 1, Default.MaxLines),
            Math.Clamp(MaxJsonNodes, 1, Default.MaxJsonNodes),
            Math.Clamp(MaxJsonDepth, 1, Default.MaxJsonDepth),
            Math.Clamp(MaxChars, 1, Default.MaxChars),
            Math.Clamp(MaxValueChars, 1, Default.MaxValueChars),
            Math.Clamp(MaxChanges, 1, Default.MaxChanges),
            Math.Clamp(MaxHunks, 1, Default.MaxHunks),
            Math.Clamp(ContextLines, 0, Default.ContextLines),
            Math.Clamp(MaxSteps, 1, Default.MaxSteps),
            Math.Clamp(MaxWork, 1, Default.MaxWork),
            Math.Clamp(MaxScratchBytes, 1, Default.MaxScratchBytes),
            Math.Clamp(MaxSerializedBytes, MinSerializedBytes, Default.MaxSerializedBytes));
    }

    // The send to explain, as ReplayOperation holds it: the captured exchange, the replay's, and verify's decode limit. The
    // operation compares them itself, inside its gate, so that the verdict and the detail come from one comparison.
    internal sealed record VerifyDiffRequest(
        ProxyConnectionModel Original,
        ReplayExchange Actual,
        long MaxDecodedBytes,
        VerifyDiffLimits Limits = null);

    internal enum VerifyDiffState
    {
        // Every difference of the compared bytes is shown, or they are equal; each side's compared bytes were examined in
        // full where they differ, and each Decoded side's end is proven. Complete covers the compared bytes only: the sides'
        // bases say how verify obtained them.
        Complete,

        // Some of the differences are shown, or a side was not examined in full or its end is not proven: Limitations say
        // why.
        Limited,

        // No body differences are shown (Body is null): Limitations say why. The verdict, the statuses and the sides are
        // still there.
        Unavailable,
    }

    // Why a detail is Limited or Unavailable. Codes, never content: the GUI words them.
    internal static class VerifyDiffLimitation
    {
        // A Decoded side's compressed content ends early (VerifyDecodeEnd.Incomplete).
        internal const string OriginalIncomplete = "verify.diff.original-incomplete";

        internal const string ActualIncomplete = "verify.diff.actual-incomplete";

        // Where a Decoded side's content ends is not proven (VerifyDecodeEnd.Unproven).
        internal const string OriginalEndUnproven = "verify.diff.original-end-unproven";

        internal const string ActualEndUnproven = "verify.diff.actual-end-unproven";

        // A side's compared bytes are longer than MaxExaminedBytes: only their start was examined.
        internal const string ExaminedLimit = "verify.diff.examined-limit";

        // A side has more text lines than MaxLines: only the first ones were compared.
        internal const string LineLimit = "verify.diff.line-limit";

        // The JSON view stopped at MaxJsonDepth or MaxJsonNodes.
        internal const string JsonLimit = "verify.diff.json-limit";

        internal const string StepLimit = "verify.diff.step-limit";

        internal const string WorkLimit = "verify.diff.work-limit";

        internal const string ScratchLimit = "verify.diff.scratch-limit";

        internal const string ChangeLimit = "verify.diff.change-limit";

        internal const string HunkLimit = "verify.diff.hunk-limit";

        internal const string CharLimit = "verify.diff.char-limit";

        // A line, path or value was cut at MaxValueChars; each cut one is marked.
        internal const string ValueCut = "verify.diff.value-cut";

        // The detail did not fit MaxSerializedBytes: its body differences were left out (Unavailable).
        internal const string SerializedLimit = "verify.diff.serialized-limit";

        // The detail could not be built (Unavailable). The verdict is unchanged.
        internal const string Internal = "verify.diff.internal";
    }

    // One side in a detail: how verify got its bytes and whether a Decoded side's end is proven, how many bytes it compared
    // and how many of them the detail examined (none when they are equal), and the charset that its text was read with, or
    // null when it was not read as text.
    internal sealed record VerifyDiffSide(
        VerifyBodyBasis Basis,
        VerifyFraming Framing,
        VerifyDecodeEnd End,
        long ComparedBytes,
        long ExaminedBytes,
        string Charset);

    // Whether two sides whose bytes differ are the same at another level: as text, or as JSON values.
    internal enum VerifyEquivalence
    {
        // They differ there too.
        Different,

        // They are the same there: only their bytes differ.
        Same,

        // Not known: a side was compared only in part, or the comparison stopped at a limit before it found a difference.
        Unknown,
    }

    internal enum VerifyLineKind
    {
        Context,
        Removed,
        Added,
    }

    // A line of text and the break that ends it ("\r\n", "\n", "\r", "\u2028" or "\u2029"; empty for a last line without
    // one), so lines that differ only in their break differ. Cut: Text is only the start of the line (MaxValueChars).
    internal sealed record VerifyTextLine(VerifyLineKind Kind, string Text, string Break, bool Cut);

    // Lines of a text difference, numbered from 1, with up to ContextLines unchanged lines around them. A count of 0 starts
    // after the line it names, as in a unified diff.
    internal sealed record VerifyTextHunk(
        int OriginalStart, int OriginalCount, int ActualStart, int ActualCount, IReadOnlyList<VerifyTextLine> Lines);

    // Equivalence: Same when the two texts are equal, breaks included, so that only their charset or byte order mark
    // differs (Hunks is then empty); Different when they differ, as the hunks show; Unknown when the lines compared are equal
    // but a side was not compared in full.
    internal sealed record VerifyTextDiff(VerifyEquivalence Equivalence, IReadOnlyList<VerifyTextHunk> Hunks);

    // A run of differing bytes at the same offset of both sides, or the bytes that only the longer side has (the other
    // length 0). Original and Actual: hex of at most ExcerptBytes bytes from the start of each side's run, read only from
    // the bytes examined, so shorter than the run when the run is longer or was not examined.
    internal sealed record VerifyByteRange(long Offset, long OriginalLength, long ActualLength, string Original, string Actual);

    // Byte differences: both compared lengths and the differing runs that the examined bytes show, in offset order.
    internal sealed record VerifyByteDiff(long OriginalLength, long ActualLength, IReadOnlyList<VerifyByteRange> Ranges);

    internal enum VerifyJsonChangeKind
    {
        Changed,
        Added,
        Removed,
    }

    // A JSON value that differs: its path ("$", then .name, ["name"] or [index]) and the JSON text of each side's value as
    // written, numbers exactly; null for the side that does not have it. PathCut, OriginalCut and ActualCut: that text is only
    // its start (MaxValueChars), so a cut path is not a path to use and a cut value is not a JSON value.
    internal sealed record VerifyJsonChange(
        string Path,
        bool PathCut,
        VerifyJsonChangeKind Kind,
        string Original,
        bool OriginalCut,
        string Actual,
        bool ActualCut);

    // How the bytes of two JSON documents with equal values differ. At least one applies.
    internal static class VerifyJsonExplanation
    {
        // The whitespace between tokens, or a byte order mark, differs.
        internal const string Formatting = "formatting";

        // An object has the same members in another order.
        internal const string MemberOrder = "member-order";

        // A string or member name is written with other escapes for the same text.
        internal const string Escapes = "escapes";
    }

    // Values: Same when the documents' values are equal (objects by member name, case-sensitive; arrays by index; numbers by
    // their exact text; strings by their unescaped text), with Explanations of how their bytes differ; Different with the
    // Changes; Unknown when the comparison stopped at a limit before it found a change.
    internal sealed record VerifyJsonDiff(
        VerifyEquivalence Values, IReadOnlyList<string> Explanations, IReadOnlyList<VerifyJsonChange> Changes);

    // Why there is no JSON view although a side's media type says JSON or its body starts like JSON.
    internal static class VerifyJsonUnavailable
    {
        internal const string Invalid = "invalid";

        // A member name appears twice in one object: comparing by name would hide one of them.
        internal const string DuplicateKeys = "duplicate-keys";

        internal const string MultipleDocuments = "multiple-documents";

        // A side's text is not UTF-8 or US-ASCII, which the body preview's JSON views also require.
        internal const string Charset = "charset";

        // A side is incomplete, its end unproven or examined only in part, or not read as text.
        internal const string NotWhole = "not-whole";

        internal const string DepthLimit = "depth-limit";

        internal const string NodeLimit = "node-limit";

        // The steps, the work or the scratch ran out.
        internal const string Limit = "limit";
    }

    // The body part of a detail. Equal: the compared bytes are equal, and nothing else is set. Otherwise Text when both
    // sides' bases are Plain or Decoded and the examined bytes of both read as text by the body preview's rules
    // (BodyTextRules), Binary when not; and Json too when both sides are whole, valid, single JSON documents, or
    // JsonUnavailable when a side looked like JSON but is not that.
    internal sealed record VerifyBodyDiff(
        bool Equal,
        VerifyTextDiff Text,
        VerifyByteDiff Binary,
        VerifyJsonDiff Json,
        string JsonUnavailable);

    // The detail of one verification (VerifyDiffOperation): the verdict as ReplayVerifier made it, which the detail never
    // changes; the status of each side; each side's body as verify compared it; and the body differences, bounded by
    // VerifyDiffLimits, null when Unavailable. It holds no bytes of either message: only the text, values and excerpts that
    // it shows. SerializedBytes is its size as VerifyDiffJson writes it (diff.json).
    internal sealed record VerifyDiff(
        VerifyDiffState State,
        IReadOnlyList<string> Limitations,
        ReplayVerification Verification,
        int? OriginalStatus,
        int ActualStatus,
        VerifyDiffSide Original,
        VerifyDiffSide Actual,
        VerifyBodyDiff Body,
        int SerializedBytes)
    {
        // diff.json's "schema" and "version".
        internal const string Schema = "xmat.verify-diff";

        internal const int SchemaVersion = 1;
    }
}
