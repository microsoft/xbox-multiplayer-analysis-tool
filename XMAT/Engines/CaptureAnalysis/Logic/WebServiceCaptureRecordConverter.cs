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
using XMAT.WebServiceCapture.Models;

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
            // A call's time is a FILETIME (ReqTimeUTC), which starts in 1601. A request whose time is unknown or earlier
            // than that cannot be placed among the calls that rules compare in time, so like a request without its times it
            // is not analyzed, rather than failing the whole analysis.
            if (!ProxyConnectionModel.IsPlaced(requestTime))
            {
                return null;
            }

            // The time between the two instants, never between their local clock readings, which differ by an hour across
            // a daylight saving change.
            var duration = ProxyConnectionModel.Elapsed(requestTime, DateTime.Parse(responseTimestamp));

            item.Host = string.IsNullOrWhiteSpace(requestHost)
                ? string.Empty
                : requestHeaders[HttpRequestHeader.Host] ?? LogicalHost(requestHost);

            // A stored hostless capture remains hostless; otherwise the logical Host label keeps
            // the captured-header behavior. Host-list actions use separate physical provenance.
            item.EndpointKind = EndpointKind.Captured;
            item.PhysicalHost = GetPhysicalHost(requestHost, requestHeaders[HttpRequestHeader.Host]);

            // A hostless capture keeps its request path without reconstructing an authority, and so does one whose scheme
            // makes no URL: an unknown scheme, or the method that earlier imports stored as the scheme.
            item.Uri = string.IsNullOrEmpty(item.Host) || !UriUtils.IsUrlScheme(requestScheme)
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

        // The request headers that analysis reads, Host and those that name an analyzed service. A line that
        // WebHeaderCollection refuses, such as a name with an apostrophe that replay sends and the SAZ readers keep, is
        // left out of these headers only; the stored head (ReqHeader) keeps it.
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
                        try
                        {
                            headers.Add(line);
                        }
                        catch (ArgumentException)
                        {
                            // Not a header that WebHeaderCollection holds.
                        }
                    }
                }
            }
            return headers;
        }

        // The label of a row without a Host header: its stored host, which an IPv6 address is whole in, where the former
        // split at the first colon kept only its first group; any other text still keeps what precedes a colon.
        private static string LogicalHost(string structuredHost) =>
            HostAuthority.TryNormalizeHost(structuredHost, out string host) ? host : structuredHost.Split(':')[0];

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
