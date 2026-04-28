// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class RuleResultTests
    {
        [Fact]
        public void Constructor_SetsProperties()
        {
            var result = new RuleResult("TestRule", "test.endpoint.com", "Test description");
            Assert.Equal("TestRule", result.RuleName);
            Assert.Equal("test.endpoint.com", result.Endpoint);
            Assert.Equal("Test description", result.RuleDescription);
            Assert.Empty(result.Violations);
            Assert.Empty(result.Results);
            Assert.Equal(0, result.ViolationCount);
        }

        [Fact]
        public void AddViolation_WithCalls_AddsViolation()
        {
            var result = new RuleResult("Rule", "endpoint", "desc");
            var calls = new List<ServiceCallItem>
            {
                new ServiceCallItem(1),
                new ServiceCallItem(2)
            };

            result.AddViolation(ViolationLevel.Warning, "Test warning", calls);

            Assert.Equal(1, result.ViolationCount);
            Assert.Equal(ViolationLevel.Warning, result.Violations[0].m_level);
            Assert.Equal("Test warning", result.Violations[0].m_summary);
            Assert.Equal(2, result.Violations[0].m_violatingCalls.Count);
        }

        [Fact]
        public void AddViolation_WithSingleCall_AddsViolation()
        {
            var result = new RuleResult("Rule", "endpoint", "desc");
            var call = new ServiceCallItem(1);

            result.AddViolation(ViolationLevel.Error, "Test error", call);

            Assert.Equal(1, result.ViolationCount);
            Assert.Equal(ViolationLevel.Error, result.Violations[0].m_level);
            Assert.Single(result.Violations[0].m_violatingCalls);
        }

        [Fact]
        public void AddViolation_WithoutCalls_AddsViolation()
        {
            var result = new RuleResult("Rule", "endpoint", "desc");
            result.AddViolation(ViolationLevel.Info, "Info message");

            Assert.Equal(1, result.ViolationCount);
            Assert.Equal(ViolationLevel.Info, result.Violations[0].m_level);
            Assert.Empty(result.Violations[0].m_violatingCalls);
        }

        [Fact]
        public void CountViolationLevel_CountsCorrectly()
        {
            var result = new RuleResult("Rule", "endpoint", "desc");
            result.AddViolation(ViolationLevel.Warning, "warn1");
            result.AddViolation(ViolationLevel.Warning, "warn2");
            result.AddViolation(ViolationLevel.Error, "error1");
            result.AddViolation(ViolationLevel.Info, "info1");

            Assert.Equal(2, result.CountViolationLevel(ViolationLevel.Warning));
            Assert.Equal(1, result.CountViolationLevel(ViolationLevel.Error));
            Assert.Equal(1, result.CountViolationLevel(ViolationLevel.Info));
        }

        [Fact]
        public void FindResultByKey_ReturnsValue_WhenKeyExists()
        {
            var result = new RuleResult("Rule", "endpoint", "desc");
            result.Results["TotalCalls"] = 42;

            Assert.Equal(42, result.FindResultByKey("TotalCalls"));
        }

        [Fact]
        public void FindResultByKey_ReturnsNull_WhenKeyMissing()
        {
            var result = new RuleResult("Rule", "endpoint", "desc");
            Assert.Null(result.FindResultByKey("NonExistent"));
        }

        [Fact]
        public void ViolationCount_MatchesViolationsList()
        {
            var result = new RuleResult("Rule", "endpoint", "desc");
            result.AddViolation(ViolationLevel.Warning, "w1");
            result.AddViolation(ViolationLevel.Error, "e1");
            result.AddViolation(ViolationLevel.Info, "i1");

            Assert.Equal(3, result.ViolationCount);
            Assert.Equal(result.Violations.Count, result.ViolationCount);
        }
    }
}
