// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class ServiceCallItemTests
    {
        [Fact]
        public void Constructor_WithId_SetsId()
        {
            var item = new ServiceCallItem(42);
            Assert.Equal(42u, item.Id);
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            var item = new ServiceCallItem(1);
            Assert.Equal(0u, item.HttpStatusCode);
            Assert.Equal(0ul, item.ReqBodyHash);
            Assert.Equal(0ul, item.ElapsedCallTimeMs);
            Assert.Equal(0ul, item.ReqTimeUTC);
            Assert.False(item.IsShoulderTap);
            Assert.False(item.IsInGameEvent);
            Assert.Equal(string.Empty, item.ConsoleIP);
            Assert.NotNull(item.BreadCrumb);
        }

        [Fact]
        public void Copy_CreatesDeepCopy()
        {
            var original = new ServiceCallItem(1)
            {
                Host = "test.host.com",
                Uri = "https://test.host.com/api",
                XboxUserId = "xuid123",
                ReqHeader = "Header: value",
                ReqBody = "request body",
                RspHeader = "Response: header",
                RspBody = "response body",
                ConsoleIP = "10.0.0.1",
                HttpStatusCode = 200,
                ReqBodyHash = 12345,
                ElapsedCallTimeMs = 100,
                ReqTimeUTC = 999999,
                Method = "GET",
                MultiplayerCorrelationId = "corr-id",
                PlayerSessionId = "session-id",
                EventName = "TestEvent",
                Dimensions = "dim1",
                Measurements = "m1",
                IsShoulderTap = true,
                IsInGameEvent = true
            };

            var copy = original.Copy();

            Assert.NotSame(original, copy);
            Assert.Equal(original.Host, copy.Host);
            Assert.Equal(original.Uri, copy.Uri);
            Assert.Equal(original.XboxUserId, copy.XboxUserId);
            Assert.Equal(original.ReqHeader, copy.ReqHeader);
            Assert.Equal(original.ReqBody, copy.ReqBody);
            Assert.Equal(original.RspHeader, copy.RspHeader);
            Assert.Equal(original.RspBody, copy.RspBody);
            Assert.Equal(original.ConsoleIP, copy.ConsoleIP);
            Assert.Equal(original.HttpStatusCode, copy.HttpStatusCode);
            Assert.Equal(original.ReqBodyHash, copy.ReqBodyHash);
            Assert.Equal(original.ElapsedCallTimeMs, copy.ElapsedCallTimeMs);
            Assert.Equal(original.ReqTimeUTC, copy.ReqTimeUTC);
            Assert.Equal(original.Method, copy.Method);
            Assert.Equal(original.MultiplayerCorrelationId, copy.MultiplayerCorrelationId);
            Assert.Equal(original.PlayerSessionId, copy.PlayerSessionId);
            Assert.Equal(original.EventName, copy.EventName);
            Assert.Equal(original.Dimensions, copy.Dimensions);
            Assert.Equal(original.IsShoulderTap, copy.IsShoulderTap);
            Assert.Equal(original.IsInGameEvent, copy.IsInGameEvent);
            Assert.Equal(original.BreadCrumb, copy.BreadCrumb);
        }

        [Fact]
        public void Stringify_ReturnsCommaSeparatedValues()
        {
            var item = new ServiceCallItem(5)
            {
                Host = "host.com",
                Uri = "https://host.com/api",
                XboxUserId = "xuid",
                ConsoleIP = "10.0.0.1",
                HttpStatusCode = 200,
                Method = "GET",
                MultiplayerCorrelationId = "corr",
                PlayerSessionId = "sess",
                EventName = "event",
                Dimensions = "dim",
                Measurements = "meas",
                IsShoulderTap = false,
                IsInGameEvent = true
            };

            string result = item.Stringify();
            Assert.Contains("host.com", result);
            Assert.Contains("200", result);
            Assert.Contains("GET", result);
            Assert.Contains(",", result);
        }
    }
}
