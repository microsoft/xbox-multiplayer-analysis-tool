// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XMAT.Operations.Replay
{
    // The JSON view of a detail: each side's text read strictly (no comments, no trailing commas) as one document, into a
    // table of at most MaxJsonNodes values no deeper than MaxJsonDepth, with every member name of an object once; then the
    // two documents compared as values. Objects compare by member name, case-sensitive; arrays by index; numbers by their
    // exact text; strings by their unescaped text. Each token read and each value compared is a step.
    internal static class VerifyDiffJsonValues
    {
        // The documents' values compared; null, with why, when there is no JSON view. bomDiffers says whether one text came
        // after a byte order mark and the other did not.
        internal static VerifyJsonDiff Compare(
            ReadOnlyMemory<byte> original,
            ReadOnlyMemory<byte> actual,
            bool bomDiffers,
            VerifyDiffBudget budget,
            out string unavailable)
        {
            Document a = Document.Read(original, budget, out unavailable);
            if (a == null)
            {
                return null;
            }

            Document b = Document.Read(actual, budget, out unavailable);
            if (b == null)
            {
                return null;
            }

            return new Comparer(a, b, budget).Run(bomDiffers);
        }

        private enum Kind : byte
        {
            Object,
            Array,
            String,
            Number,
            True,
            False,
            Null,
        }

        // A value: its JSON text from Start to End, its member name (an index into Names) or -1, for an escaped string its
        // unescaped text (an index into Strings) or -1, and its first child, last child and next sibling, or -1.
        private struct Node
        {
            internal Kind Kind;
            internal int Start;
            internal int End;
            internal int Name;
            internal int Unescaped;
            internal int First;
            internal int Last;
            internal int Next;
        }

        private sealed class Document
        {
            // Scratch for a node, and for a name's or string's entry beyond its characters.
            internal const int NodeBytes = 32;
            internal const int EntryBytes = 32;

            private int _count;

            private Document(ReadOnlyMemory<byte> json, int maxNodes)
            {
                Json = json;
                Nodes = new Node[maxNodes];
            }

            internal ReadOnlyMemory<byte> Json { get; }

            internal Node[] Nodes { get; }

            // Member names, unescaped, and where each is written, between its quotes.
            internal List<string> Names { get; } = new();

            internal List<(int Start, int Length)> NameText { get; } = new();

            internal List<string> Strings { get; } = new();

            // The bytes before each token, then after the last one: whitespace, commas and colons.
            internal List<(int Start, int Length)> Gaps { get; } = new();

            internal static Document Read(ReadOnlyMemory<byte> json, VerifyDiffBudget budget, out string unavailable)
            {
                VerifyDiffLimits limits = budget.Limits;
                unavailable = null;
                try
                {
                    // The pass reads every byte, whitespace included, then unescapes and hashes names and escaped strings,
                    // which are never longer than the text.
                    budget.SpendOrStop(2L * json.Length);
                    budget.ReserveOrStop((long)limits.MaxJsonNodes * NodeBytes);
                    var document = new Document(json, limits.MaxJsonNodes);
                    var reader = new Utf8JsonReader(
                        json.Span, new JsonReaderOptions { AllowMultipleValues = true, MaxDepth = limits.MaxJsonDepth + 1 });
                    var open = new List<(int Node, HashSet<string> Names)>();
                    int name = -1;
                    int roots = 0;
                    int previousEnd = 0;
                    while (reader.Read())
                    {
                        budget.StepOrStop();
                        int start = (int)reader.TokenStartIndex;
                        int end = start + TokenLength(ref reader);
                        budget.ReserveOrStop(2 * sizeof(int));
                        document.Gaps.Add((previousEnd, start - previousEnd));
                        previousEnd = end;
                        switch (reader.TokenType)
                        {
                            case JsonTokenType.PropertyName:
                                // Unescaped, a name is no longer than as written: reserved before it is allocated.
                                budget.ReserveOrStop((2L * reader.ValueSpan.Length) + EntryBytes);
                                string text = reader.GetString();
                                if (!open[^1].Names.Add(text))
                                {
                                    unavailable = VerifyJsonUnavailable.DuplicateKeys;
                                    return null;
                                }

                                document.Names.Add(text);
                                document.NameText.Add((start + 1, reader.ValueSpan.Length));
                                name = document.Names.Count - 1;
                                break;

                            case JsonTokenType.StartObject:
                            case JsonTokenType.StartArray:
                                if (open.Count >= limits.MaxJsonDepth)
                                {
                                    budget.Limit(VerifyDiffLimitation.JsonLimit);
                                    unavailable = VerifyJsonUnavailable.DepthLimit;
                                    return null;
                                }

                                bool isObject = reader.TokenType == JsonTokenType.StartObject;
                                int container = document.Add(
                                    isObject ? Kind.Object : Kind.Array, start, name, open, ref roots, budget, out unavailable);
                                if (container < 0)
                                {
                                    return null;
                                }

                                name = -1;
                                open.Add((container, isObject ? new HashSet<string>(StringComparer.Ordinal) : null));
                                break;

                            case JsonTokenType.EndObject:
                            case JsonTokenType.EndArray:
                                document.Nodes[open[^1].Node].End = end;
                                open.RemoveAt(open.Count - 1);
                                break;

                            default:
                                Kind kind = reader.TokenType switch
                                {
                                    JsonTokenType.String => Kind.String,
                                    JsonTokenType.Number => Kind.Number,
                                    JsonTokenType.True => Kind.True,
                                    JsonTokenType.False => Kind.False,
                                    _ => Kind.Null,
                                };
                                int value = document.Add(kind, start, name, open, ref roots, budget, out unavailable);
                                if (value < 0)
                                {
                                    return null;
                                }

                                name = -1;
                                document.Nodes[value].End = end;
                                if (kind == Kind.String && reader.ValueIsEscaped)
                                {
                                    budget.ReserveOrStop((2L * reader.ValueSpan.Length) + EntryBytes);
                                    document.Strings.Add(reader.GetString());
                                    document.Nodes[value].Unescaped = document.Strings.Count - 1;
                                }

                                break;
                        }
                    }

                    if (roots == 0)
                    {
                        unavailable = VerifyJsonUnavailable.Invalid;
                        return null;
                    }

                    budget.ReserveOrStop(2 * sizeof(int));
                    document.Gaps.Add((previousEnd, json.Length - previousEnd));
                    return document;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    // Not JSON, or a string that is not valid text, such as an escaped lone surrogate.
                    unavailable = VerifyJsonUnavailable.Invalid;
                    return null;
                }
                catch (VerifyDiffLimitException)
                {
                    unavailable = VerifyJsonUnavailable.Limit;
                    return null;
                }
            }

            // Adds a value under the innermost open container, or as a document; -1, with why, when it is a second document or
            // one value more than MaxJsonNodes.
            private int Add(
                Kind kind,
                int start,
                int name,
                List<(int Node, HashSet<string> Names)> open,
                ref int roots,
                VerifyDiffBudget budget,
                out string unavailable)
            {
                unavailable = null;
                if (open.Count == 0 && ++roots > 1)
                {
                    unavailable = VerifyJsonUnavailable.MultipleDocuments;
                    return -1;
                }

                if (_count == Nodes.Length)
                {
                    budget.Limit(VerifyDiffLimitation.JsonLimit);
                    unavailable = VerifyJsonUnavailable.NodeLimit;
                    return -1;
                }

                int index = _count++;
                Nodes[index] = new Node
                {
                    Kind = kind,
                    Start = start,
                    End = start,
                    Name = name,
                    Unescaped = -1,
                    First = -1,
                    Last = -1,
                    Next = -1,
                };
                if (open.Count > 0)
                {
                    int parent = open[^1].Node;
                    if (Nodes[parent].First < 0)
                    {
                        Nodes[parent].First = index;
                    }
                    else
                    {
                        Nodes[Nodes[parent].Last].Next = index;
                    }

                    Nodes[parent].Last = index;
                }

                return index;
            }

            // The length of the token itself: a string or name with its quotes, a number or literal as written, or one
            // structural character.
            private static int TokenLength(ref Utf8JsonReader reader) => reader.TokenType switch
            {
                JsonTokenType.PropertyName or JsonTokenType.String => reader.ValueSpan.Length + 2,
                JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null =>
                    reader.ValueSpan.Length,
                _ => 1,
            };
        }

        private sealed class Comparer
        {
            private readonly Document _a;
            private readonly Document _b;
            private readonly VerifyDiffBudget _budget;

            // Where the comparison is: a member name, or an array index when Name is null.
            private readonly List<(string Name, int Index)> _path = new();
            private readonly List<VerifyJsonChange> _changes = new();
            private bool _differs;
            private bool _orderDiffers;
            private bool _escapesDiffer;

            internal Comparer(Document a, Document b, VerifyDiffBudget budget)
            {
                _a = a;
                _b = b;
                _budget = budget;
            }

            internal VerifyJsonDiff Run(bool bomDiffers)
            {
                IReadOnlyList<string> noExplanations = Array.Empty<string>();
                IReadOnlyList<VerifyJsonChange> noChanges = Array.Empty<VerifyJsonChange>();
                bool stopped = false;
                try
                {
                    Values(0, 0);
                }
                catch (VerifyDiffLimitException)
                {
                    stopped = true;
                }

                if (_differs)
                {
                    // A change was found, whether or not the limits let the detail show it.
                    return new VerifyJsonDiff(VerifyEquivalence.Different, noExplanations, _changes.AsReadOnly());
                }

                if (stopped)
                {
                    // A limit stopped the comparison before it found a change: the values are not known to be equal.
                    return new VerifyJsonDiff(VerifyEquivalence.Unknown, noExplanations, noChanges);
                }

                var explanations = new List<string>();
                bool formatting = bomDiffers;
                try
                {
                    formatting = formatting || GapsDiffer();
                }
                catch (VerifyDiffLimitException)
                {
                    // The values are equal; whether the whitespace differs too is not known (WorkLimit).
                }

                if (formatting)
                {
                    explanations.Add(VerifyJsonExplanation.Formatting);
                }

                if (_orderDiffers)
                {
                    explanations.Add(VerifyJsonExplanation.MemberOrder);
                }

                if (_escapesDiffer)
                {
                    explanations.Add(VerifyJsonExplanation.Escapes);
                }

                return new VerifyJsonDiff(VerifyEquivalence.Same, explanations.AsReadOnly(), noChanges);
            }

            private void Values(int x, int y)
            {
                _budget.StepOrStop();
                Node p = _a.Nodes[x];
                Node q = _b.Nodes[y];
                if (p.Kind != q.Kind)
                {
                    Change(VerifyJsonChangeKind.Changed, x, y);
                    return;
                }

                switch (p.Kind)
                {
                    case Kind.Object:
                        Objects(x, y);
                        break;

                    case Kind.Array:
                        Arrays(x, y);
                        break;

                    case Kind.String:
                        if (!StringsEqual(p, q))
                        {
                            Change(VerifyJsonChangeKind.Changed, x, y);
                        }

                        break;

                    case Kind.Number:
                        if (!BytesEqual(_a, p.Start, p.End - p.Start, _b, q.Start, q.End - q.Start))
                        {
                            Change(VerifyJsonChangeKind.Changed, x, y);
                        }

                        break;
                }
            }

            private void Objects(int x, int y)
            {
                // The members are found by name: each name is hashed once into the table and once to look it up, and
                // compared once when found; all of it is charged before it starts.
                int members = 0;
                long nameChars = 0;
                for (int child = _b.Nodes[y].First; child >= 0; child = _b.Nodes[child].Next)
                {
                    members++;
                    nameChars += _b.Names[_b.Nodes[child].Name].Length;
                }

                for (int child = _a.Nodes[x].First; child >= 0; child = _a.Nodes[child].Next)
                {
                    nameChars += _a.Names[_a.Nodes[child].Name].Length;
                }

                _budget.SpendOrStop(3 * nameChars);
                long entries = (long)members * Document.EntryBytes;
                _budget.ReserveOrStop(entries);
                try
                {
                    var others = new Dictionary<string, (int Node, int Order)>(members, StringComparer.Ordinal);
                    int order = 0;
                    for (int child = _b.Nodes[y].First; child >= 0; child = _b.Nodes[child].Next)
                    {
                        others[_b.Names[_b.Nodes[child].Name]] = (child, order++);
                    }

                    int lastOrder = -1;
                    for (int child = _a.Nodes[x].First; child >= 0; child = _a.Nodes[child].Next)
                    {
                        int name = _a.Nodes[child].Name;
                        _path.Add((_a.Names[name], 0));
                        if (others.Remove(_a.Names[name], out (int Node, int Order) match))
                        {
                            _orderDiffers |= match.Order < lastOrder;
                            lastOrder = match.Order;
                            (int start, int length) = _a.NameText[name];
                            (int otherStart, int otherLength) = _b.NameText[_b.Nodes[match.Node].Name];
                            _escapesDiffer |= !BytesEqual(_a, start, length, _b, otherStart, otherLength);
                            Values(child, match.Node);
                        }
                        else
                        {
                            Change(VerifyJsonChangeKind.Removed, child, -1);
                        }

                        _path.RemoveAt(_path.Count - 1);
                    }

                    for (int child = _b.Nodes[y].First; child >= 0; child = _b.Nodes[child].Next)
                    {
                        string name = _b.Names[_b.Nodes[child].Name];
                        if (others.ContainsKey(name))
                        {
                            _path.Add((name, 0));
                            Change(VerifyJsonChangeKind.Added, -1, child);
                            _path.RemoveAt(_path.Count - 1);
                        }
                    }
                }
                finally
                {
                    _budget.Release(entries);
                }
            }

            private void Arrays(int x, int y)
            {
                int index = 0;
                int child = _a.Nodes[x].First;
                int other = _b.Nodes[y].First;
                while (child >= 0 || other >= 0)
                {
                    _path.Add((null, index));
                    if (child >= 0 && other >= 0)
                    {
                        Values(child, other);
                    }
                    else if (child >= 0)
                    {
                        Change(VerifyJsonChangeKind.Removed, child, -1);
                    }
                    else
                    {
                        Change(VerifyJsonChangeKind.Added, -1, other);
                    }

                    _path.RemoveAt(_path.Count - 1);
                    child = child >= 0 ? _a.Nodes[child].Next : -1;
                    other = other >= 0 ? _b.Nodes[other].Next : -1;
                    index++;
                }
            }

            // Strings as written are equal, or else their unescaped texts are (escapes differ). A string written without
            // escapes is decoded to compare it, within reserved scratch that is returned once it is compared.
            private bool StringsEqual(Node p, Node q)
            {
                if (BytesEqual(_a, p.Start + 1, p.End - p.Start - 2, _b, q.Start + 1, q.End - q.Start - 2))
                {
                    return true;
                }

                if (p.Unescaped < 0 && q.Unescaped < 0)
                {
                    return false;
                }

                long decoded = (p.Unescaped < 0 ? 2L * (p.End - p.Start - 2) : 0) + (q.Unescaped < 0 ? 2L * (q.End - q.Start - 2) : 0);
                _budget.ReserveOrStop(decoded);
                try
                {
                    string first = p.Unescaped >= 0 ? _a.Strings[p.Unescaped] : Written(_a, p);
                    string second = q.Unescaped >= 0 ? _b.Strings[q.Unescaped] : Written(_b, q);
                    _budget.SpendOrStop((long)first.Length + second.Length);
                    if (!string.Equals(first, second, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
                finally
                {
                    _budget.Release(decoded);
                }

                _escapesDiffer = true;
                return true;
            }

            private string Written(Document document, Node node)
            {
                int length = node.End - node.Start - 2;
                _budget.SpendOrStop(length);
                return Encoding.UTF8.GetString(document.Json.Span.Slice(node.Start + 1, length));
            }

            private bool BytesEqual(Document a, int start, int length, Document b, int otherStart, int otherLength)
            {
                if (length != otherLength)
                {
                    return false;
                }

                _budget.SpendOrStop(length);
                return a.Json.Span.Slice(start, length).SequenceEqual(b.Json.Span.Slice(otherStart, otherLength));
            }

            // Whether the bytes between the tokens differ, or their number does.
            private bool GapsDiffer()
            {
                if (_a.Gaps.Count != _b.Gaps.Count)
                {
                    return true;
                }

                for (int index = 0; index < _a.Gaps.Count; index++)
                {
                    (int start, int length) = _a.Gaps[index];
                    (int otherStart, int otherLength) = _b.Gaps[index];
                    if (!BytesEqual(_a, start, length, _b, otherStart, otherLength))
                    {
                        return true;
                    }
                }

                return false;
            }

            // A change at the current path. MaxChanges, or the characters, end the comparison.
            private void Change(VerifyJsonChangeKind kind, int x, int y)
            {
                _differs = true;
                _budget.StepOrStop();
                if (_changes.Count == _budget.Limits.MaxChanges)
                {
                    _budget.Limit(VerifyDiffLimitation.ChangeLimit);
                    throw new VerifyDiffLimitException();
                }

                string path = _budget.TakeOrStop(Path(), out bool pathCut);
                bool originalCut = false;
                bool actualCut = false;
                string original = x >= 0 ? Text(_a, x, out originalCut) : null;
                string actual = y >= 0 ? Text(_b, y, out actualCut) : null;
                _changes.Add(new VerifyJsonChange(path, pathCut, kind, original, originalCut, actual, actualCut));
            }

            // A value's JSON text as written, decoded only as far as a value can show.
            private string Text(Document document, int index, out bool cut)
            {
                Node node = document.Nodes[index];
                int length = node.End - node.Start;
                int shown = (int)Math.Min(length, ((long)_budget.Limits.MaxValueChars + 1) * 4);
                _budget.SpendOrStop(shown);
                string text = _budget.TakeOrStop(
                    Encoding.UTF8.GetString(document.Json.Span.Slice(node.Start, shown)), out cut);
                cut |= shown < length;
                return text;
            }

            // "$", then .name, ["name"] or [index] for each level, built only as far as a value can show.
            private string Path()
            {
                int most = _budget.Limits.MaxValueChars + 1;
                var text = new StringBuilder("$");
                foreach ((string name, int index) in _path)
                {
                    if (text.Length > most)
                    {
                        break;
                    }

                    if (name == null)
                    {
                        text.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
                    }
                    else if (IsIdentifier(name))
                    {
                        text.Append('.').Append(name, 0, Math.Min(name.Length, most));
                    }
                    else
                    {
                        text.Append("[\"");
                        foreach (char character in name)
                        {
                            if (text.Length > most)
                            {
                                break;
                            }

                            switch (character)
                            {
                                case '"':
                                    text.Append("\\\"");
                                    break;

                                case '\\':
                                    text.Append("\\\\");
                                    break;

                                default:
                                    if (character < ' ')
                                    {
                                        text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                                    }
                                    else
                                    {
                                        text.Append(character);
                                    }

                                    break;
                            }
                        }

                        text.Append("\"]");
                    }
                }

                return text.ToString();
            }

            private static bool IsIdentifier(string name)
            {
                if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] is '_' or '$'))
                {
                    return false;
                }

                foreach (char character in name)
                {
                    if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '$'))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
