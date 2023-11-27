// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
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

        public Int64 RecordRowId { get; private set; }

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
        public byte[] RequestBody { get; private set; }
        public string ResponseLineAndHeaders { get; private set; }
        public byte[] ResponseBody { get; private set; }
        public string ClientIP { get; private set; }

        public Dictionary<string, string> RequestHeaders { get; private set; }
        public Dictionary<string, string> ResponseHeaders { get; private set; }

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
            RequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ResponseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            var hostAndPortSplit = hostAndPort.Split(':');
            Host = hostAndPortSplit[0];
            Port = hostAndPortSplit.Length > 1 ? hostAndPortSplit[1] : string.Empty;
            Method = method;
            // TODO: possibly remove the host & port?
            Path = call;
            RequestLineAndHeaders = requestLineAndHeaders;
            ResponseLineAndHeaders = responseLineAndHeaders;
            RequestBody = requestBody;
            ResponseBody = responseBody;
            ClientIP = clientIP;

            RequestHeaders = StringToHeaders(RequestLineAndHeaders);
            ResponseHeaders = StringToHeaders(ResponseLineAndHeaders);

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

            RequestHeaders = model.RequestHeaders;
            ResponseHeaders = model.ResponseHeaders;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        private Dictionary<string, string> StringToHeaders(string firstLineAndHeaders)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if(!string.IsNullOrEmpty(firstLineAndHeaders))
            {
                string[] lines = firstLineAndHeaders.Split("\r\n");

                for(int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string[] header = line.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if(header.Length < 2)
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
            UpdateFromDataRecord(dataRecord);
        }

        internal void AddToDataTable(string deviceName, IDataTable dataTable)
        {
            var requestBodyAsBase64 = RequestBody != null && RequestBody.Length > 0 ? Convert.ToBase64String(RequestBody) : string.Empty;
            var responseBodyAsBase64 = ResponseBody != null && ResponseBody.Length > 0 ? Convert.ToBase64String(ResponseBody) : string.Empty;

            // see layout in WebServiceCaptureMethod.InitializeDataTables()

            RecordRowId = dataTable.AddRow(
                deviceName,
                RequestNumber,
                Id,
                ToDateTimeString(RequestTime),
                ToDateTimeString(RequestTime + Duration),
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
            );
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

            RequestHeaders = StringToHeaders(RequestLineAndHeaders);
            ResponseHeaders = StringToHeaders(ResponseLineAndHeaders);

            // notify whoever is listening that all properties changed (null == all properties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public static string ToDateTimeString(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("o");
        }
    }
}
