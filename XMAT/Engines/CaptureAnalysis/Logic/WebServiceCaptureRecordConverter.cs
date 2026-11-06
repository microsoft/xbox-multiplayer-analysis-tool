// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Net;
using System.Text;
using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.XboxLiveCaptureAnalysis
{
    internal interface IRawBodyDataRecord
    {
        ReadOnlyMemory<byte> RawBody(string fieldName);
    }

    internal static class WebServiceCaptureRecordConverter
    {
        private static readonly string[] AllowedNonAnalyzedHosts =
        {
            "playfab.com",
            "playfabapi.com",
        };

        internal static ServiceCallItem Convert(IDataRecord dataRecord)
        {
            return Convert(
                dataRecord,
                null);
        }

        internal static ServiceCallItem Convert(
            IDataRecord dataRecord,
            RequestBodyIdentityInterner bodyIdentities)
        {
            var requestNumber = dataRecord.Int(WebServiceCaptureMethod.FieldKey_RequestNumber);
            var requestTimestamp = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestTimestamp);
            var responseTimestamp = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ResponseTimestamp);
            var requestScheme = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestScheme);
            var requestHost = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestHost);
            var requestPort = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestPort);
            var requestMethod = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestMethod);
            var requestPath = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestPath);
            var requestStatus = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestStatus);
            var requestLineAndHeaders = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders);
            var responseLineAndHeaders = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders);
            var clientIP = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ClientIP);

            var item = new ServiceCallItem((UInt32)requestNumber);

            if (requestMethod == "CONNECT")
            {
                return null;
            }

            if (string.IsNullOrEmpty(requestTimestamp) ||
                string.IsNullOrEmpty(responseTimestamp))
            {
                return null;
            }

            var requestHeaders = GetRequestHeadersForString(requestLineAndHeaders);
            if (!Utils.IsAnalyzedService(requestHeaders, null) &&
                !AllowedNonAnalyzedHosts.Any(host => requestHost.Contains(host)))
            {
                return null;
            }

            var requestTime = DateTime.Parse(requestTimestamp);
            var duration = DateTime.Parse(responseTimestamp) - requestTime;

            item.Host = string.IsNullOrWhiteSpace(requestHost)
                ? string.Empty
                : requestHeaders[HttpRequestHeader.Host] ??
                    requestHost.Split(':')[0];

            // A stored hostless capture remains hostless; otherwise the logical Host label keeps
            // the captured-header behavior. Host-list actions use separate physical provenance.
            item.EndpointKind = EndpointKind.Captured;
            item.PhysicalHost = GetPhysicalHost(requestHost, requestHeaders[HttpRequestHeader.Host]);

            // A hostless capture keeps its request path without reconstructing an authority.
            item.Uri = string.IsNullOrEmpty(item.Host)
                ? requestPath ?? string.Empty
                : UriUtils.GetAbsoluteUri(
                    requestScheme,
                    requestHost,
                    requestPort,
                    requestPath);
            item.XboxUserId = Utils.GetXboxUserID(requestPath);
            item.ReqHeader = requestLineAndHeaders;
            item.ReqBody = DecodeBody(
                dataRecord,
                WebServiceCaptureMethod.FieldKey_RequestBody);
            item.RspHeader = responseLineAndHeaders;
            item.RspBody = DecodeBody(
                dataRecord,
                WebServiceCaptureMethod.FieldKey_ResponseBody);
            item.ConsoleIP = clientIP;
            item.HttpStatusCode = string.IsNullOrEmpty(requestStatus)
                ? 0
                : System.Convert.ToUInt32(requestStatus);
            item.ReqBodyHash = (UInt64)item.ReqBody.GetHashCode();
            item.BodyIdentity =
                bodyIdentities?.GetOrAdd(
                    item.ReqBody);
            item.ElapsedCallTimeMs = (ulong)duration.TotalMilliseconds;
            item.ReqTimeUTC = (ulong)requestTime.ToFileTimeUtc();
            item.Method = requestMethod;

            return item;
        }

        private static string DecodeBody(
            IDataRecord dataRecord,
            string fieldName)
        {
            if (dataRecord is IRawBodyDataRecord raw)
            {
                return Encoding.ASCII.GetString(
                    raw.RawBody(fieldName).Span);
            }

            return Encoding.ASCII.GetString(
                System.Convert.FromBase64String(
                    dataRecord.Str(fieldName)));
        }

        private static WebHeaderCollection GetRequestHeadersForString(
            string requestLineAndHeaders)
        {
            var headers = new WebHeaderCollection();
            if (!string.IsNullOrEmpty(requestLineAndHeaders))
            {
                var lines = requestLineAndHeaders.Split("\r\n");
                for (var index = 1; index < lines.Length; index++)
                {
                    var line = lines[index];
                    if (!string.IsNullOrEmpty(line) && line.Contains(':'))
                    {
                        headers.Add(line);
                    }
                }
            }
            return headers;
        }

        private static string GetPhysicalHost(string structuredHost, string hostHeader)
        {
            if (HostAuthority.TryNormalizeHost(structuredHost, out string host))
            {
                return host;
            }
            return HostAuthority.TryParse(hostHeader, out host, out _) ? host : null;
        }
    }
}
