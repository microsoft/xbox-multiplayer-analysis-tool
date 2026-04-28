// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class RuleViolationTests
    {
        [Fact]
        public void ToString_WithoutCalls_FormatsCorrectly()
        {
            var violation = new RuleViolation
            {
                m_level = ViolationLevel.Warning,
                m_endpoint = "test.endpoint.com",
                m_summary = "Test violation message"
            };

            string result = violation.ToString();
            Assert.Contains("[Warning]", result);
            Assert.Contains("[test.endpoint.com]", result);
            Assert.Contains("Test violation message", result);
        }

        [Fact]
        public void ToString_WithCalls_IncludesIds()
        {
            var violation = new RuleViolation
            {
                m_level = ViolationLevel.Error,
                m_endpoint = "api.endpoint.com",
                m_summary = "Error message"
            };
            violation.m_violatingCalls.Add(new ServiceCallItem(1));
            violation.m_violatingCalls.Add(new ServiceCallItem(2));

            string result = violation.ToString();
            Assert.Contains("[Error]", result);
            Assert.Contains("[api.endpoint.com]", result);
            Assert.Contains("Error message", result);
            Assert.Contains("ID(s):", result);
        }

        [Fact]
        public void DefaultValues_AreCorrect()
        {
            var violation = new RuleViolation();
            Assert.Equal(ViolationLevel.Warning, violation.m_level);
            Assert.Equal(string.Empty, violation.m_endpoint);
            Assert.Equal(string.Empty, violation.m_summary);
            Assert.Empty(violation.m_violatingCalls);
        }
    }
}
