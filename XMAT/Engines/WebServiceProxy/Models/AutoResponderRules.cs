// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture.Models
{
    public enum AutoResponderScenario { Throttled, Unauthorized, Unavailable, Delayed }

    public sealed class AutoResponderRule
    {
        private readonly Regex _pattern;
        private readonly byte[] _body;
        private readonly List<KeyValuePair<string, string>> _headers = new();

        public string Pattern { get; }
        public string Method { get; }
        public int Status { get; }
        public string HeadersText { get; }
        public string BodyText { get; }
        public bool BodyIsBase64 { get; }
        public int DelayMilliseconds { get; }

        public AutoResponderRule(string pattern, string method, int status, string headers, string body, int delayMilliseconds, bool bodyIsBase64 = false)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 2048 ||
                (!pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ||
                pattern.Contains('\r') || pattern.Contains('\n'))
                throw new ArgumentException("Enter an HTTP or HTTPS URL pattern (use * as a wildcard).", nameof(pattern));
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Enter an HTTP method or *.", nameof(method));
            if (method != "*")
            {
                try { _ = new HttpMethod(method); }
                catch (FormatException ex) { throw new ArgumentException("Invalid HTTP method.", nameof(method), ex); }
            }
            if (status < 200 || status > 599)
                throw new ArgumentException("Status must be between 200 and 599.", nameof(status));
            if (delayMilliseconds < 0 || delayMilliseconds > 300000)
                throw new ArgumentException("Delay must be between 0 and 300000 milliseconds.", nameof(delayMilliseconds));

            try
            {
                _body = bodyIsBase64 ? Convert.FromBase64String(body ?? string.Empty) : new UTF8Encoding(false, true).GetBytes(body ?? string.Empty);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("The response body is not valid Base64.", nameof(body), ex);
            }
            catch (EncoderFallbackException ex)
            {
                throw new ArgumentException("The response body contains invalid text.", nameof(body), ex);
            }
            if (_body.Length > 1024 * 1024)
                throw new ArgumentException("The response body must not exceed 1 MiB.", nameof(body));
            if ((status == 204 || status == 205 || status == 304) && _body.Length != 0)
                throw new ArgumentException("This status cannot have a response body.", nameof(body));

            using var validator = new HttpResponseMessage();
            validator.Content = new ByteArrayContent(Array.Empty<byte>());
            foreach (string line in (headers ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0 || line.Contains('\r'))
                    throw new ArgumentException("Each header must be a name and value on one line.", nameof(headers));
                string name = line[..colon];
                string value = line[(colon + 1)..].Trim();
                foreach (char character in value)
                    if ((character < 32 && character != '\t') || character > 126)
                        throw new ArgumentException("Header values must contain only ASCII text and tabs.", nameof(headers));
                if (ProxyHeaderUtilities.IsHopByHopHeader(name) ||
                    name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    (!validator.Headers.TryAddWithoutValidation(name, value) &&
                     !validator.Content.Headers.TryAddWithoutValidation(name, value)))
                    throw new ArgumentException($"Header '{name}' is invalid or managed by the proxy.", nameof(headers));
                _headers.Add(new(name, value));
            }
            Pattern = pattern.Trim();
            Method = method;
            Status = status;
            HeadersText = headers ?? string.Empty;
            BodyText = body ?? string.Empty;
            BodyIsBase64 = bodyIsBase64;
            DelayMilliseconds = delayMilliseconds;
            int authorityEnd = Pattern.IndexOfAny(new[] { '/', '?', '#' }, Pattern.IndexOf("://", StringComparison.Ordinal) + 3);
            string authority = authorityEnd < 0 ? Pattern : Pattern[..authorityEnd];
            string path = authorityEnd < 0 ? string.Empty : Pattern[authorityEnd..];
            if (path.StartsWith('?') || path.StartsWith('#')) path = "/" + path;
            if (!authority.Contains('*'))
            {
                if (!Uri.TryCreate(authority, UriKind.Absolute, out Uri origin) ||
                    string.IsNullOrEmpty(origin.Host) || !string.IsNullOrEmpty(origin.UserInfo) ||
                    !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
                    throw new ArgumentException("The URL pattern has an invalid authority.", nameof(pattern));
                authority = origin.GetLeftPart(UriPartial.Authority);
                if (path.Length == 0) path = "/";
            }
            _pattern = new Regex("^(?i:" + Regex.Escape(authority).Replace("\\*", ".*") + ")" +
                Regex.Escape(path).Replace("\\*", ".*") + "$",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }

        internal bool Matches(string method, Uri url) =>
            (Method == "*" || string.Equals(Method, method, StringComparison.OrdinalIgnoreCase)) &&
            _pattern.IsMatch(url.AbsoluteUri);

        internal ServerResponse CreateResponse(ClientRequest request)
        {
            var response = new ServerResponse
            {
                RequestNumber = request.RequestNumber,
                StreamId = request.StreamId,
                Version = request.Version,
                Status = Status.ToString(CultureInfo.InvariantCulture),
                StatusDescription = ReasonPhrases.GetReasonPhrase(Status),
                BodyBytes = (byte[])_body.Clone(),
            };
            foreach (var header in _headers)
            {
                var target = ProxyHeaderUtilities.IsContentHeader(header.Key) ? response.ContentHeaders : response.Headers;
                target.Add(header.Key, header.Value);
            }
            if (Status != 204 && Status != 304)
                response.ContentHeaders["Content-Length"] = _body.Length.ToString(CultureInfo.InvariantCulture);
            return response;
        }

        public static AutoResponderRule FromScenario(AutoResponderScenario scenario, string pattern) => scenario switch
        {
            AutoResponderScenario.Throttled => new(pattern, "*", 429, "Retry-After: 30\r\nContent-Type: application/json", "{\"error\":\"throttled\"}", 0),
            AutoResponderScenario.Unauthorized => new(pattern, "*", 401, "Content-Type: application/json", "{\"error\":\"unauthorized\"}", 0),
            AutoResponderScenario.Unavailable => new(pattern, "*", 503, "Retry-After: 30\r\nContent-Type: application/json", "{\"error\":\"service unavailable\"}", 0),
            AutoResponderScenario.Delayed => new(pattern, "*", 200, "Content-Type: application/json", "{}", 5000),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        public override string ToString() => $"{Method} {Pattern} -> {Status} ({DelayMilliseconds} ms)";
    }

    public sealed class AutoResponderRules
    {
        private sealed record RuleData(string Pattern, string Method, int Status, string Headers, string Body, int DelayMilliseconds, bool BodyIsBase64);
        private sealed record RuleFile(int Version, RuleData[] Rules);
        private static readonly JsonSerializerOptions FileOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        private readonly object _gate = new();
        private readonly ObservableCollection<AutoResponderRule> _items = new();
        private bool _enabled = true;
        public ReadOnlyObservableCollection<AutoResponderRule> Items { get; }
        public bool Enabled
        {
            get { lock (_gate) return _enabled; }
            set { lock (_gate) _enabled = value; }
        }

        public AutoResponderRules() => Items = new(_items);

        public void Add(AutoResponderRule rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            lock (_gate)
            {
                if (_items.Count >= 128) throw new ArgumentException("At most 128 rules are supported.", nameof(rule));
                _items.Add(rule);
            }
        }

        public void Remove(AutoResponderRule rule)
        {
            lock (_gate) _items.Remove(rule);
        }

        public void Replace(AutoResponderRule oldRule, AutoResponderRule newRule)
        {
            ArgumentNullException.ThrowIfNull(newRule);
            lock (_gate)
            {
                int index = _items.IndexOf(oldRule);
                if (index < 0) throw new ArgumentException("The selected rule no longer exists.", nameof(oldRule));
                _items[index] = newRule;
            }
        }

        public string Export()
        {
            RuleData[] rules;
            lock (_gate)
            {
                rules = new RuleData[_items.Count];
                for (int i = 0; i < rules.Length; i++)
                {
                    AutoResponderRule rule = _items[i];
                    rules[i] = new(rule.Pattern, rule.Method, rule.Status, rule.HeadersText, rule.BodyText, rule.DelayMilliseconds, rule.BodyIsBase64);
                }
            }
            string json = JsonSerializer.Serialize(new RuleFile(1, rules), FileOptions);
            if (Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024)
                throw new ArgumentException("The rule file exceeds 4 MiB.");
            return json;
        }

        public void Import(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024)
                throw new ArgumentException("The rule file is missing or larger than 4 MiB.", nameof(json));
            RuleFile file;
            try { file = JsonSerializer.Deserialize<RuleFile>(json, FileOptions); }
            catch (JsonException ex) { throw new ArgumentException("The rule file is not valid JSON.", nameof(json), ex); }
            if (file?.Version != 1 || file.Rules == null || file.Rules.Length > 128)
                throw new ArgumentException("The rule file has an unsupported version or too many rules.", nameof(json));
            var rules = new List<AutoResponderRule>(file.Rules.Length);
            foreach (RuleData entry in file.Rules)
            {
                if (entry == null) throw new ArgumentException("The rule file contains an empty rule.", nameof(json));
                rules.Add(new AutoResponderRule(entry.Pattern, entry.Method, entry.Status, entry.Headers, entry.Body, entry.DelayMilliseconds, entry.BodyIsBase64));
            }
            lock (_gate)
            {
                _items.Clear();
                foreach (AutoResponderRule rule in rules) _items.Add(rule);
            }
        }

        public AutoResponderRule Match(string method, Uri url)
        {
            ArgumentNullException.ThrowIfNull(url);
            AutoResponderRule[] snapshot;
            lock (_gate)
            {
                if (!_enabled) return null;
                snapshot = new AutoResponderRule[_items.Count];
                _items.CopyTo(snapshot, 0);
            }
            foreach (AutoResponderRule rule in snapshot)
                if (rule.Matches(method, url)) return rule;
            return null;
        }
    }
}
