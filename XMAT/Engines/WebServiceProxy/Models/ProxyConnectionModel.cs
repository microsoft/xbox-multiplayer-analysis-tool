// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using XMAT.SharedInterfaces;

namespace XMAT.WebServiceCapture.Models
{
    public class ProxyConnectionModel : INotifyPropertyChanged
    {
        public readonly string UnknownStringValue = string.Empty;
        public const Int64 UnknownRowId = -1;

        public Int64 RecordRowId { get; internal set; }

        public Int64 RequestNumber { get; private set; }
        public Int64 Id { get; private set; }
        public DateTime RequestTime { get; private set; }
        public TimeSpan Duration { get; private set; }
        public string Status { get; private set; }
        public string Scheme { get; private set; }
        public string Host { get; private set; }
        public string Port { get; private set; }
        public string Method { get; private set; }
        public string Path { get; private set; }
        public string RequestLineAndHeaders { get; private set; }
        // The body arrays, and the header dictionaries below, are never changed in place once a row is published: an
        // update (UpdateFromDataRecord) replaces the arrays and drops the dictionaries. So a copy (the copy constructor),
        // such as a replay's snapshot of the rows the user confirmed, may share them and still keeps what it copied.
        public byte[] RequestBody { get; private set; }
        public string ResponseLineAndHeaders { get; private set; }
        public byte[] ResponseBody { get; private set; }
        public string ClientIP { get; private set; }

        // For rows of the Replays tab (ReplayRecorder): where the row comes from, and a note on the bodies that the tab did
        // not keep; null for other rows. Both are kept in memory only.
        internal ReplayProvenance Replay { get; set; }

        public bool IsReplay => Replay != null;

        public string ReplaySource => Replay?.Describe();

        public string ReplayNote { get; internal set; }

        public bool HasReplayNote => !string.IsNullOrEmpty(ReplayNote);

        // For rows of the Replays tab that Reissue and verify recorded from a send that it verified (ReplayRecorder): the
        // verdict, and the verification differences when the tab kept them; null for other rows. The differences hold
        // captured content: they are kept in memory only, never in the capture table, saved captures or exports, and a
        // copy (the copy constructor), which is made to be sent, does not take them.
        internal RecordedVerification Verification { get; set; }

        // View verification differences applies to the row (WebServiceProxyCommands.ViewVerifyDiff).
        public bool IsVerified => Verification != null;

        private Dictionary<string, string> _requestHeaders;
        private Dictionary<string, string> _responseHeaders;

        public Dictionary<string, string> RequestHeaders =>
            GetOrCreateHeaders(
                ref _requestHeaders,
                RequestLineAndHeaders);

        public Dictionary<string, string> ResponseHeaders =>
            GetOrCreateHeaders(
                ref _responseHeaders,
                ResponseLineAndHeaders);

        internal bool HeaderCollectionsMaterialized =>
            _requestHeaders != null ||
            _responseHeaders != null;

