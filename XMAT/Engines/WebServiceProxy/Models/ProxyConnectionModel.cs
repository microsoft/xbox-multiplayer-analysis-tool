// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;

namespace XMAT.WebServiceCapture.Models
{
    public class ProxyConnectionModel : INotifyPropertyChanged
    {
        public string ResponseText
        {
            get => PublicUtilities.BodyAsText(ResponseHeaders, ResponseBody);
        }

        public string RequestText
        {
            get => PublicUtilities.BodyAsText(RequestHeaders, RequestBody);
        }


        public readonly string UnknownStringValue = string.Empty;
        public const Int64 UnknownRowId = -1;

        public Int64 RecordRowId { get; internal set; }
        public CaptureRowKey RowKey { get; internal set; }
        public ClientProcessInfo ClientProcess { get; private set; }
        public bool IsProcessResolving { get; private set; }

        public Int64 RequestNumber { get; private set; }
        public Int64 Id { get; private set; }
        public DateTime RequestTime { get; private set; }
        public TimeSpan Duration { get; private set; }
        internal bool HasRecordedResponseTimestamp { get; private set; }
        public string Status { get; private set; }
        public string Scheme { get; private set; }
        public string Host { get; private set; }
        public string Port { get; private set; }
        public string Method { get; private set; }
        public string Path { get; private set; }
        public string RequestLineAndHeaders { get; private set; }
        public byte[] RequestBody { get; private set; }
        public string ResponseLineAndHeaders { get; private set; }
        public byte[] ResponseBody { get; private set; }
        public string ClientIP { get; private set; }

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
            ClientProcess = ClientProcessInfo.NotRecorded;

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
            HasRecordedResponseTimestamp = !string.IsNullOrEmpty(responseLineAndHeaders);
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
            ClientProcess = ClientProcessInfo.NotRecorded;

            // notify whoever is listening that all properties changed (null == all properties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        internal ProxyConnectionModel(ProxyConnectionModel model)
        {
            RecordRowId = model.RecordRowId;
            RowKey = model.RowKey;
            RequestNumber = model.RequestNumber;
            Id = model.Id;
            Status = model.Status;
            RequestTime = model.RequestTime;
            Duration = model.Duration;
            HasRecordedResponseTimestamp = model.HasRecordedResponseTimestamp;
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
            ClientProcess = model.ClientProcess;
            IsProcessResolving = model.IsProcessResolving;

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

            if (!string.IsNullOrEmpty(firstLineAndHeaders))
            {
                string[] lines = firstLineAndHeaders.Split("\r\n");

                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string[] header = line.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (header.Length < 2)
                    {
                        // TODO: we have a bad header
                    }
                    else
                    {
                        string name = header[0].Trim();
                        string value = header[1].Trim();

                        headers[name] = value;
                    }
                }
            }
            return headers;
        }

        internal ProxyConnectionModel(IDataRecord dataRecord)
        {
            ClientProcess = ClientProcessInfo.NotRecorded;
            UpdateFromDataRecord(dataRecord);
        }

        internal void SetImportedIdentity(long requestNumber, long connectionId)
        {
            RequestNumber = requestNumber;
            Id = connectionId;
        }

