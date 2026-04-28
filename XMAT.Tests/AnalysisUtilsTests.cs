// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class AnalysisUtilsTests
    {
        #region IsSuccessHTTPStatusCode

        [Theory]
        [InlineData(200u, true)]
        [InlineData(201u, true)]
        [InlineData(204u, true)]
        [InlineData(299u, true)]
        [InlineData(1223u, true)]  // MSXML XHR bug workaround
        [InlineData(100u, false)]
        [InlineData(199u, false)]
        [InlineData(300u, false)]
        [InlineData(404u, false)]
        [InlineData(500u, false)]
        [InlineData(0u, false)]
        public void IsSuccessHTTPStatusCode_ReturnsExpected(uint statusCode, bool expected)
        {
            Assert.Equal(expected, Utils.IsSuccessHTTPStatusCode(statusCode));
        }

        #endregion

        #region GetCSVValues

        [Fact]
        public void GetCSVValues_ParsesSimpleCSV()
        {
            string input = "\"value1\",\"value2\",\"value3\"";
            var result = Utils.GetCSVValues(input);
            Assert.Equal(3, result.Length);
            Assert.Equal("value1", result[0]);
            Assert.Equal("value2", result[1]);
            Assert.Equal("value3", result[2]);
        }

        [Fact]
        public void GetCSVValues_HandlesSingleValue()
        {
            string input = "\"onlyvalue\"";
            var result = Utils.GetCSVValues(input);
            Assert.Single(result);
            Assert.Equal("onlyvalue", result[0]);
        }

        [Fact]
        public void GetCSVValues_HandlesEscapedQuotes()
        {
            string input = "\"val\"\"ue1\",\"value2\"";
            var result = Utils.GetCSVValues(input);
            Assert.Equal(2, result.Length);
            Assert.Equal("val\"ue1", result[0]);
            Assert.Equal("value2", result[1]);
        }

        #endregion

        #region GetFrameNumber

        [Fact]
        public void GetFrameNumber_ExtractsNumber()
        {
            Assert.Equal(42, Utils.GetFrameNumber("42_c.txt"));
        }

        [Fact]
        public void GetFrameNumber_ExtractsLargeNumber()
        {
            Assert.Equal(12345, Utils.GetFrameNumber("12345_s.txt"));
        }

        #endregion

        #region GetXboxUserID

        [Fact]
        public void GetXboxUserID_ExtractsXuid()
        {
            string line = "https://userpresence.xboxlive.com/users/xuid(2669321029139235)/devices";
            string xuid = Utils.GetXboxUserID(line);
            Assert.Equal("2669321029139235", xuid);
        }

        [Fact]
        public void GetXboxUserID_ReturnsEmpty_WhenNoXuid()
        {
            string line = "https://userpresence.xboxlive.com/users/devices";
            string xuid = Utils.GetXboxUserID(line);
            Assert.Equal("", xuid);
        }

        #endregion

        #region SafeAssign

        [Fact]
        public void SafeAssign_AssignsValue_WhenPropertyExists()
        {
            var json = JsonDocument.Parse("{\"name\": \"test\"}").RootElement;
            string value = "";
            Utils.SafeAssign(json, "name", ref value);
            Assert.Equal("test", value);
        }

        [Fact]
        public void SafeAssign_DoesNotModify_WhenPropertyMissing()
        {
            var json = JsonDocument.Parse("{\"other\": \"test\"}").RootElement;
            string value = "original";
            Utils.SafeAssign(json, "missing", ref value);
            Assert.Equal("original", value);
        }

        [Fact]
        public void SafeAssign_AssignsIntValue()
        {
            var json = JsonDocument.Parse("{\"count\": 42}").RootElement;
            int value = 0;
            Utils.SafeAssign(json, "count", ref value);
            Assert.Equal(42, value);
        }

        [Fact]
        public void SafeAssign_AssignsBoolValue()
        {
            var json = JsonDocument.Parse("{\"flag\": true}").RootElement;
            bool value = false;
            Utils.SafeAssign(json, "flag", ref value);
            Assert.True(value);
        }

        #endregion

        #region GetCallsBetweenRange

        [Fact]
        public void GetCallsBetweenRange_ReturnsCorrectRange()
        {
            var items = new List<ServiceCallItem>();
            for (uint i = 0; i < 10; i++)
            {
                items.Add(new ServiceCallItem(i) { Uri = $"http://test/{i}" });
            }

            var result = Utils.GetCallsBetweenRange(items, 2, 5);
            Assert.Equal(4, result.Count);
            Assert.Equal(items[2], result.First.Value);
            Assert.Equal(items[5], result.Last.Value);
        }

        [Fact]
        public void GetCallsBetweenRange_ReturnsSingleItem()
        {
            var items = new List<ServiceCallItem>();
            for (uint i = 0; i < 5; i++)
            {
                items.Add(new ServiceCallItem(i));
            }

            var result = Utils.GetCallsBetweenRange(items, 3, 3);
            Assert.Single(result);
        }

        #endregion

        #region GetExcessCallsForTimeWindow

        [Fact]
        public void GetExcessCallsForTimeWindow_ReturnsEmpty_WhenFewCalls()
        {
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0) { ReqTimeUTC = 1000 }
            };

            var result = Utils.GetExcessCallsForTimeWindow(items, 1000, 10);
            Assert.Empty(result);
        }

        [Fact]
        public void GetExcessCallsForTimeWindow_ReturnsEmpty_WhenCallsWithinLimit()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>();
            for (uint i = 0; i < 5; i++)
            {
                items.Add(new ServiceCallItem(i)
                {
                    ReqTimeUTC = baseTime + (ulong)(i * TimeSpan.TicksPerMillisecond * 100)
                });
            }

            var result = Utils.GetExcessCallsForTimeWindow(items, 1000, 10);
            Assert.Empty(result);
        }

        [Fact]
        public void GetExcessCallsForTimeWindow_DetectsExcessCalls()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>();
            // Create 20 calls within a 500ms window (exceeds limit of 5)
            for (uint i = 0; i < 20; i++)
            {
                items.Add(new ServiceCallItem(i)
                {
                    ReqTimeUTC = baseTime + (ulong)(i * TimeSpan.TicksPerMillisecond * 10)
                });
            }

            var result = Utils.GetExcessCallsForTimeWindow(items, 500, 5);
            Assert.NotEmpty(result);
        }

        [Fact]
        public void GetExcessCallsForTimeWindow_IgnoresShoulderTaps()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>();
            for (uint i = 0; i < 20; i++)
            {
                items.Add(new ServiceCallItem(i)
                {
                    ReqTimeUTC = baseTime + (ulong)(i * TimeSpan.TicksPerMillisecond * 10),
                    IsShoulderTap = true
                });
            }

            var result = Utils.GetExcessCallsForTimeWindow(items, 500, 5);
            Assert.Empty(result);
        }

        #endregion

        #region PrintCallIdRange

        [Fact]
        public void PrintCallIdRange_FormatsConsecutiveRange()
        {
            var sb = new System.Text.StringBuilder();
            var calls = new List<ServiceCallItem>();
            for (uint i = 1; i <= 15; i++)
            {
                calls.Add(new ServiceCallItem(i));
            }

            Utils.PrintCallIdRange(sb, calls, 5);
            Assert.Contains("1-15", sb.ToString());
        }

        [Fact]
        public void PrintCallIdRange_FormatsIndividualIds_WhenBelowMinRange()
        {
            var sb = new System.Text.StringBuilder();
            var calls = new List<ServiceCallItem>
            {
                new ServiceCallItem(1),
                new ServiceCallItem(2)
            };

            Utils.PrintCallIdRange(sb, calls, 5);
            string result = sb.ToString();
            Assert.Contains("1, ", result);
            Assert.Contains("2, ", result);
        }

        [Fact]
        public void PrintCallIdRange_DoesNothing_ForEmptyCalls()
        {
            var sb = new System.Text.StringBuilder();
            Utils.PrintCallIdRange(sb, new List<ServiceCallItem>(), 5);
            Assert.Equal("", sb.ToString());
        }

        #endregion

        #region HasStaticProperty / GetStaticProperty

        [Fact]
        public void HasStaticProperty_ReturnsTrue_ForExistingProperty()
        {
            // DateTime.Now is a public static property
            bool result = Utils.HasStaticProperty("Now", typeof(DateTime));
            Assert.True(result);
        }

        [Fact]
        public void HasStaticProperty_ReturnsFalse_ForMissingProperty()
        {
            bool result = Utils.HasStaticProperty("NonExistent", typeof(DateTime));
            Assert.False(result);
        }

        [Fact]
        public void GetStaticProperty_ReturnsValue()
        {
            // DateTime.UtcNow is a static property that returns the current UTC time
            var result = Utils.GetStaticProperty("UtcNow", typeof(DateTime));
            Assert.NotNull(result);
            Assert.IsType<DateTime>(result);
        }

        #endregion
    }
}
