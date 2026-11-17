// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // Why Parsed to Raw refused (RawConversion.FromDraft), in its order of checks. The GUI refuses omissions, a numbered
    // URL and options that Raw mode never uses itself, before this.
    internal enum RawFromDraftRefusal
    {
        Url,
        Target,
        Method,
        Headers,
        Limits,
    }

    // What a conversion changed that the other mode cannot keep, each disclosed with a fixed text.
    internal enum RawDisclosureKind
    {
        // Parsed to Raw.
        LeftOut,
        HostWritten,
        LengthWritten,
        Spacing,
        TargetEscaped,
        FormParts,

        // Raw to Parsed.
        ChunkedDecoded,
        AbsoluteTarget,
        TargetNormalized,
        NotSentByParsed,
        VersionAutomatic,
    }

    // One disclosure and its detail from the request, raw: a list of header names (", " between them), an authority, a
    // length or a target. The GUI bounds and escapes it.
    internal sealed record RawDisclosure(RawDisclosureKind Kind, string Detail);

    // Parsed to Raw's result: the target and its text for the target box, the head text for the raw editor, and what was
    // disclosed. An editor-only value: it may hold fields that Raw refuses, or text outside ASCII, and only Prepare makes
    // an image of it, at Send.
    internal sealed record RawFromDraft(
        ReplayRawTarget Target, string TargetText, string HeadText, IReadOnlyList<RawDisclosure> Disclosures);

    internal sealed record RawFromDraftResult(RawFromDraft Value, RawFromDraftRefusal? Refusal);

    // Raw to Parsed's result: the draft for the Parsed editors and what was disclosed.
    internal sealed record RawToDraft(RequestReplayDraft Draft, IReadOnlyList<RawDisclosure> Disclosures);

    // The draft, or a section 3 code when it refused.
    internal sealed record RawToDraftResult(RawToDraft Value, string Code);

    // The conversions between the composer's two modes. They live in the replay core, not the window, so that the GUI
    // and its tests run the same code. Neither makes a send-admitted image.
    internal static class RawConversion
    {
        // The header lines that Parsed mode never sends, besides Host and Content-Length, which it writes anew, and the
        // fields that Connection nominates. Upgrade is not among them: it is kept as typed, and Raw refuses it at Send.
        private static readonly string[] NotSentByParsed =
        {
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "Proxy-Connection",
            "TE",
            "Trailer",
            "Transfer-Encoding",
        };

        // The header preparation budget of Parsed to Raw: the distinct names, in any case, of the lines
        // that Parsed mode may send, each counted once before Connection nominations are applied, and their characters
        // (names are tokens, so characters are bytes). Host, Content-Length and NotSentByParsed's fields are not among
        // them, and values and nomination tokens never count. More is Limits.
        internal const int MaxCandidateNames = 1_024;
        internal const int MaxCandidateNameBytes = 262_144;

        // Parsed to Raw: header validation and rendering. bodyLength is the count that TryCountBody made of
        // the draft's body on the worker; formBody says that the body is a Build-form body, whose parts are not kept. The
        // body itself is not rendered: it stays the draft's bytes and their editor. The header text is read in place, in
        // three passes that copy nothing of it: the first checks every line and holds the candidates' names within
        // their budget; the second marks the candidates that a Connection line nominates, before or after them; the third
        // renders, checking each line against Raw mode's limits before it is written. Each scan checks the token, and calls
        // checkpoint (tests), each time another RawRequest.CheckChars characters have been read.
        internal static RawFromDraftResult FromDraft(
            RequestReplayDraft draft,
            long bodyLength,
            bool formBody,
            CancellationToken token,
            Action<long> checkpoint = null)
        {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
            token.ThrowIfCancellationRequested();
            string url = draft.Url ?? string.Empty;

            // A URL longer than a request file holds is refused before any of it is parsed, so that the parsing
            // below, which cannot be cancelled, is bounded; the token is checked after each step of it.
            if (url.Length > RequestFileLimits.MaxUrlChars ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || url.Contains('#'))
            {
                return Refused(RawFromDraftRefusal.Url);
            }

            token.ThrowIfCancellationRequested();
            ReplayRawTarget target = ReplayRawTarget.FromUrl(uri);
            if (target == null)
            {
                return Refused(RawFromDraftRefusal.Target);
            }

            token.ThrowIfCancellationRequested();
            string method = draft.Method ?? string.Empty;
            if (method.Length == 0 || !IsToken(method, checkpoint, token))
            {
                return Refused(RawFromDraftRefusal.Method);
            }

            string headersText = draft.HeadersText ?? string.Empty;
            string pathAndQuery = uri.PathAndQuery;
            token.ThrowIfCancellationRequested();
            long requestLineBytes = method.Length + 1L + pathAndQuery.Length + " HTTP/1.1\r\n".Length;
            bool over = bodyLength > RawRequest.MaxEntityBytes || method.Length > RawRequest.MaxMethodChars ||
                requestLineBytes > RawRequest.MaxRequestLineBytes;
            var candidates = new Dictionary<string, CandidateState>(StringComparer.OrdinalIgnoreCase);
            var held = candidates.GetAlternateLookup<ReadOnlySpan<char>>();
            int candidateNameBytes = 0;
            bool connection = false;
            try
            {
                // Every line is checked, even once a limit is passed, so that Headers comes before Limits.
                var lines = new RequestReplayDraft.HeaderLineReader(headersText, checkpoint, token);
                while (lines.Read(out ReadOnlySpan<char> name, out _, out _))
                {
                    connection |= name.Equals("Connection", StringComparison.OrdinalIgnoreCase);
                    if (!over && IsCandidate(name))
                    {
                        over = !Hold(name, held, ref candidateNameBytes);
                    }
                }
            }
            catch (FormatException)
            {
                return Refused(RawFromDraftRefusal.Headers);
            }

            if (over)
            {
                return Refused(RawFromDraftRefusal.Limits);
            }

            if (connection && candidates.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var lines = new RequestReplayDraft.HeaderLineReader(headersText, checkpoint, token);
                while (lines.Read(out ReadOnlySpan<char> name, out _, out ReadOnlySpan<char> value))
                {
                    if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    {
                        Nominate(value, held, checkpoint, token);
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            string length = bodyLength.ToString(CultureInfo.InvariantCulture);
            string lengthLine = "Content-Length: " + length;
            var head = new StringBuilder();
            head.Append(method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\nHost: ").Append(target.Authority);
            int fieldLines = 1;
            var leftOut = new List<string>();
            var fixedLeftOut = new bool[NotSentByParsed.Length];
            bool hostReplaced = false;
            bool lengthReplaced = false;
            bool lengthWritten = false;
            bool spacing = false;
            int hosts = 0;
            int lengths = 0;
            var render = new RequestReplayDraft.HeaderLineReader(headersText, checkpoint, token);
            while (render.Read(out ReadOnlySpan<char> name, out ReadOnlySpan<char> raw, out ReadOnlySpan<char> value))
            {
                int fixedField;
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    hostReplaced |= ++hosts > 1 || !value.Equals(target.Authority, StringComparison.Ordinal);
                }
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    lengthReplaced |= ++lengths > 1 || !value.Equals(length, StringComparison.Ordinal);
                    if (!lengthWritten)
                    {
                        if (!Fits(head, fieldLines, 0, lengthLine.Length))
                        {
                            return Refused(RawFromDraftRefusal.Limits);
                        }

                        head.Append("\r\n").Append(lengthLine);
                        fieldLines++;
                        lengthWritten = true;
                    }
                }
                else if ((fixedField = FixedField(name)) >= 0)
                {
                    if (!fixedLeftOut[fixedField])
                    {
                        fixedLeftOut[fixedField] = true;
                        leftOut.Add(name.ToString());
                    }
                }
                else if (held.TryGetValue(name, out string heldName, out CandidateState state) && state != CandidateState.Sent)
                {
                    if (state == CandidateState.Nominated)
                    {
                        candidates[heldName] = CandidateState.LeftOut;
                        leftOut.Add(heldName);
                    }
                }
                else
                {
                    // The value was trimmed of the spaces and tabs around it: kept as typed only when nothing was trimmed but
                    // the one space after the colon.
                    spacing |= raw.Length != value.Length && !(raw.Length == value.Length + 1 && raw[0] == ' ');
                    if (!Fits(head, fieldLines, lengthWritten ? 0 : 2 + lengthLine.Length, name.Length + 2 + value.Length))
                    {
                        return Refused(RawFromDraftRefusal.Limits);
                    }

                    head.Append("\r\n").Append(name).Append(": ").Append(value);
                    fieldLines++;
                }
            }

            if (!lengthWritten)
            {
                if (!Fits(head, fieldLines, 0, lengthLine.Length))
                {
                    return Refused(RawFromDraftRefusal.Limits);
                }

                head.Append("\r\n").Append(lengthLine);
            }

            // Raw mode's own count of the head decides; the checks above only stop it from growing past the limits.
            string headText = head.ToString();
            if (RawRequest.CountHead(headText).OverLimit)
            {
                return Refused(RawFromDraftRefusal.Limits);
            }

            token.ThrowIfCancellationRequested();
            var disclosures = new List<RawDisclosure>();
            if (leftOut.Count > 0)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.LeftOut, string.Join(", ", leftOut)));
            }

            if (hostReplaced)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.HostWritten, target.Authority));
            }

            if (lengthReplaced)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.LengthWritten, length));
            }

            if (spacing)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.Spacing, null));
            }

            if (!string.Equals(TypedPathAndQuery(url), pathAndQuery, StringComparison.Ordinal))
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.TargetEscaped, pathAndQuery));
            }

            if (formBody)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.FormParts, null));
            }

            return new RawFromDraftResult(
                new RawFromDraft(target, target.Scheme + "://" + target.Authority, headText, disclosures.AsReadOnly()),
                null);
        }

        // Raw to Parsed: the method; the target's origin with the origin form as the URL; every field
        // line as written but Host and Content-Length, which Parsed mode writes with the same values, and
        // Transfer-Encoding when the chunked body is decoded; and the entity, shown as FromCapture shows a body. The
        // provenance's captured origin and stored-text source are kept, so a mode switch can launder neither; without a
        // provenance, the source is Unknown, never Literal.
        internal static RawToDraftResult ToDraft(
            RawEditorParse parse,
            RequestReplayDraft provenance,
            int maxShownBodyBytes,
            bool versionAutomatic,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(parse);
            ArgumentOutOfRangeException.ThrowIfNegative(maxShownBodyBytes);
            token.ThrowIfCancellationRequested();
            ReplayRawTarget target = parse.Target;
            string url = target.Scheme + "://" + target.Authority + parse.OriginForm;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                return new RawToDraftResult(null, RawCodes.Target);
            }

            bool chunked = parse.Framing == RawFraming.Chunked;
            HashSet<string> nominated = RequestReplayDraft.ConnectionNominations(parse.Fields);
            var lines = new List<string>();
            var notSent = new List<string>();
            for (int index = 0; index < parse.Fields.Count; index++)
            {
                string name = parse.Fields[index].Key;
                if (Is(name, "Host") || Is(name, "Content-Length") || (chunked && Is(name, "Transfer-Encoding")))
                {
                    continue;
                }

                lines.Add(parse.FieldText[index]);
                if ((NotSentByParsed.Any(field => Is(name, field)) || nominated.Contains(name)) &&
                    !notSent.Any(field => Is(field, name)))
                {
                    notSent.Add(name);
                }
            }

            var draft = new RequestReplayDraft
            {
                Method = parse.Method,
                Url = url,
                HeadersText = string.Join("\r\n", lines),
                CaptureTextSource = provenance?.CaptureTextSource ?? CaptureTextSource.Unknown,
            };
            string encoding = parse.Fields.FirstOrDefault(field => Is(field.Key, "Content-Encoding")).Value;
            bool encoded = encoding != null && !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase);
            draft.ShowBody(parse.Body.ToArray(), encoded, maxShownBodyBytes, token);
            if (provenance?.CapturedOrigin != null)
            {
                draft = draft.WithCapturedOrigin(provenance.CapturedOrigin);
            }

            token.ThrowIfCancellationRequested();
            var disclosures = new List<RawDisclosure>();
            if (chunked)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.ChunkedDecoded, null));
            }

            if (parse.AbsoluteForm)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.AbsoluteTarget, null));
            }

            if (!string.Equals(uri.PathAndQuery, parse.OriginForm, StringComparison.Ordinal))
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.TargetNormalized, uri.PathAndQuery));
            }

            if (notSent.Count > 0)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.NotSentByParsed, string.Join(", ", notSent)));
            }

            if (versionAutomatic)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.VersionAutomatic, null));
            }

            return new RawToDraftResult(new RawToDraft(draft, disclosures.AsReadOnly()), null);
        }

        // What the third pass of FromDraft does with a held candidate's lines: send them, or leave them out because
        // Connection nominates them, disclosing the name once.
        private enum CandidateState : byte
        {
            Sent,
            Nominated,
            LeftOut,
        }

        // Whether the name is an HTTP token, read with the token checked every RawRequest.CheckChars characters.
        private static bool IsToken(string text, Action<long> checkpoint, CancellationToken token)
        {
            var progress = new ScanProgress(checkpoint, token);
            foreach (char character in text)
            {
                progress.Advance(1);
                if (!RequestReplayDraft.IsTokenCharacter(character))
                {
                    return false;
                }
            }

            return true;
        }

        // A line that Parsed mode may send: neither Host nor Content-Length, which it writes anew, nor a NotSentByParsed field.
        private static bool IsCandidate(ReadOnlySpan<char> name) =>
            !name.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
            FixedField(name) < 0;

        // The index of the NotSentByParsed field with this name, in any case, or -1.
        private static int FixedField(ReadOnlySpan<char> name)
        {
            for (int index = 0; index < NotSentByParsed.Length; index++)
            {
                if (name.Equals(NotSentByParsed[index], StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        // Holds a candidate's name once, in any case; false when a new name would pass MaxCandidateNames or
        // MaxCandidateNameBytes. A name longer than the whole budget cannot be held already, so it is not looked up.
        private static bool Hold(
            ReadOnlySpan<char> name,
            Dictionary<string, CandidateState>.AlternateLookup<ReadOnlySpan<char>> held,
            ref int heldBytes)
        {
            if (name.Length <= MaxCandidateNameBytes && held.ContainsKey(name))
            {
                return true;
            }

            if (held.Dictionary.Count == MaxCandidateNames || name.Length > MaxCandidateNameBytes - heldBytes)
            {
                return false;
            }

            held.TryAdd(name, CandidateState.Sent);
            heldBytes += name.Length;
            return true;
        }

        // Marks each held candidate that this Connection value nominates: the names between its commas, without the white
        // space around them, as ConnectionNominations reads them. The value is read once, in place; a name longer than the
        // whole budget cannot be held, so it is not looked up.
        private static void Nominate(
            ReadOnlySpan<char> value,
            Dictionary<string, CandidateState>.AlternateLookup<ReadOnlySpan<char>> held,
            Action<long> checkpoint,
            CancellationToken token)
        {
            var progress = new ScanProgress(checkpoint, token);
            int first = -1;
            int last = -1;
            for (int index = 0; index <= value.Length; index++)
            {
                // Every character is read, the commas too; the position after the last only ends the last name.
                if (index < value.Length)
                {
                    progress.Advance(1);
                }

                if (index == value.Length || value[index] == ',')
                {
                    ReadOnlySpan<char> name = first < 0 ? ReadOnlySpan<char>.Empty : value[first..(last + 1)];
                    if (!name.IsEmpty && name.Length <= MaxCandidateNameBytes && held.ContainsKey(name))
                    {
                        held[name] = CandidateState.Nominated;
                    }

                    first = -1;
                    continue;
                }

                if (!char.IsWhiteSpace(value[index]))
                {
                    first = first < 0 ? index : first;
                    last = index;
                }
            }
        }

        // Whether a head of these characters and field lines so far can take another field line of lineChars characters
        // within Raw mode's limits, with pendingChars more for a Content-Length line that is still to be written (its
        // CRLF included; 0 once it is written), and the head's last CRLF and empty line.
        private static bool Fits(StringBuilder head, int fieldLines, int pendingChars, int lineChars) =>
            fieldLines + 1 + (pendingChars > 0 ? 1 : 0) <= RawRequest.MaxFieldLines &&
            head.Length + 2L + lineChars + pendingChars + 4 <= RawRequest.MaxHeadBytes;

        // The path and query as the URL text has them, after its authority; the root path when it has none, since an
        // origin-form always starts with '/'.
        private static string TypedPathAndQuery(string url)
        {
            int authority = url.IndexOf("://", StringComparison.Ordinal) + 3;
            int end = url.IndexOfAny(new[] { '/', '?' }, authority);
            string rest = end < 0 ? string.Empty : url[end..];
            return rest.StartsWith('/') ? rest : "/" + rest;
        }

        private static bool Is(string name, string expected) =>
            string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);

        private static RawFromDraftResult Refused(RawFromDraftRefusal refusal) => new(null, refusal);
    }
}
