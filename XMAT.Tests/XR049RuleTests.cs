// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class XR049RuleTests
    {
        private const string PresenceEndpoint = "userpresence.xboxlive.com";

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \t\r\n")]
        [InlineData("{}")]
        [InlineData("{\"state\":\"online\"}")]
        [InlineData("{\"other\":{\"activity\":{}}}")]
        [InlineData("{\"Activity\":{}}")]
        [InlineData("null")]
        [InlineData("[]")]
        [InlineData("[{\"activity\":{}}]")]
        [InlineData("\"online\"")]
        [InlineData("42")]
        [InlineData("true")]
        public void Run_SkipsBodiesWithoutAnActivityObject(string requestBody)
        {
            var call = CreateCall(1, requestBody);

            var result = RunRule(call);

            var violation = Assert.Single(result.Violations);
            Assert.Equal(ViolationLevel.Warning, violation.m_level);
            Assert.Empty(violation.m_violatingCalls);

            var resultWithPresence = RunRule(call, CreateCall(2, "{\"activity\":{}}"));

            Assert.Empty(resultWithPresence.Violations);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("<html>not JSON</html>")]
        [InlineData("{")]
        [InlineData("{\"activity\":")]
        [InlineData("{\"activity\":{}} trailing")]
        [InlineData("{\"activity\":{}}\n{\"activity\":{}}")]
        public void Run_ReportsMalformedJsonAndContinuesScanning(string requestBody)
        {
            var parseException = Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(requestBody));
            var call = CreateCall(1, requestBody);

            var result = RunRule(call, CreateCall(2, "{\"activity\":{}}"));

            var violation = Assert.Single(result.Violations);
            Assert.Equal(ViolationLevel.Warning, violation.m_level);
            Assert.Equal(PresenceEndpoint, violation.m_endpoint);
            Assert.Equal(parseException.Message, violation.m_summary);
            Assert.Same(call, Assert.Single(violation.m_violatingCalls));
        }

        [Fact]
        public void Run_ReturnsMissingPresenceViolation_WhenOnlyMalformedJsonIsPresent()
        {
            var call = CreateCall(1, "not json");

            var result = RunRule(call);

            Assert.Equal(2, result.ViolationCount);
            Assert.Same(call, Assert.Single(result.Violations[0].m_violatingCalls));
            Assert.Empty(result.Violations[1].m_violatingCalls);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("null")]
        [InlineData("\"online\"")]
        [InlineData("42")]
        [InlineData("[]")]
        [InlineData("true")]
        public void Run_DetectsActivityRegardlessOfPropertyValue(string activityJson)
        {
            var result = RunRule(CreateCall(1, "{\"activity\":" + activityJson + "}"));

            Assert.Equal(XR049Rule.DisplayName, result.RuleName);
            Assert.Equal(XR049Rule.Description, result.RuleDescription);
            Assert.Equal(PresenceEndpoint, result.Endpoint);
            Assert.Empty(result.Violations);
        }

        [Fact]
        public void Run_ReturnsMissingPresenceViolation_WhenNoCalls()
        {
            var result = RunRule();

            Assert.Single(result.Violations);
        }

        [Fact]
        public void Run_StopsAfterFindingRichPresence()
        {
            IEnumerable<ServiceCallItem> Calls()
            {
                yield return CreateCall(1, "{\"activity\":{}}");
                throw new InvalidOperationException("The rule should stop after finding rich presence.");
            }

            var result = CreateRule().Run(new RulesEngine(false), Calls(), new ServiceCallStats(Array.Empty<ServiceCallItem>()));

            Assert.Empty(result.Violations);
        }

        [Fact]
        public void Run_DoesNotSwallowUnexpectedFailures()
        {
            var exception = new InvalidOperationException("Unexpected enumeration failure.");
            IEnumerable<ServiceCallItem> Calls()
            {
                yield return CreateCall(1, "{}");
                throw exception;
            }

            var thrown = Assert.Throws<InvalidOperationException>(() =>
                CreateRule().Run(new RulesEngine(false), Calls(), new ServiceCallStats(Array.Empty<ServiceCallItem>())));

            Assert.Same(exception, thrown);
        }

        [Theory]
        [InlineData(null, 0)]
        [InlineData("", 0)]
        [InlineData("not json", 1)]
        [InlineData("[]", 0)]
        public void RunRulesOnData_HandlesUnusableBodiesWithoutInterruptingOtherRules(string requestBody, int expectedWarnings)
        {
            var engine = new RulesEngine(false);
            engine.AddRule(CreateRule());
            engine.AddRule(new TestRule { Name = "OtherRule", Endpoint = PresenceEndpoint });
            var calls = new LinkedList<ServiceCallItem>(new[]
            {
                CreateCall(1, requestBody),
                CreateCall(2, "{\"activity\":{}}")
            });
            var data = new ServiceCallData.PerConsoleData();
            data.m_servicesHistory[PresenceEndpoint] = calls;
            data.m_servicesStats[PresenceEndpoint] = new ServiceCallStats(calls);

            engine.RunRulesOnData("Console1", data);

            var results = engine.GetResults("Console1");
            Assert.Equal(2, results.Count);
            var presenceResult = Assert.Single(results, result => result.RuleName == XR049Rule.DisplayName);
            Assert.Equal(expectedWarnings, presenceResult.ViolationCount);
            var otherResult = Assert.Single(results, result => result.RuleName == "OtherRule");
            Assert.Equal(2, otherResult.Results["CallCount"]);
        }

        private static ServiceCallItem CreateCall(uint id, string requestBody)
        {
            return new ServiceCallItem(id) { Host = PresenceEndpoint, ReqBody = requestBody };
        }

        private static XR049Rule CreateRule()
        {
            return new XR049Rule { Name = "XR049Rule_UserPresence", Endpoint = PresenceEndpoint };
        }

        private static RuleResult RunRule(params ServiceCallItem[] calls)
        {
            return CreateRule().Run(new RulesEngine(false), calls, new ServiceCallStats(calls));
        }
    }
}