        // Whether RequestHeaders has a header with this name, answered as RequestHeaders.ContainsKey would. A dictionary
        // that is already made answers; otherwise the head's lines are read in place, so a row whose head is large, such as
        // one whose request line holds a long URL, is checked without making the dictionary or copying its head.
        internal bool HasRequestHeader(string name)
        {
            Dictionary<string, string> headers = Volatile.Read(ref _requestHeaders);
            if (headers != null)
            {
                return headers.ContainsKey(name);
            }

            var lines = new HeaderLines(RequestLineAndHeaders);
            while (lines.TryReadNext(out ReadOnlySpan<char> headerName, out _))
            {
                if (headerName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // The host offered by host-list menus (DNS spelling kept, IP literals canonical), or
        // null when the row has no valid host.
        public string ActionableHost => HostAuthority.TryNormalizeHost(Host, out string host) ? host : null;

        public event PropertyChangedEventHandler PropertyChanged;

        internal ProxyConnectionModel(Int64 rowId, int requestNumber, int id)
        {
            RecordRowId = rowId;
            RequestNumber = Convert.ToInt64(requestNumber);
            Id = Convert.ToInt64(id);
            Status = UnknownStringValue;
            RequestTime = DateTime.MinValue;
            Duration = TimeSpan.FromMilliseconds(0);
            Scheme = UnknownStringValue;
            Host = UnknownStringValue;
            Port = UnknownStringValue;
            Method = UnknownStringValue;
            Path = UnknownStringValue;
            RequestLineAndHeaders = UnknownStringValue;
            ResponseLineAndHeaders = UnknownStringValue;
            RequestBody = Array.Empty<byte>();
            ResponseBody = Array.Empty<byte>();
            ClientIP = UnknownStringValue;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        internal ProxyConnectionModel(
            Int64 id,
            string status,
            DateTime requestTime,
            TimeSpan duration,
            string scheme,
            string hostAndPort,
            string method,
            string call,
            string requestLineAndHeaders,
            byte[] requestBody,
            string responseLineAndHeaders,
            byte[] responseBody,
            string clientIP)
        {
            RecordRowId = UnknownRowId;
            RequestNumber = id;
            Id = id;
            Status = status;
            RequestTime = requestTime;
            Duration = duration;
            Scheme = scheme;
            if (HostAuthority.TryParse(hostAndPort, out string host, out int? port))
            {
                Host = host;
                Port = port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
            else
            {
                // Without a parsable authority, such as a SAZ frame with no Host header, keep the
                // former split; a missing value gives an empty host and port.
                var hostAndPortSplit = (hostAndPort ?? string.Empty).Split(':');
                Host = hostAndPortSplit[0];
                Port = hostAndPortSplit.Length > 1 ? hostAndPortSplit[1] : string.Empty;
            }
            Method = method;
            // TODO: possibly remove the host & port?
            Path = call;
            RequestLineAndHeaders = requestLineAndHeaders;
            ResponseLineAndHeaders = responseLineAndHeaders;
            RequestBody = requestBody;
            ResponseBody = responseBody;
            ClientIP = clientIP;

            // notify whoever is listening that all properties changed (null == all properties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        internal ProxyConnectionModel(ProxyConnectionModel model)
        {
            RecordRowId = model.RecordRowId;
            RequestNumber = model.RequestNumber;
            Id = model.Id;
            Status = model.Status;
            RequestTime = model.RequestTime;
            Duration = model.Duration;
            Scheme = model.Scheme;
            Host = model.Host;
            Port = model.Port;
            Method = model.Method;
            Path = model.Path;

            RequestLineAndHeaders = model.RequestLineAndHeaders;
            ResponseLineAndHeaders = model.ResponseLineAndHeaders;
            RequestBody = model.RequestBody;
            ResponseBody = model.ResponseBody;
            ClientIP = model.ClientIP;
            Replay = model.Replay;
            ReplayNote = model.ReplayNote;

            // Shared, not copied: they are immutable once published (see RequestBody).
            _requestHeaders = model.RequestHeaders;
            _responseHeaders = model.ResponseHeaders;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        private static Dictionary<string, string> GetOrCreateHeaders(
            ref Dictionary<string, string> field,
            string firstLineAndHeaders)
        {
            Dictionary<string, string> existing =
                Volatile.Read(ref field);
            if (existing != null)
            {
                return existing;
            }

            var parsed =
                StringToHeaders(
                    firstLineAndHeaders);
            return Interlocked.CompareExchange(
                    ref field,
                    parsed,
                    null) ??
                parsed;
        }

        private static Dictionary<string, string> StringToHeaders(string firstLineAndHeaders)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = new HeaderLines(firstLineAndHeaders);
            while (lines.TryReadNext(out ReadOnlySpan<char> name, out ReadOnlySpan<char> value))
            {
                headers[name.ToString()] = value.ToString();
            }

            return headers;
        }

        // The headers of a head as rows have always read them: the lines that "\r\n" separates, after the first, which is the
        // request or status line, each parsed by TryParseHeaderLine. A line that is not a header is skipped.
        private ref struct HeaderLines
        {
            private ReadOnlySpan<char> _rest;
            private bool _done;

            internal HeaderLines(string firstLineAndHeaders)
            {
                ReadOnlySpan<char> head = firstLineAndHeaders;
                int end = head.IndexOf("\r\n");
                _done = end < 0;
                _rest = _done ? default : head[(end + 2)..];
            }

            internal bool TryReadNext(out ReadOnlySpan<char> name, out ReadOnlySpan<char> value)
            {
                while (!_done)
                {
                    int end = _rest.IndexOf("\r\n");
                    ReadOnlySpan<char> line;
                    if (end < 0)
                    {
                        line = _rest;
                        _done = true;
                    }
                    else
                    {
                        line = _rest[..end];
                        _rest = _rest[(end + 2)..];
                    }

                    if (TryParseHeaderLine(line, out name, out value))
                    {
                        return true;
                    }
                }

                name = default;
                value = default;
                return false;
            }
        }

        // One header line as line.Split(':', 2, RemoveEmptyEntries | TrimEntries) has always read it: the name is the first
        // segment that a colon ends and that is not empty once trimmed; the value is the rest of the line after the colon
        // that ends the name and after any empty segments that follow it, trimmed. A line without such a name, or whose value
        // is empty, is not a header. So ":Upgrade: x" is Upgrade with "x", "Name::: v" is Name with "v", "Name: a: b" is Name
        // with "a: b", and "Name:" and ": v" are not headers.
        private static bool TryParseHeaderLine(ReadOnlySpan<char> line, out ReadOnlySpan<char> name, out ReadOnlySpan<char> value)
        {
            name = default;
            value = default;
            int current = 0;
            while (true)
            {
                int colon = line[current..].IndexOf(':');
                if (colon < 0)
                {
                    return false;
                }

                ReadOnlySpan<char> segment = line.Slice(current, colon).Trim();
                current += colon + 1;
                if (!segment.IsEmpty)
                {
                    name = segment;
                    break;
                }
            }

            while (true)
            {
                int colon = line[current..].IndexOf(':');
                if (colon < 0 || !line.Slice(current, colon).Trim().IsEmpty)
                {
                    break;
                }

                current += colon + 1;
            }

            value = line[current..].Trim();
            return !value.IsEmpty;
        }

        internal ProxyConnectionModel(IDataRecord dataRecord)
        {
            UpdateFromDataRecord(dataRecord);
        }

        internal void AddToDataTable(string deviceName, IDataTable dataTable)
        {
            RecordRowId = dataTable.AddRow(ToDataTableValues(deviceName));
        }

        internal object[] ToDataTableValues(string deviceName)
        {
            var requestBodyAsBase64 = RequestBody != null && RequestBody.Length > 0 ? Convert.ToBase64String(RequestBody) : string.Empty;
            var responseBodyAsBase64 = ResponseBody != null && ResponseBody.Length > 0 ? Convert.ToBase64String(ResponseBody) : string.Empty;

            // see layout in WebServiceCaptureMethod.InitializeDataTables()

            return new object[]
            {
                deviceName,
                RequestNumber,
                Id,
                ToDateTimeString(RequestTime),
                ToDateTimeString(ResponseDoneTime),
                Scheme,
                Host,
                Port,
                Method,
                Path,
                Status,
                RequestLineAndHeaders,
                requestBodyAsBase64,
                ResponseLineAndHeaders,
                responseBodyAsBase64,
                ClientIP
            };
        }

        internal void UpdateFromDataRecord(IDataRecord dataRecord)
        {
            RecordRowId = dataRecord.RowId;

            var requestNumber = dataRecord.Int(WebServiceCaptureMethod.FieldKey_RequestNumber);
            if (requestNumber != default)
            {
                RequestNumber = Convert.ToInt64(requestNumber);
            }

            var requestTimestamp = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestTimestamp);
            if (!string.IsNullOrEmpty(requestTimestamp))
            {
                RequestTime = DateTime.Parse(requestTimestamp);
            }

            var responseTimestamp = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ResponseTimestamp);
            if (!string.IsNullOrEmpty(responseTimestamp))
            {
                Duration = Elapsed(RequestTime, DateTime.Parse(responseTimestamp));
            }

            var requestScheme = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestScheme);
            if (!string.IsNullOrEmpty(requestScheme))
            {
                Scheme = requestScheme;
            }

            var requestHost = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestHost);
            if (!string.IsNullOrEmpty(requestHost))
            {
                Host = requestHost;
            }

            var requestPort = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestPort);
            if (!string.IsNullOrEmpty(requestPort))
            {
                Port = requestPort;
            }

            var requestMethod = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestMethod);
            if (!string.IsNullOrEmpty(requestMethod))
            {
                Method = requestMethod;
            }

            var requestPath = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestPath);
            if (!string.IsNullOrEmpty(requestPath))
            {
                Path = requestPath;
            }

