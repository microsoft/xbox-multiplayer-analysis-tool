// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using CaptureAnalysisEngine;

namespace XMAT.ApplicationServices
{
    internal static class CaptureFindingDetails
    {
        internal static CaptureDetailTable Project(
            Guid snapshotId, RuleResult rule, IEnumerable<RuleViolation> violations,
            IReadOnlyDictionary<string, long> recordByBreadcrumb)
        {
            var fields = new List<object[]>();
            void Add(string group, long item, string name, object value) => fields.Add([group, item, name, value]);
            Add("rule", 0, "type", rule.RuleType);
            Add("rule", 0, "name", rule.RuleName);
            Add("rule", 0, "description", rule.RuleDescription);
            Add("rule", 0, "endpoint", rule.Endpoint);
            Add("rule", 0, "endpointKind", rule.Identity.Kind.ToString());
            var keys = Keys(rule.RuleType);
            Add("rule", 0, "resultAdapterAvailable", keys != null);
            foreach (var key in keys ?? [])
            {
                if (rule.Results.TryGetValue(key, out var value) &&
                    value is null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                    Add("result", 0, key, value);
            }
            long violationId = 0;
            long callId = 0;
            foreach (var violation in violations)
            {
                Add("violation", violationId, "severity", violation.m_level.ToString());
                Add("violation", violationId, "summary", violation.m_summary);
                foreach (var call in violation.m_violatingCalls)
                {
                    Add("call", callId, "violation", violationId);
                    Add("call", callId, "recordId", call.BreadCrumb != null &&
                        recordByBreadcrumb.TryGetValue(call.BreadCrumb, out var recordId) ? recordId : null);
                    Add("call", callId, "requestNumber", call.Id);
                    Add("call", callId, "synthetic", call.EndpointKind == EndpointKind.SyntheticTelemetry);
                    Add("call", callId, "method", call.Method);
                    Add("call", callId, "uri", call.Uri);
                    Add("call", callId, "requestHeaders", call.ReqHeader);
                    Add("call", callId, "requestBody", call.ReqBody);
                    Add("call", callId, "responseHeaders", call.RspHeader);
                    Add("call", callId, "responseBody", call.RspBody);
                    Add("call", callId, "statusCode", call.HttpStatusCode);
                    Add("call", callId, "durationMilliseconds", call.ElapsedCallTimeMs);
                    Add("call", callId, "requestUtcTicks", call.ReqTimeUTC);
                    Add("call", callId, "console", call.ConsoleIP);
                    Add("call", callId, "xboxUserId", call.XboxUserId);
                    Add("call", callId, "multiplayerCorrelationId", call.MultiplayerCorrelationId);
                    Add("call", callId, "playerSessionId", call.PlayerSessionId);
                    Add("call", callId, "eventName", call.EventName);
                    Add("call", callId, "dimensions", call.Dimensions);
                    Add("call", callId, "measurements", call.Measurements);
                    callId++;
                }
                violationId++;
            }
            return new(snapshotId, ["section", "item", "field", "value"], fields);
        }

        private static string[] Keys(string type) => type switch
        {
            nameof(BatchFrequencyRule) => [BatchFrequencyRule.TotalBatchCallsDataKey, BatchFrequencyRule.AllowedTimeBetweenCallsDataKey,
                BatchFrequencyRule.TimesExceededDataKey, BatchFrequencyRule.PotentialReducedCallCountDataKey],
            nameof(BurstDetectionRule) => [BurstDetectionRule.AvgCallsPerSecDataKey, BurstDetectionRule.StdDeviationDataKey,
                BurstDetectionRule.BurstSizeDataKey, BurstDetectionRule.BurstWindowDataKey, BurstDetectionRule.TotalBurstsDataKey],
            nameof(CallFrequencyRule) => [CallFrequencyRule.TotalCallsDataKey, CallFrequencyRule.TimesSustainedExceededDataKey,
                CallFrequencyRule.TimesBurstExceededDataKey],
            nameof(PollingDetectionRule) => [PollingDetectionRule.PollingSequencesFoundDataKey],
            nameof(RepeatedCallsRule) => [RepeatedCallsRule.TotalCallsDataKey, RepeatedCallsRule.DuplicatesDataKey, RepeatedCallsRule.PercentageDataKey],
            nameof(SmallBatchDetectionRule) => [SmallBatchDetectionRule.TotalBatchCallsDataKey, SmallBatchDetectionRule.MinUsersAllowedDataKey,
                SmallBatchDetectionRule.CallsBelowCountDataKey, SmallBatchDetectionRule.PercentBelowCountDataKey],
            nameof(ThrottledCallsRule) => [ThrottledCallsRule.TotalCallsDataKey, ThrottledCallsRule.ThrottledCallsDataKey, ThrottledCallsRule.PercentageDataKey],
            _ => null
        };
    }
}
