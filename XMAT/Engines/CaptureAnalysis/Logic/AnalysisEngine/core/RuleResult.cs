// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CaptureAnalysisEngine
{
    public class RuleResult
    {
        // Name of the Rule the result came from
        public String RuleName { get; set; }
        // Endpoint the had the rule applied
        public String Endpoint { get; set; }
        // Description of the applied rule
        public String RuleDescription { get; set; }
        // List of the violations caught by this rule
        public List<RuleViolation> Violations { get; }
        // Total Number of violations
        public Int32 ViolationCount { get { return Violations.Count; } }
        // Detailed information captured by the rule
        public Dictionary<string, Object> Results { get; }

        public RuleResult(String ruleName, String endpoint, String ruleDescription)
        {
            RuleName = ruleName;
            Endpoint = endpoint;
            RuleDescription = ruleDescription;
            Results = new();
            Violations = new List<RuleViolation>();
        }

        public object FindResultByKey(string key)
        {
            return Results.FirstOrDefault(kv => kv.Key == key).Value;
        }

        // Count the number of violations at the specified level
        public Int32 CountViolationLevel(ViolationLevel level)
        {
            return Violations.Count(v => v.m_level == level);
        }

        // Helper method to add a violation with a list of calls to the list.
        public void AddViolation(ViolationLevel level, String description, IEnumerable<ServiceCallItem> calls)
        {
            RuleViolation v = new RuleViolation();
            v.m_level = level;
            v.m_endpoint = Endpoint;
            v.m_summary = description;
            v.m_violatingCalls.AddRange(calls);
            Violations.Add(v);
        }

        // Helper method to add a violation with a single call
        public void AddViolation(ViolationLevel level, String description, ServiceCallItem call)
        {
            RuleViolation v = new RuleViolation();
            v.m_level = level;
            v.m_endpoint = Endpoint;
            v.m_summary = description;
            v.m_violatingCalls.Add(call);
            Violations.Add(v);
        }

        // Helper method to add a violation with no calls.
        public void AddViolation(ViolationLevel level, String description)
        {
            RuleViolation v = new RuleViolation();
            v.m_level = level;
            v.m_endpoint = Endpoint;
            v.m_summary = description;
            Violations.Add(v);
        }
    }
}
