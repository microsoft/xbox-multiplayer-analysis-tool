// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;

namespace XMAT.WebServiceCapture
{
    // The header fields of a SAZ message as the readers collect them: the lines that WebHeaderCollection takes, and also a
    // name with an apostrophe, which HTTP allows and replay sends but WebHeaderCollection refuses. Lines are checked first
    // (FiddlerSazHandler.IsValidHeaderLine), and a value is trimmed of tabs, line breaks and spaces. ToString gives every
    // field in the order it came, under its name as written: "Name: value" and CRLF for each, then CRLF. A repeated name
    // keeps each of its lines, since joining them changes what some mean: a comma separates the values of most fields, but
    // it is part of a Set-Cookie value (RFC 6265 3). The indexer and Count are a separate lookup view, as
    // WebHeaderCollection gives one: a name in any case, with its values joined by a comma and no space.
    internal sealed class SazHeaderFields
    {
        private static readonly char[] TrimCharacters = { '\t', '\n', '\v', '\f', '\r', ' ' };

        private readonly List<(string Name, string Value)> _fields = new();
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

        // The number of names, in any case.
        internal int Count => _values.Count;

        // The values of a name, in any case, joined by commas; null when there is none.
        internal string this[string name] =>
            _values.TryGetValue(name, out List<string> values) ? string.Join(",", values) : null;

        // A header line that IsValidHeaderLine accepted: the name before its first colon, the value after it, trimmed.
        internal void Add(string line)
        {
            ArgumentNullException.ThrowIfNull(line);
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                throw new ArgumentException("A header line has a colon.", nameof(line));
            }

            string name = line[..colon];
            string value = line[(colon + 1)..].Trim(TrimCharacters);
            _fields.Add((name, value));
            if (_values.TryGetValue(name, out List<string> values))
            {
                values.Add(value);
                return;
            }

            _values.Add(name, new List<string> { value });
        }

        public override string ToString()
        {
            if (_fields.Count == 0)
            {
                return "\r\n";
            }

            var text = new StringBuilder();
            foreach ((string name, string value) in _fields)
            {
                text.Append(name).Append(": ").Append(value).Append("\r\n");
            }

            return text.Append("\r\n").ToString();
        }
    }
}
