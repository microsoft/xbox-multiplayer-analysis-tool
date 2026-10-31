// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CaptureAnalysisEngine
{
    internal sealed class InputRegex
    {
        private static readonly TimeSpan CustomPatternTimeout =
            TimeSpan.FromMilliseconds(100);

        private readonly string _pattern;
        private readonly RegexOptions _options;
        private readonly string _requiredLiteral;
        private readonly Regex _validator;
        private readonly Lazy<Matcher> _exactMatcher;
        private readonly bool _cacheLastResult;
        private readonly object _cacheLock = new();
        private string _lastInput;
        private bool _lastResult;
        private bool _hasLastInput;
        private int _exactEvaluationCount;

        internal InputRegex(
            string pattern,
            RegexOptions options = RegexOptions.None,
            bool cacheLastResult = true)
        {
            _pattern = pattern;
            _options = options;
            _cacheLastResult = cacheLastResult;
            _requiredLiteral =
                CanUseOrdinalPrefilter(options)
                    ? FindRequiredLiteral(pattern)
                    : null;
            _validator =
                new Regex(
                    pattern,
                    options);
            _exactMatcher =
                new Lazy<Matcher>(
                    CreateExactMatcher,
                    LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal bool MatcherCreated =>
            _exactMatcher.IsValueCreated;

        internal int ExactEvaluationCount =>
            Volatile.Read(
                ref _exactEvaluationCount);

        internal bool IsMatch(string input)
        {
            return IsMatch(
                input,
                null,
                CancellationToken.None);
        }

        internal bool IsMatch(
            string input,
            AnalysisWorkBudget budget,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget?.ThrowIfLimitExceeded();
            if (input == null)
            {
                return _validator.IsMatch(input);
            }
            if (_requiredLiteral != null &&
                !input.Contains(
                    _requiredLiteral,
                    StringComparison.Ordinal))
            {
                return false;
            }
            if (!_cacheLastResult)
            {
                Interlocked.Increment(
                    ref _exactEvaluationCount);
                bool result = MatchExact(
                    input,
                    budget,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }

            lock (_cacheLock)
            {
                if (_hasLastInput &&
                    string.Equals(
                        _lastInput,
                        input,
                        StringComparison.Ordinal))
                {
                    return _lastResult;
                }

                _exactEvaluationCount++;
                bool result =
                    MatchExact(
                        input,
                        budget,
                        cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _lastInput = input;
                _lastResult = result;
                _hasLastInput = true;
                return result;
            }
        }

        private bool MatchExact(
            string input,
            AnalysisWorkBudget budget,
            CancellationToken cancellationToken)
        {
            Matcher matcher =
                _exactMatcher.Value;
            return matcher.IsFallback &&
                budget != null
                    ? budget.MatchFallback(
                        matcher.Regex,
                        input,
                        cancellationToken)
                    : matcher.Regex.IsMatch(input);
        }

        private Matcher CreateExactMatcher()
        {
            try
            {
                return new Matcher(
                    new Regex(
                        _pattern,
                        _options |
                            RegexOptions.NonBacktracking),
                    false);
            }
            catch (NotSupportedException)
            {
                // Preserve custom constructs unsupported by the non-backtracking engine,
                // but bound their evaluation for attacker-controlled long input.
                return new Matcher(
                    new Regex(
                        _pattern,
                        _options,
                        CustomPatternTimeout),
                    true);
            }
        }

        private static bool CanUseOrdinalPrefilter(
            RegexOptions options) =>
            (options &
             (RegexOptions.IgnoreCase |
              RegexOptions.IgnorePatternWhitespace)) == 0;

        private static string FindRequiredLiteral(
            string pattern)
        {
            if (string.IsNullOrEmpty(pattern) ||
                pattern.Contains('|'))
            {
                return null;
            }

            var literal = new StringBuilder();
            var index = pattern[0] == '^'
                ? 1
                : 0;
            for (; index < pattern.Length; index++)
            {
                char character = pattern[index];
                if (character == '\\')
                {
                    if (index + 1 >= pattern.Length ||
                        !IsEscapedLiteral(
                            pattern[index + 1]))
                    {
                        break;
                    }
                    literal.Append(
                        pattern[++index]);
                    continue;
                }
                if (character is '*' or '?' or '{')
                {
                    if (literal.Length > 0)
                    {
                        literal.Length--;
                    }
                    break;
                }
                if (character is
                    '.' or '^' or '$' or '+' or
                    '(' or ')' or '[' or ']' or '}')
                {
                    break;
                }
                literal.Append(character);
            }
            return literal.Length == 0
                ? null
                : literal.ToString();
        }

        private static bool IsEscapedLiteral(
            char character) =>
            character is
                '\\' or '.' or '$' or '^' or
                '{' or '[' or '(' or '|' or
                ')' or '*' or '+' or '?' or
                ']' or '}';

        private sealed record Matcher(
            Regex Regex,
            bool IsFallback);
    }
}