            var requestStatus = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestStatus);
            if (!string.IsNullOrEmpty(requestStatus))
            {
                Status = requestStatus;
            }

            var requestLineAndHeaders = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders);
            if (!string.IsNullOrEmpty(requestLineAndHeaders))
            {
                RequestLineAndHeaders = requestLineAndHeaders;
            }

            var requestBody = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestBody);
            if (!string.IsNullOrEmpty(requestBody))
            {
                RequestBody = Convert.FromBase64String(requestBody);
            }

            var responseLineAndHeaders = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders);
            if (!string.IsNullOrEmpty(responseLineAndHeaders))
            {
                ResponseLineAndHeaders = responseLineAndHeaders;
            }

            var responseBody = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ResponseBody);
            if (!string.IsNullOrEmpty(responseBody))
            {
                ResponseBody = Convert.FromBase64String(responseBody);
            }

            var clientIP = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ClientIP);
            if (!string.IsNullOrEmpty(clientIP))
            {
                ClientIP = clientIP;
            }

            _requestHeaders = null;
            _responseHeaders = null;

            // notify whoever is listening that all properties changed (null == all properties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public static string ToDateTimeString(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("o");
        }

        // When the response ended, in RequestTime's kind (EndOf).
        internal DateTime ResponseDoneTime => EndOf(RequestTime, Duration);

        // The time elapsed after start, in start's kind. Duration is elapsed time, so it is added to the instant that a local
        // time names, never to its clock reading, which is an hour off across a daylight saving change. A UTC time is its
        // instant, and a time of no kind, such as an unknown DateTime.MinValue, names none and is added to as it is.
        internal static DateTime EndOf(DateTime start, TimeSpan elapsed) =>
            start.Kind == DateTimeKind.Local ? (start.ToUniversalTime() + elapsed).ToLocalTime() : start + elapsed;

        // The time from start to end, the inverse of EndOf: the time between their instants, unless start has no kind.
        internal static TimeSpan Elapsed(DateTime start, DateTime end) =>
            start.Kind == DateTimeKind.Unspecified ? end - start : end.ToUniversalTime() - start.ToUniversalTime();
    }
}
