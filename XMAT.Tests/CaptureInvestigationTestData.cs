// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using CaptureAnalysisEngine;
using XMAT.DataAbstractionLayer;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.XboxLiveCaptureAnalysis;
using Web = XMAT.WebServiceCapture.WebServiceCaptureMethod;

namespace XMAT.Tests
{
    internal static class CaptureInvestigationTestData
    {
        internal static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        internal const string Secret = "PRIVATE_PAYLOAD_DO_NOT_RETURN";

        internal static SqLtDatabase WebDatabase()
        {
            var database = new SqLtDatabase();
            database.Initialize();
            database.CreateTable(Web.TableName,
                new Field<string>(Web.FieldKey_DeviceName), new Field<long>(Web.FieldKey_RequestNumber),
                new Field<long>(Web.FieldKey_ConnectionId), new Field<string>(Web.FieldKey_RequestTimestamp),
                new Field<string>(Web.FieldKey_ResponseTimestamp), new Field<string>(Web.FieldKey_RequestScheme),
                new Field<string>(Web.FieldKey_RequestHost), new Field<string>(Web.FieldKey_RequestPort),
                new Field<string>(Web.FieldKey_RequestMethod), new Field<string>(Web.FieldKey_RequestPath),
                new Field<string>(Web.FieldKey_RequestStatus), new Field<string>(Web.FieldKey_RequestLineAndHeaders),
                new Field<string>(Web.FieldKey_RequestBody), new Field<string>(Web.FieldKey_ResponseLineAndHeaders),
                new Field<string>(Web.FieldKey_ResponseBody), new Field<string>(Web.FieldKey_ClientIP));
            return database;
        }

        internal static long AddWeb(
            SqLtDatabase database, int number, int status = 429, int duration = 100,
            string console = "10.0.0.1", string device = "test-device", string host = "api.xboxlive.com", bool complete = true)
        {
            var time = Start.AddSeconds(number);
            return database.TableByName(Web.TableName).AddRow(
                device, (long)number, (long)number, time.ToString("o"), complete ? time.AddMilliseconds(duration).ToString("o") : string.Empty,
                "https", host, "443", "GET", $"/users/xuid(123)/calls?token={Secret}",
                status.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"GET /?token={Secret} HTTP/1.1\r\nHost: {host}\r\nx-xbl-api-build-version: adk\r\nAuthorization: {Secret}\r\n\r\n",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)), $"HTTP/1.1 {status} Test\r\nSecret: {Secret}\r\n\r\n",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)), console);
        }

        internal static XboxLiveCaptureAnalyzer Analyzer(Rule rule = null)
        {
            var trace = new TraceAnalyzer();
            trace.AddRule(rule ?? new ThrottledCallsRule { Endpoint = "*" });
            trace.AddReports([new PerEndpointReport(), new StatsReport(), new CallReport()]);
            using var map = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
                "\"api.xboxlive.com\",\"test_service\",\"TestService\",\"XblTest\"\n")));
            trace.LoadURIMap(map);
            return new XboxLiveCaptureAnalyzer(trace);
        }

        internal static SqLtDatabase NetworkDatabase()
        {
            var database = new SqLtDatabase();
            database.Initialize();
            database.CreateTable(NetworkTraceCaptureMethod.TableName,
                new Field<string>("DeviceName"), new Field<int>("ProcessId"), new Field<int>("ThreadId"),
                new Field<string>("Timestamp"), new Field<string>("MediaType"), new Field<int>("StartPacket"),
                new Field<int>("EndPacket"), new Field<int>("Fragment"), new Field<int>("Send"),
                new Field<int>("Receive"), new Field<string>("Payload"));
            return database;
        }

        internal static long AddPacket(SqLtDatabase database, DateTime timestamp, byte marker = 0, bool send = true,
            bool shortPacket = false, string device = "test-device")
        {
            var bytes = shortPacket ? new byte[2] : new byte[40];
            if (!shortPacket)
            {
                bytes[14] = 0x45;
                bytes[17] = 26;
                bytes[23] = 17;
                bytes[26] = bytes[30] = 10;
                bytes[29] = 1;
                bytes[33] = 2;
                bytes[34] = marker;
            }
            return database.TableByName(NetworkTraceCaptureMethod.TableName).AddRow(
                device, 1, 2, timestamp.ToString("o"), "ethernet", 1, 1, 0,
                send ? 1 : 0, send ? 0 : 1, Convert.ToBase64String(bytes));
        }

        internal sealed class SensitiveRule : BaseRule<SensitiveRule>
        {
            public SensitiveRule() { Endpoint = "*"; }
            public override void DeserializeJson(JsonElement json) { }
            public override RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
            {
                var result = InitializeResult("TestRule", Secret);
                result.Results.Add("private-value", Secret);
                result.AddViolation(ViolationLevel.Error, Secret, items.ToArray());
                return result;
            }
        }

        internal sealed class GatedRule(ManualResetEventSlim started, ManualResetEventSlim release) : BaseRule<GatedRule>
        {
            public override void DeserializeJson(JsonElement json) { }
            public override RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The test rule was not released.");
                return InitializeResult("GatedRule", string.Empty);
            }
        }
    }
}
