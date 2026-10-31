// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using CaptureAnalysisEngine;
using XMAT.Operations.Analysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.Tests.Operations.Analysis
{
    public class AnalysisDataRecordBodyTests
    {
        [Fact]
        public void BodyStrings_AreBase64EncodedOnDemandWithoutCaching()
        {
            byte[] request =
                Encoding.UTF8.GetBytes(
                    "{\"xuids\":[\"1\",\"2\"],\"ok\":true}");
            byte[] response =
            {
                0,
                1,
                2,
                127,
                128,
                255,
            };
            AnalyzeCaptureOperation.AnalysisDataRecord record =
                AnalyzeCaptureOperation
                    .AnalysisDataRecord
                    .FromProxyModel(
                        7,
                        Model(
                            request,
                            response));

            string firstRequest =
                record.Str(
                    WebServiceCaptureMethod
                        .FieldKey_RequestBody);
            string secondRequest =
                record.Str(
                    WebServiceCaptureMethod
                        .FieldKey_RequestBody);
            string firstResponse =
                record.Str(
                    WebServiceCaptureMethod
                        .FieldKey_ResponseBody);
            string secondResponse =
                record.Str(
                    WebServiceCaptureMethod
                        .FieldKey_ResponseBody);

            Assert.Equal(
                Convert.ToBase64String(request),
                firstRequest);
            Assert.Equal(
                Convert.ToBase64String(response),
                firstResponse);
            Assert.Equal(
                firstRequest,
                secondRequest);
            Assert.Equal(
                firstResponse,
                secondResponse);
            Assert.NotSame(
                firstRequest,
                secondRequest);
            Assert.NotSame(
                firstResponse,
                secondResponse);
        }

        [Fact]
        public void NullAndEmptyBodies_RemainEmptyStrings()
        {
            AnalyzeCaptureOperation.AnalysisDataRecord record =
                AnalyzeCaptureOperation
                    .AnalysisDataRecord
                    .FromProxyModel(
                        8,
                        Model(
                            null,
                            Array.Empty<byte>()));

            Assert.Equal(
                string.Empty,
                record.Str(
                    WebServiceCaptureMethod
                        .FieldKey_RequestBody));
            Assert.Equal(
                string.Empty,
                record.Str(
                    WebServiceCaptureMethod
                        .FieldKey_ResponseBody));
        }

        [Fact]
        public void NonBodyFields_AreUnchanged()
        {
            AnalyzeCaptureOperation.AnalysisDataRecord record =
                AnalyzeCaptureOperation
                    .AnalysisDataRecord
                    .FromProxyModel(
                        9,
                        Model(
                            new byte[] { 1 },
                            new byte[] { 2 }));

            Assert.Equal(9, record.RowId);
            Assert.Equal(
                42L,
                record.Int(
                    WebServiceCaptureMethod
                        .FieldKey_RequestNumber));
            Assert.Equal(
                "example.com",
                record.Str(
                    WebServiceCaptureMethod
                        .FieldKey_RequestHost));
            Assert.Equal(
                "/path",
                record.Str(
                    WebServiceCaptureMethod
                        .FieldKey_RequestPath));
            Assert.Null(
                record.Str("missing"));
        }

        [Fact]
        public void ConverterUsesRawBodiesWithoutCallingBodyStr()
        {
            byte[] request =
            {
                (byte)'r',
                0x80,
                (byte)'q',
            };
            byte[] response =
            {
                0,
                1,
                127,
                255,
            };
            var record = new RawBodyRecord(
                request,
                response);

            ServiceCallItem item =
                WebServiceCaptureRecordConverter.Convert(
                    record);

            string expectedRequest =
                Encoding.ASCII.GetString(request);
            Assert.Equal(expectedRequest, item.ReqBody);
            Assert.Equal(
                Encoding.ASCII.GetString(response),
                item.RspBody);
            Assert.Equal(0, record.BodyStringReads);
            Assert.Equal(
                (ulong)expectedRequest.GetHashCode(),
                item.ReqBodyHash);
        }

        [Fact]
        public void ConverterIdentityUsesAsciiDecodedRequestBody()
        {
            var identities =
                new RequestBodyIdentityInterner();
            ServiceCallItem first =
                WebServiceCaptureRecordConverter.Convert(
                    new RawBodyRecord(
                        new byte[] { 0x80 },
                        Array.Empty<byte>()),
                    identities);
            ServiceCallItem second =
                WebServiceCaptureRecordConverter.Convert(
                    new RawBodyRecord(
                        new byte[] { 0x81 },
                        Array.Empty<byte>()),
                    identities);

            Assert.Equal(
                first.ReqBody,
                second.ReqBody);
            Assert.Same(
                first.BodyIdentity,
                second.BodyIdentity);
        }

        private sealed class RawBodyRecord :
            IDataRecord,
            IRawBodyDataRecord
        {
            private readonly byte[] _request;
            private readonly byte[] _response;

            internal RawBodyRecord(
                byte[] request,
                byte[] response)
            {
                _request = request;
                _response = response;
            }

            public int RowId => 1;

            internal int BodyStringReads { get; private set; }

            public long Int(string fieldName) =>
                fieldName is
                    WebServiceCaptureMethod.FieldKey_RequestNumber or
                    WebServiceCaptureMethod.FieldKey_ConnectionId
                        ? 1
                        : 0;

            public string Str(string fieldName)
            {
                if (fieldName is
                    WebServiceCaptureMethod.FieldKey_RequestBody or
                    WebServiceCaptureMethod.FieldKey_ResponseBody)
                {
                    BodyStringReads++;
                    throw new InvalidOperationException(
                        "The raw-body fast path was not used.");
                }

                return fieldName switch
                {
                    WebServiceCaptureMethod.FieldKey_RequestTimestamp =>
                        ProxyConnectionModel.ToDateTimeString(
                            DateTime.UnixEpoch),
                    WebServiceCaptureMethod.FieldKey_ResponseTimestamp =>
                        ProxyConnectionModel.ToDateTimeString(
                            DateTime.UnixEpoch.AddMilliseconds(1)),
                    WebServiceCaptureMethod.FieldKey_RequestScheme =>
                        "https",
                    WebServiceCaptureMethod.FieldKey_RequestHost =>
                        "example.test",
                    WebServiceCaptureMethod.FieldKey_RequestPort =>
                        "443",
                    WebServiceCaptureMethod.FieldKey_RequestMethod =>
                        "POST",
                    WebServiceCaptureMethod.FieldKey_RequestPath =>
                        "/path",
                    WebServiceCaptureMethod.FieldKey_RequestStatus =>
                        "200",
                    WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders =>
                        "POST /path HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "User-Agent: XboxServicesAPI\r\n",
                    WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders =>
                        "HTTP/1.1 200 OK\r\n",
                    WebServiceCaptureMethod.FieldKey_ClientIP =>
                        "10.0.0.1",
                    _ => string.Empty,
                };
            }

            public ReadOnlyMemory<byte> RawBody(
                string fieldName) =>
                fieldName ==
                    WebServiceCaptureMethod.FieldKey_RequestBody
                        ? _request
                        : _response;
        }

        private static ProxyConnectionModel Model(
            byte[] request,
            byte[] response) =>
            new(
                42,
                "200",
                new DateTime(
                    2025,
                    1,
                    2,
                    3,
                    4,
                    5,
                    DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(25),
                "https",
                "example.com:443",
                "POST",
                "/path",
                "POST /path HTTP/1.1\r\nHost: example.com\r\n",
                request,
                "HTTP/1.1 200 OK\r\n",
                response,
                "10.0.0.1");
    }
}
