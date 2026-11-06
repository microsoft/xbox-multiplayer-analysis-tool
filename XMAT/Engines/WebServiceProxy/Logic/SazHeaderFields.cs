// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;

namespace XMAT.WebServiceCapture
{
    // The header fields of a SAZ message as the readers collect them: what WebHeaderCollection made of them, and also a
    // name with an apostrophe, which HTTP allows and replay sends but WebHeaderCollection refuses. Repeated names, in any
    // case, are one field under the first one's casing and position, with their values joined by a comma and no space,
    // empty values included; a value is trimmed of tabs, line breaks and spaces. ToString gives the fields as
    // WebHeaderCollection.ToString does: "Name: value" and CRLF for each, then CRLF. Lines are checked first
    // (FiddlerSazHandler.IsValidHeaderLine).
    internal sealed class SazHeaderFields
    {
        private static readonly char[] TrimCharacters = { '\t', '\n', '\v', '\f', '\r', ' ' };

        private readonly List<(string Name, List<string> Values)> _fields = new();
        private readonly Dictionary<string, int> _positions = new(StringComparer.OrdinalIgnoreCase);

        internal int Count => _fields.Count;

        // The values of a name, in any case, joined by commas; null when there is none.
        internal string this[string name] =>
            _positions.TryGetValue(name, out int position) ? string.Join(",", _fields[position].Values) : null;

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
            if (_positions.TryGetValue(name, out int position))
            {
                _fields[position].Values.Add(value);
                return;
            }

            _positions.Add(name, _fields.Count);
            _fields.Add((name, new List<string> { value }));
        }

        public override string ToString()
        {
            if (_fields.Count == 0)
            {
                return "\r\n";
            }

            var text = new StringBuilder();
            foreach ((string name, List<string> values) in _fields)
            {
                text.Append(name).Append(": ").AppendJoin(',', values).Append("\r\n");
            }

            return text.Append("\r\n").ToString();
        }
    }
}
