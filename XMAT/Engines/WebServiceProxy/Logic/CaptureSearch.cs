// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    // What the capture view's search box finds: words that a request's host or path contains, and terms named as the
    // command line names its selections, such as status:5xx, host:*.xboxlive.com, method:POST and process:SampleGame.
    // A word or term that starts with - hides what it matches. Words, and terms of different names, must all match;
    // terms of one name match when any of them does, as does a list such as status:404,429. Quotes keep a phrase together,
    // as in "sessions/active" or host:"my host". Text before a colon that names nothing, as in https://example.com, is
    // part of a word.
    //
    // A host or process without * matches every host or process that contains it; with *, it matches as the command
    // line's --host and --process do. A status is a code such as 404, a class such as 5xx or 40x, or none.
    internal sealed class CaptureSearch
    {
        private readonly List<string> _words = new();
        private readonly List<string> _hiddenWords = new();
        private readonly Values<StatusPattern> _statuses = new();
        private readonly Values<HostPattern> _hosts = new();
        private readonly Values<string> _methods = new();
        private readonly List<string> _processes = new();
        private readonly List<string> _hiddenProcesses = new();
        private readonly List<string> _problems = new();
        private ProcessFilter _processFilter;

        private CaptureSearch(string text) => Text = text ?? string.Empty;

        internal static CaptureSearch Empty { get; } = Parse(string.Empty);

        internal string Text { get; }

        // Whether the search finds anything other than every request: it has a word or a term it can use.
        internal bool IsActive =>
            _words.Count + _hiddenWords.Count + _statuses.Count + _hosts.Count + _methods.Count + _processes.Count +
            _hiddenProcesses.Count > 0;

        // Why each term that can't be used is ignored, in the order typed, such as "status:abc isn't a status...".
        internal IReadOnlyList<string> Problems => _problems;

        internal static CaptureSearch Parse(string text)
        {
            var search = new CaptureSearch(text);
            foreach ((string token, bool quoted) in Tokens(search.Text))
            {
                search.Add(token, quoted);
            }

            if (search._processes.Count + search._hiddenProcesses.Count > 0)
            {
                search._processFilter = ProcessFilter.Compile(
                    new ProcessFilterSpec(search._processes.ToImmutableArray(), search._hiddenProcesses.ToImmutableArray()));
            }

            return search;
        }

        internal bool Matches(ProxyConnectionModel row) =>
            row != null && Matches(row.Host, row.Path, row.Status, row.Method, row.ClientProcess);

        internal bool Matches(string host, string path, string status, string method, ClientProcessInfo process)
        {
            host ??= string.Empty;
            path ??= string.Empty;
            status ??= string.Empty;
            method ??= string.Empty;
            bool Contains(string word) =>
                host.Contains(word, StringComparison.OrdinalIgnoreCase) || path.Contains(word, StringComparison.OrdinalIgnoreCase);

            HostMatchCandidates candidates = default;
            bool candidatesFound = false;
            bool HostMatches(HostPattern pattern)
            {
                if (pattern.Glob == null)
                {
                    return host.Contains(pattern.Text, StringComparison.OrdinalIgnoreCase);
                }

                if (!candidatesFound)
                {
                    candidates = HostListEntry.MatchCandidates(host, path);
                    candidatesFound = true;
                }

                return HostListEntry.Matches(pattern.Glob, candidates);
            }

            return _words.All(Contains) && !_hiddenWords.Any(Contains) &&
                _statuses.Matches(pattern => pattern.Matches(status)) &&
                _hosts.Matches(HostMatches) &&
                _methods.Matches(value => string.Equals(value, method, StringComparison.OrdinalIgnoreCase)) &&
                (_processFilter == null || _processFilter.Matches(process));
        }

        // Words, phrases in quotes, and terms, split at spaces outside quotes. QUOTED tells that a token began with a quote.
        private static IEnumerable<(string Token, bool Quoted)> Tokens(string text)
        {
            var token = new StringBuilder();
            bool inQuotes = false;
            bool started = false;
            bool quoted = false;
            foreach (char character in text)
            {
                if (character == '"')
                {
                    quoted |= !started;
                    started = true;
                    inQuotes = !inQuotes;
                }
                else if (char.IsWhiteSpace(character) && !inQuotes)
                {
                    if (started)
                    {
                        yield return (token.ToString(), quoted);
                        token.Clear();
                        started = quoted = false;
                    }
                }
                else
                {
                    token.Append(character);
                    started = true;
                }
            }

            if (started)
            {
                yield return (token.ToString(), quoted);
            }
        }

        private void Add(string token, bool quoted)
        {
            if (token.Length == 0)
            {
                return;
            }

            bool hide = !quoted && token.Length > 1 && token[0] == '-';
            string body = hide ? token[1..] : token;
            int colon = quoted ? -1 : body.IndexOf(':');
            string name = colon > 0 ? body[..colon].ToLowerInvariant() : null;
            if (name is not ("status" or "host" or "method" or "process"))
            {
                (hide ? _hiddenWords : _words).Add(body);
                return;
            }

            // A term still being typed, such as "status:", finds nothing yet and isn't a problem.
            foreach (string value in body[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (name)
                {
                    case "status" when StatusPattern.TryParse(value, out StatusPattern status):
                        _statuses.Add(status, hide);
                        break;
                    case "host":
                        _hosts.Add(HostPattern.Parse(value), hide);
                        break;
                    case "method":
                        _methods.Add(value, hide);
                        break;
                    case "process" when TryProcessSelector(value, out string selector):
                        (hide ? _hiddenProcesses : _processes).Add(selector);
                        break;
                    default:
                        _problems.Add(Localization.GetLocalizedString("CAPTURE_SEARCH_PROBLEM_" + name.ToUpperInvariant(), value));
                        break;
                }
            }
        }

        // A process selector as the Process filter reads it: a process ID, a name with *, or a name it contains.
        private static bool TryProcessSelector(string value, out string selector)
        {
            selector = value.All(char.IsAsciiDigit) || value.Contains('*') || value.Contains('?') ? value : "*" + value + "*";
            return ProcessFilter.Parse(new[] { selector }, null).Succeeded;
        }

        // The terms of one name: shown when any shown term matches, or there are none, and none of the hidden ones match.
        private sealed class Values<T>
        {
            private readonly List<T> _shown = new();
            private readonly List<T> _hidden = new();

            internal int Count => _shown.Count + _hidden.Count;

            internal void Add(T value, bool hide) => (hide ? _hidden : _shown).Add(value);

            internal bool Matches(Func<T, bool> matches) =>
                (_shown.Count == 0 || _shown.Any(matches)) && !_hidden.Any(matches);
        }

        // A status code with x for any digit, such as 404, 5xx or 40x; or none, for a request without a status.
        private readonly record struct StatusPattern(string Pattern)
        {
            internal static bool TryParse(string value, out StatusPattern pattern)
            {
                pattern = new StatusPattern(value.ToLowerInvariant());
                return pattern.Pattern == "none" ||
                    (pattern.Pattern.Length == 3 && pattern.Pattern.All(character => char.IsAsciiDigit(character) || character == 'x'));
            }

            internal bool Matches(string status)
            {
                if (Pattern == "none")
                {
                    return status.Length == 0;
                }

                if (status.Length != Pattern.Length)
                {
                    return false;
                }

                for (int index = 0; index < Pattern.Length; index++)
                {
                    if (Pattern[index] != 'x' && Pattern[index] != status[index])
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        // A host to look for in a request's host, or with * or /, a pattern matched as the Block List and --host match it.
        // The Block List's patterns are opaque text, so every host term can be used.
        private readonly record struct HostPattern(string Text, Regex Glob)
        {
            private static readonly char[] PatternCharacters = { '*', '/' };

            internal static HostPattern Parse(string value) =>
                value.IndexOfAny(PatternCharacters) < 0 ? new HostPattern(value, null) : new HostPattern(value, HostListEntry.CreatePattern(value));
        }
    }
}
