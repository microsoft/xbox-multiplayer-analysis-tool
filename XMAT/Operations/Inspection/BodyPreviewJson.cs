// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XMAT.Operations.Inspection
{
    // Text that never grows past its limits: at most maxChars characters on at most maxLines lines. What does not fit is cut,
    // never in the middle of a surrogate pair, and then nothing more is appended.
    internal sealed class BodyPreviewText
    {
        private readonly StringBuilder _text = new();
        private readonly int _maxChars;
        private readonly int _maxBreaks;
        private int _breaks;

        internal BodyPreviewText(int maxChars, int maxLines)
        {
            _maxChars = maxChars;
            _maxBreaks = Math.Max(0, maxLines - 1);
        }

        // Whether something did not fit.
        internal bool Cut { get; private set; }

        // How many more characters fit.
        internal int Remaining => Cut ? 0 : _maxChars - _text.Length;

        // Appends text, or as much of it as fits; false once something did not fit.
        internal bool Append(ReadOnlySpan<char> text)
        {
            if (Cut)
            {
                return false;
            }

            int allowed = Math.Min(text.Length, _maxChars - _text.Length);
            int breaks = _breaks;
            for (int index = 0; index < allowed; index++)
            {
                if (text[index] == '\n')
                {
                    if (breaks == _maxBreaks)
                    {
                        allowed = index;
                        break;
                    }

                    breaks++;
                }
            }

            if (allowed < text.Length && allowed > 0 && char.IsHighSurrogate(text[allowed - 1]))
            {
                allowed--;
            }

            _text.Append(text[..allowed]);
            _breaks = breaks;
            if (allowed < text.Length)
            {
                Cut = true;
                return false;
            }

            return true;
        }

        // Records that the caller left out text that did not fit.
        internal void MarkCut() => Cut = true;

        public override string ToString() => _text.ToString();
    }

    // The JSON documents of a completely read body, as a tree and as formatted text, each within the preview's limits.
    // Several documents in a row (NDJSON) share one budget.
    internal static class BodyPreviewJson
    {
        // A long name, string or number is decoded from at most this many bytes before its label is cut.
        private const int MaxLabelBytes = 4 * BodyPreviewNode.MaxJsonValueChars;

        private static readonly string Indentation = new(' ', 2 * BodyPreviewLimits.Default.MaxJsonDepth);

        internal enum TreeEnd
        {
            Complete,
            Limit,
            Malformed,
            NotJson,
        }

        internal enum FormatEnd
        {
            Complete,
            TextLimit,
            DepthLimit,
            Malformed,
        }

        // The documents as nodes. A limit keeps the nodes so far, the open document closed where it stopped (Limit); invalid
        // JSON drops the open document and keeps the complete ones before it (Malformed, or NotJson when there are none).
        internal static IReadOnlyList<BodyPreviewNode> BuildTree(
            ReadOnlySpan<byte> json, BodyPreviewLimits limits, BodyPreviewWork work, out TreeEnd end)
        {
            var roots = new List<BodyPreviewNode>();
            var open = new List<Container>();
            string name = null;
            int nodes = 0;
            long chars = 0;
            var reader = new Utf8JsonReader(json, Options(limits));
            try
            {
                while (reader.Read())
                {
                    work.Spend(reader.ValueSpan.Length + 1);
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            name = Label(ref reader);
                            break;

                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            string label = ChildLabel(open, name);
                            name = null;
                            if (open.Count >= limits.MaxJsonDepth || !Admit(label))
                            {
                                end = TreeEnd.Limit;
                                CloseAll();
                                return roots;
                            }

                            open.Add(new Container(label, reader.TokenType == JsonTokenType.StartArray));
                            break;

                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            Container closed = open[^1];
                            open.RemoveAt(open.Count - 1);
                            Add(new BodyPreviewNode(closed.Label, closed.Children));
                            break;

                        default:
                            string value = ChildLabel(open, name) + "=" + Label(ref reader);
                            name = null;
                            if (!Admit(value))
                            {
                                end = TreeEnd.Limit;
                                CloseAll();
                                return roots;
                            }

                            Add(new BodyPreviewNode(value, Array.Empty<BodyPreviewNode>()));
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                end = roots.Count == 0 ? TreeEnd.NotJson : TreeEnd.Malformed;
                return roots;
            }

            end = roots.Count == 0 ? TreeEnd.NotJson : TreeEnd.Complete;
            return roots;

            bool Admit(string label)
            {
                if (nodes >= limits.MaxJsonNodes || chars + label.Length > limits.MaxJsonChars)
                {
                    return false;
                }

                nodes++;
                chars += label.Length;
                return true;
            }

            void Add(BodyPreviewNode node)
            {
                if (open.Count == 0)
                {
                    roots.Add(node);
                }
                else
                {
                    open[^1].Children.Add(node);
                }
            }

            void CloseAll()
            {
                while (open.Count > 0)
                {
                    Container container = open[^1];
                    open.RemoveAt(open.Count - 1);
                    Add(new BodyPreviewNode(container.Label, container.Children));
                }
            }
        }

        // The documents indented by two spaces a level, one after another, names and values written as they are in the body,
        // escapes included. A text limit keeps the formatted start (TextLimit); a document deeper than MaxJsonDepth
        // (DepthLimit) or invalid JSON (Malformed) leaves the caller to show the body as it is.
        internal static FormatEnd Format(
            ReadOnlySpan<byte> json, BodyPreviewLimits limits, BodyPreviewWork work, BodyPreviewText text)
        {
            var reader = new Utf8JsonReader(json, Options(limits));
            var hasElements = new List<bool>();
            bool afterName = false;
            bool started = false;
            try
            {
                while (reader.Read())
                {
                    work.Spend(reader.ValueSpan.Length + 1);
                    JsonTokenType token = reader.TokenType;
                    if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    {
                        bool hadElements = hasElements[^1];
                        hasElements.RemoveAt(hasElements.Count - 1);
                        if ((hadElements && !NewLine(text, hasElements.Count)) ||
                            !text.Append(token == JsonTokenType.EndObject ? "}" : "]"))
                        {
                            return FormatEnd.TextLimit;
                        }

                        continue;
                    }

                    if (!afterName)
                    {
                        if (hasElements.Count == 0)
                        {
                            if (started && !text.Append("\r\n"))
                            {
                                return FormatEnd.TextLimit;
                            }

                            started = true;
                        }
                        else
                        {
                            if ((hasElements[^1] && !text.Append(",")) || !NewLine(text, hasElements.Count))
                            {
                                return FormatEnd.TextLimit;
                            }

                            hasElements[^1] = true;
                        }
                    }

                    afterName = false;
                    switch (token)
                    {
                        case JsonTokenType.PropertyName:
                            if (!Quoted(text, reader.ValueSpan) || !text.Append(": "))
                            {
                                return FormatEnd.TextLimit;
                            }

                            afterName = true;
                            break;

                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            if (hasElements.Count >= limits.MaxJsonDepth)
                            {
                                return FormatEnd.DepthLimit;
                            }

                            if (!text.Append(token == JsonTokenType.StartObject ? "{" : "["))
                            {
                                return FormatEnd.TextLimit;
                            }

                            hasElements.Add(false);
                            break;

                        case JsonTokenType.String:
                            if (!Quoted(text, reader.ValueSpan))
                            {
                                return FormatEnd.TextLimit;
                            }

                            break;

                        default:
                            if (!Raw(text, reader.ValueSpan))
                            {
                                return FormatEnd.TextLimit;
                            }

                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return FormatEnd.Malformed;
            }

            return FormatEnd.Complete;
        }

        // Whether the body starts like a JSON object or array, after any whitespace.
        internal static bool LooksLikeJson(ReadOnlySpan<byte> body)
        {
            int start = body.IndexOfAnyExcept(" \t\r\n"u8);
            return start >= 0 && body[start] is (byte)'{' or (byte)'[';
        }

        // Each preview reader allows one level more than the limit, so the preview, not the reader, stops at the limit.
        private static JsonReaderOptions Options(BodyPreviewLimits limits) => new()
        {
            AllowMultipleValues = true,
            AllowTrailingCommas = true,
            MaxDepth = limits.MaxJsonDepth + 1,
        };

        private static string ChildLabel(List<Container> open, string name)
        {
            if (open.Count == 0)
            {
                return "[root]";
            }

            Container parent = open[^1];
            return parent.IsArray ? (parent.NextIndex++).ToString(CultureInfo.InvariantCulture) : name ?? string.Empty;
        }

        // A name or value as a label: strings and names unescaped, other values as written; long ones decoded from their
        // first MaxLabelBytes bytes as written, escapes included, and cut to MaxJsonValueChars.
        private static string Label(ref Utf8JsonReader reader)
        {
            ReadOnlySpan<byte> raw = reader.ValueSpan;
            string text = reader.TokenType switch
            {
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => "null",
                JsonTokenType.String or JsonTokenType.PropertyName when raw.Length <= MaxLabelBytes => reader.GetString(),
                _ => Utf8Prefix(raw, MaxLabelBytes),
            };
            return Cut(text, BodyPreviewNode.MaxJsonValueChars);
        }

        private static string Cut(string text, int maxChars)
        {
            if (text.Length <= maxChars)
            {
                return text;
            }

            int length = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
            return string.Concat(text.AsSpan(0, length), "\u2026");
        }

        // The first maxBytes bytes, or fewer to end on a whole UTF-8 character.
        private static string Utf8Prefix(ReadOnlySpan<byte> raw, int maxBytes)
        {
            int length = Math.Min(raw.Length, maxBytes);
            while (length > 0 && length < raw.Length && (raw[length] & 0xC0) == 0x80)
            {
                length--;
            }

            return Encoding.UTF8.GetString(raw[..length]);
        }

        private static bool NewLine(BodyPreviewText text, int depth) =>
            text.Append("\r\n") && text.Append(Indentation.AsSpan(0, Math.Min(Indentation.Length, 2 * depth)));

        private static bool Quoted(BodyPreviewText text, ReadOnlySpan<byte> raw) =>
            text.Append("\"") && Raw(text, raw) && text.Append("\"");

        // The token as written, decoded from no more bytes than can fit.
        private static bool Raw(BodyPreviewText text, ReadOnlySpan<byte> raw)
        {
            int take = Math.Min(raw.Length, text.Remaining + 4);
            if (!text.Append(Utf8Prefix(raw, take)))
            {
                return false;
            }

            if (take < raw.Length)
            {
                text.MarkCut();
                return false;
            }

            return true;
        }

        private sealed class Container
        {
            internal Container(string label, bool isArray)
            {
                Label = label;
                IsArray = isArray;
            }

            internal string Label { get; }

            internal bool IsArray { get; }

            internal int NextIndex { get; set; }

            internal List<BodyPreviewNode> Children { get; } = new();
        }
    }
}