        internal void SetClientProcess(ClientProcessInfo info, bool resolving = false)
        {
            ClientProcessInfo value = info ?? ClientProcessInfo.NotRecorded;
            bool processChanged = ClientProcess != value;
            bool resolvingChanged = IsProcessResolving != resolving;
            ClientProcess = value;
            IsProcessResolving = resolving;
            if (processChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClientProcess)));
            }
            if (resolvingChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProcessResolving)));
            }
        }

        internal void AddToDataTable(string deviceName, IDataTable dataTable)
        {
            RecordRowId = dataTable.AddRow(ToDataTableValues(deviceName, dataTable));
        }

        internal object[] ToDataTableValues(string deviceName)
        {
            var requestBodyAsBase64 = RequestBody != null && RequestBody.Length > 0 ? Convert.ToBase64String(RequestBody) : string.Empty;
            var responseBodyAsBase64 = ResponseBody != null && ResponseBody.Length > 0 ? Convert.ToBase64String(ResponseBody) : string.Empty;

            return WebProxyCaptureSchema.Project(
                new Dictionary<string, object>(
                    StringComparer.Ordinal)
                {
                    [WebServiceCaptureMethod
                        .FieldKey_DeviceName] =
                        deviceName,
                    [WebServiceCaptureMethod
                        .FieldKey_RequestNumber] =
                        RequestNumber,
                    [WebServiceCaptureMethod
                        .FieldKey_ConnectionId] =
                        Id,
                    [WebServiceCaptureMethod
                        .FieldKey_RequestTimestamp] =
                        ToDateTimeString(RequestTime),
                    [WebServiceCaptureMethod
                        .FieldKey_ResponseTimestamp] =
                        ToDateTimeString(
                            RequestTime + Duration),
                    [WebServiceCaptureMethod
                        .FieldKey_RequestScheme] =
                        Scheme,
                    [WebServiceCaptureMethod
                        .FieldKey_RequestHost] =
                        Host,
                    [WebServiceCaptureMethod
                        .FieldKey_RequestPort] =
                        Port,
                    [WebServiceCaptureMethod
                        .FieldKey_RequestMethod] =
                        Method,
                    [WebServiceCaptureMethod
                        .FieldKey_RequestPath] =
                        Path,
                    [WebServiceCaptureMethod
                        .FieldKey_RequestStatus] =
                        Status,
                    [WebServiceCaptureMethod
                        .FieldKey_RequestLineAndHeaders] =
                        RequestLineAndHeaders,
                    [WebServiceCaptureMethod
                        .FieldKey_RequestBody] =
                        requestBodyAsBase64,
                    [WebServiceCaptureMethod
                        .FieldKey_ResponseLineAndHeaders] =
                        ResponseLineAndHeaders,
                    [WebServiceCaptureMethod
                        .FieldKey_ResponseBody] =
                        responseBodyAsBase64,
                    [WebServiceCaptureMethod
                        .FieldKey_ClientIP] =
                        ClientIP,
                },
                ClientProcess);
        }

        internal object[] ToDataTableValues(string deviceName, IDataTable dataTable)
        {
            ArgumentNullException.ThrowIfNull(dataTable);
            IDataset dataset = dataTable.Dataset;
            using (dataset as IDisposable)
            {
                return ToDataTableValues(deviceName, dataset.FieldDefs);
            }
        }

        internal object[] ToDataTableValues(string deviceName, IEnumerable<IFieldDefinition> fieldDefinitions)
        {
            ArgumentNullException.ThrowIfNull(fieldDefinitions);
            object[] projected = ToDataTableValues(deviceName);
            var byName =
                WebProxyCaptureSchema.Fields
                    .Select(
                        (field, index) =>
                            new
                            {
                                field.Name,
                                Value = projected[index],
                            }).ToDictionary(item => item.Name,
                        item => item.Value,
                        StringComparer.Ordinal);
            return fieldDefinitions.OrderBy(field => field.ColumnIndex).Select(field => byName.TryGetValue(field.Name,
                out object value) ? value : field.ValueType == FieldValueType.String ? string.Empty : 0L).ToArray();
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
            HasRecordedResponseTimestamp = !string.IsNullOrEmpty(responseTimestamp);
            if (!string.IsNullOrEmpty(responseTimestamp))
            {
                Duration = DateTime.Parse(responseTimestamp) - RequestTime;
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

            ClientProcessInfo process = ClientProcessRecordCodec.Read(dataRecord);
            if (ClientProcess.Status != ClientProcessStatus.NotRecorded && process != ClientProcess)
            {
                process = ClientProcess;
            }
            SetClientProcess(process, process.Status == ClientProcessStatus.NotRecorded && IsProcessResolving);

            _requestHeaders = null;
            _responseHeaders = null;

            // notify whoever is listening that all properties changed (null == all properties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public static string ToDateTimeString(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("o");
        }
    }
}
