// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.AgentAccess
{
    internal sealed class AgentDataPolicy(AgentRuntime runtime)
    {
        private readonly byte[] _pseudonymKey = RandomNumberGenerator.GetBytes(32);
        private static readonly SearchValues<char> AliasCharacters = SearchValues.Create("0123456789abcdef");
        private readonly object _aliasGate = new();
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private long _aliasRevision;

        internal bool Redacts => runtime.Policy?.Settings.DataSharing == AgentDataSharing.Redacted;

        internal AgentCaptureProjection CreateSnapshotProjection(long maximumBytes) =>
            Redacts ? new(this, maximumBytes) : null;

        internal string Pseudonym(string kind, string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            var revision = runtime.DataRevision;
            var input = Encoding.UTF8.GetBytes(revision.ToString(CultureInfo.InvariantCulture) + ":" + kind + ":" + value);
            var alias = kind + "-" + Convert.ToHexString(HMACSHA256.HashData(_pseudonymKey, input))[..12].ToLowerInvariant();
            if (value.Length <= 256)
            {
                lock (_aliasGate)
                {
                    if (_aliasRevision != revision)
                    {
                        _aliases.Clear();
                        _aliasRevision = revision;
                    }
                    if (_aliases.TryAdd(alias, value) && _aliases.Count > 4096)
                        _aliases.Remove(_aliases.Keys.First());
                }
            }
            return alias;
        }

        internal string Resolve(string kind, string value)
        {
            if (!Redacts || value == null || value.Length != kind.Length + 13 ||
                !value.StartsWith(kind + "-", StringComparison.Ordinal) ||
                value.AsSpan(kind.Length + 1).ContainsAnyExcept(AliasCharacters))
                return value;
            lock (_aliasGate)
                return _aliasRevision == runtime.DataRevision && _aliases.TryGetValue(value, out var original)
                    ? original : throw new AgentAccessException(AgentAccessError.SourceChanged);
        }

        internal CaptureDataFilter Resolve(CaptureDataFilter filter) => !Redacts ? filter : new(
            Resolve("device", filter.DeviceName), Resolve("address", filter.Host), filter.Method, filter.StatusCode,
            filter.AfterUtc, filter.BeforeUtc, filter.MinimumDurationMilliseconds, filter.Protocol,
            Resolve("address", filter.SourceAddress), Resolve("address", filter.DestinationAddress),
            filter.CaptureId, filter.CaptureEpoch, filter.ConnectionId, filter.FromHost, filter.MessageType);

        internal string Text(string value, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            if ((value.Contains(':') || value.AsSpan().Count('.') == 3) && IPAddress.TryParse(value, out _))
                return Pseudonym("address", value);
            return new SazSanitizer(cancellationToken).SanitizeTextValue(value);
        }
    }

    internal sealed class AgentCaptureProjection(AgentDataPolicy policy, long maximumBytes) : IQuerySnapshotProjection
    {
        internal long RedactedValues { get; private set; }
        internal long OmittedValues { get; private set; }
        public long MaximumBytes { get; } = maximumBytes;

        public object[] Project(QueryTable table, object[] values, CancellationToken cancellationToken)
        {
            var output = (object[])values.Clone();
            var http = table.Name == WebServiceCaptureMethod.TableName;
            if (http)
                ProjectHttp(table, values, output, cancellationToken);
            for (var index = 0; index < output.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var column = table.Columns[index];
                if (column.PrimaryKey || values[index] == null ||
                    http && column.Name is WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders or
                        WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders or
                        WebServiceCaptureMethod.FieldKey_RequestBody or WebServiceCaptureMethod.FieldKey_ResponseBody)
                    continue;
                var name = column.Name;
                var value = values[index];
                if (value is byte[] bytes && bytes.Length > 0)
                {
                    output[index] = Array.Empty<byte>();
                    OmittedValues++;
                }
                else if (value is string text)
                {
                    var sensitive = SazSanitizer.IsSensitiveField(name);
                    output[index] = name.Equals("DeviceName", StringComparison.OrdinalIgnoreCase)
                        ? policy.Pseudonym("device", text)
                        : name.EndsWith("IP", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Address", StringComparison.OrdinalIgnoreCase)
                            ? policy.Pseudonym("address", text)
                            : IsPayload(name) ? string.Empty
                            : sensitive ? SazSanitizer.Redacted : policy.Text(text, cancellationToken);
                    if (!Equals(value, output[index]))
                    {
                        if (IsPayload(name))
                            OmittedValues++;
                        else
                            RedactedValues++;
                    }
                }
                else if (SazSanitizer.IsSensitiveField(name))
                {
                    output[index] = value is double ? 0.0 : 0L;
                    RedactedValues++;
                }
            }
            return output;
        }

        private void ProjectHttp(QueryTable table, object[] original, object[] output, CancellationToken cancellationToken)
        {
            var requestHeaders = Index(table, WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders);
            var responseHeaders = Index(table, WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders);
            var requestBody = Index(table, WebServiceCaptureMethod.FieldKey_RequestBody);
            var responseBody = Index(table, WebServiceCaptureMethod.FieldKey_ResponseBody);
            var method = Index(table, WebServiceCaptureMethod.FieldKey_RequestMethod);
            if (requestHeaders < 0 || responseHeaders < 0 || requestBody < 0 || responseBody < 0)
                throw new QueryException(QueryError.ProjectionUnavailable, "A complete HTTP projection is unavailable.");
            var sanitizer = new SazSanitizer(cancellationToken);
            var sanitized = sanitizer.SanitizeSession(original[requestHeaders] as string, Decode(original[requestBody]),
                original[responseHeaders] as string, Decode(original[responseBody]), method < 0 ? null : original[method] as string);
            output[requestHeaders] = original[requestHeaders] == null ? null : sanitized.Request.Headers ?? string.Empty;
            output[responseHeaders] = original[responseHeaders] == null ? null : sanitized.Response?.Headers ?? string.Empty;
            output[requestBody] = original[requestBody] == null ? null : Convert.ToBase64String(sanitized.Request.Body ?? []);
            output[responseBody] = original[responseBody] == null ? null : Convert.ToBase64String(sanitized.Response?.Body ?? []);
            RedactedValues += sanitizer.Summary.RedactedValues;
            OmittedValues += sanitizer.Summary.OmittedBodies + sanitizer.Summary.OmittedHeaders;
        }

        private byte[] Decode(object value)
        {
            if (value is not string text || string.IsNullOrEmpty(text))
                return Array.Empty<byte>();
            if (text.Length > (SazSanitizer.MaximumBodyBytes + 2L) / 3 * 4)
            {
                OmittedValues++;
                return Array.Empty<byte>();
            }
            try
            { return Convert.FromBase64String(text); }
            catch (FormatException)
            {
                OmittedValues++;
                return Array.Empty<byte>();
            }
        }

        private static bool IsPayload(string name) =>
            name.Contains("payload", StringComparison.OrdinalIgnoreCase) || name.Contains("body", StringComparison.OrdinalIgnoreCase);

        private static int Index(QueryTable table, string name)
        {
            for (var index = 0; index < table.Columns.Count; index++)
                if (table.Columns[index].Name == name)
                    return index;
            return -1;
        }
    }
}
