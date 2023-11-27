// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using XMAT;

namespace CaptureAnalysisEngine
{
    [Rule]
    public class CallFrequencyRule : BaseRule<CallFrequencyRule>
    {
        public static string TotalCallsDataKey { get { return Localization.GetLocalizedString("LTA_FREQ_CALLS_TOTAL"); } }
        public static string TimesSustainedExceededDataKey { get { return Localization.GetLocalizedString("LTA_FREQ_CALLS_SUSTAINED"); } }
        public static string TimesBurstExceededDataKey { get { return Localization.GetLocalizedString("LTA_FREQ_CALLS_BURST"); } }

        internal class RateLimits
        {
            public String m_description = string.Empty;
            public List<String> m_applicableSubpaths = new List<String>();

            public UInt32 m_sustainedTimePeriodSeconds = Constants.CallFrequencySustainedTimePeriod;
            public UInt32 m_sustainedCallLimit = Constants.CallFrequencySustainedAllowedCalls;
            public UInt32 m_burstTimePeriodSeconds = Constants.CallFrequencyBurstTimePeriod;
            public UInt32 m_burstCallLimit = Constants.CallFrequencyBurstAllowedCalls;

            public UInt64 m_avgTimeBetweenReqsMs = Constants.CallFrequencyAvgTimeBetweenReq;
            public UInt64 m_avgElapsedCallTimeMs = Constants.CallFrequencyAvgElapsedCallTime;
            public UInt64 m_maxElapsedCallTimeMs = Constants.CallFrequencyMaxElapsedCallTime;
        }

        public static String DisplayName { get { return Localization.GetLocalizedString("LTA_FREQ_CALLS_TITLE"); } }
        public static String Description { get { return Localization.GetLocalizedString("LTA_FREQ_CALLS_DESC"); } }

        // Some service endpoints have multiple sets of limits depending on subpath. For example, presence.xboxlive.com
        // has seperate limits for reading and writing presence.
        internal List<RateLimits> m_rateLimits;

        public ServiceCallStats m_stats;
        public UInt32 m_endpointSustainedViolations = 0;
        public UInt32 m_endpointBurstViolations = 0;

        public CallFrequencyRule() : base()
        {
            m_rateLimits = new List<RateLimits>();
        }

        override public RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RuleResult result = InitializeResult(DisplayName, Description);

            m_stats = stats;
            m_endpointSustainedViolations = 0;
            m_endpointBurstViolations = 0;

            // For set of limits, look through items to determine where excess calls occurred
            foreach (var limits in m_rateLimits)
            {
                // Filter the full list of service calls to those which apply to this set of limits
                List<ServiceCallItem> applicableCalls = items.Where(serviceCall => 
                {
                    foreach (var subpath in limits.m_applicableSubpaths)
                    {
                        var subpathRegex = new Regex("^" + Regex.Escape(subpath).Replace("\\?", ".").Replace("\\*", ".*") + "$");
                        if (subpathRegex.IsMatch(new Uri(serviceCall.Uri).AbsolutePath))
                        {
                            return true;
                        }
                    }
                    return false;
                }).ToList();

                var sustainedExcessCallsPerWindow = Utils.GetExcessCallsForTimeWindow(applicableCalls, limits.m_sustainedTimePeriodSeconds * 1000, limits.m_sustainedCallLimit);
                var burstExcessCallsPerWindow = Utils.GetExcessCallsForTimeWindow(applicableCalls, limits.m_burstTimePeriodSeconds * 1000, limits.m_burstCallLimit);

                foreach (var excessCalls in sustainedExcessCallsPerWindow)
                {
                    if (excessCalls.Count >= limits.m_sustainedCallLimit * 10)
                    {
                        var desc = Localization.GetLocalizedString("LTA_FREQ_CALLS_VIOLATION_EXCEEDED", limits.m_description, limits.m_sustainedCallLimit * 10, excessCalls.Count, limits.m_sustainedTimePeriodSeconds);
                        result.AddViolation(ViolationLevel.Error, desc, excessCalls);
                    }
                    else
                    {
                        var desc = Localization.GetLocalizedString("LTA_FREQ_CALLS_VIOLATION_SUSTAINED", limits.m_description, limits.m_sustainedCallLimit, excessCalls.Count, limits.m_sustainedTimePeriodSeconds);
                        result.AddViolation(ViolationLevel.Warning, desc, excessCalls);
                    }
                    m_endpointSustainedViolations++;
                }

                foreach (var excessCalls in burstExcessCallsPerWindow)
                {
                    var desc = Localization.GetLocalizedString("LTA_FREQ_CALLS_VIOLATION_BURST", limits.m_description, limits.m_burstCallLimit, excessCalls.Count, limits.m_burstTimePeriodSeconds);
                    result.AddViolation(ViolationLevel.Warning, desc, excessCalls);
                    m_endpointBurstViolations++;
                }

                // The following is information that would only be useful for internal purposes.
                if (engine.IsInternal)
                {
                    UInt64 avgTimeBetweenReqsMs = stats.m_avgTimeBetweenReqsMs;
                    UInt64 avgElapsedCallTimeMs = stats.m_avgElapsedCallTimeMs;
                    UInt64 maxElapsedCallTimeMs = stats.m_maxElapsedCallTimeMs;

                    if (avgTimeBetweenReqsMs > 0 && avgTimeBetweenReqsMs < limits.m_avgTimeBetweenReqsMs)
                    {
                        result.AddViolation(ViolationLevel.Warning, Localization.GetLocalizedString("LTA_FREQ_CALLS_VIOLATION_SHORT", avgTimeBetweenReqsMs));
                    }

                    if (avgElapsedCallTimeMs > 0 && avgElapsedCallTimeMs > limits.m_avgElapsedCallTimeMs)
                    {
                        result.AddViolation(ViolationLevel.Warning, Localization.GetLocalizedString("LTA_FREQ_CALLS_VIOLATION_LONG", avgElapsedCallTimeMs));
                    }

                    if (maxElapsedCallTimeMs > 0 && maxElapsedCallTimeMs > limits.m_maxElapsedCallTimeMs)
                    {
                        result.AddViolation(ViolationLevel.Warning, Localization.GetLocalizedString("LTA_FREQ_CALLS_VIOLATION_MAX", maxElapsedCallTimeMs));
                    }
                }
            }

            result.Results.Add(TotalCallsDataKey, m_stats == null ? 0 : m_stats.m_numCalls);
            result.Results.Add(TimesSustainedExceededDataKey, m_endpointSustainedViolations);
            result.Results.Add(TimesBurstExceededDataKey, m_endpointBurstViolations);

            return result;
        }

        // TODO: ignoring serialization for now
        //public override Newtonsoft.Json.Linq.JObject SerializeJson()
        //{
        //    var limitsArray = new JArray();
        //    foreach (var limits in m_rateLimits)
        //    {
        //        var limitsJson = new JObject();
        //        var subpathsArray = new JArray();
        //        foreach(var subpath in limits.m_applicableSubpaths)
        //        {
        //            subpathsArray.Add(subpath);
        //        }
        //        limitsJson["Description"] = limits.m_description;
        //        limitsJson["Subpaths"] = subpathsArray;
        //        limitsJson["SustainedCallPeriod"] = limits.m_sustainedTimePeriodSeconds;
        //        limitsJson["SustainedCallLimit"] = limits.m_sustainedCallLimit;
        //        limitsJson["BurstCallPeriod"] = limits.m_burstTimePeriodSeconds;
        //        limitsJson["BurstCallLimit"] = limits.m_burstCallLimit;
        //        limitsJson["AvgTimeBetweenReqsMs"] = limits.m_avgTimeBetweenReqsMs;
        //        limitsJson["AvgElapsedCallTimeMs"] = limits.m_avgElapsedCallTimeMs;
        //        limitsJson["MaxElapsedCallTimeMs"] = limits.m_maxElapsedCallTimeMs;
        //        limitsArray.Add(limitsJson);
        //    }
        //    var json = new JObject();
        //    json["Limits"] = limitsArray;
        //    return json;
        //}

        public override void DeserializeJson(JsonElement json)
        {
            JsonElement limitsObject;
            if (json.TryGetProperty(@"Limits", out limitsObject))
            {
                JsonElement.ArrayEnumerator limitsArray = limitsObject.EnumerateArray();
                foreach (JsonElement limitObject in limitsArray)
                {
                    var limits = new RateLimits { m_description = Endpoint };

                    // Check which subpaths these rate limits apply to
                    JsonElement subpathsObject;
                    if (limitObject.TryGetProperty(@"Subpaths", out subpathsObject))
                    {
                        JsonElement.ArrayEnumerator subpathArray = subpathsObject.EnumerateArray();
                        foreach (var subpathToken in subpathArray)
                        {
                            limits.m_applicableSubpaths.Add(subpathToken.GetString());
                        }
                    }
                    else
                    {
                        // If subpaths field isn't specified, apply the limits to all calls to the endpoint
                        limits.m_applicableSubpaths.Add("*");
                    }

                    Utils.SafeAssign(limitObject, @"Description", ref limits.m_description);
                    Utils.SafeAssign(limitObject, @"SustainedCallPeriod", ref limits.m_sustainedTimePeriodSeconds);
                    Utils.SafeAssign(limitObject, @"SustainedCallLimit", ref limits.m_sustainedCallLimit);
                    Utils.SafeAssign(limitObject, @"BurstCallPeriod", ref limits.m_burstTimePeriodSeconds);
                    Utils.SafeAssign(limitObject, @"BurstCallLimit", ref limits.m_burstCallLimit);
                    Utils.SafeAssign(limitObject, @"AvgTimeBetweenReqsMs", ref limits.m_avgTimeBetweenReqsMs);
                    Utils.SafeAssign(limitObject, @"AvgElapsedCallTimeMs", ref limits.m_avgElapsedCallTimeMs);
                    Utils.SafeAssign(limitObject, @"MaxElapsedCallTimeMs", ref limits.m_maxElapsedCallTimeMs);

                    m_rateLimits.Add(limits);
                }
            }
        }

        private class Constants
        {
            public const UInt32 CallFrequencySustainedTimePeriod = 300;
            public const UInt32 CallFrequencyBurstTimePeriod = 15;
            public const UInt32 CallFrequencySustainedAllowedCalls = 30;
            public const UInt32 CallFrequencyBurstAllowedCalls = 10;
            public const UInt64 CallFrequencyAvgTimeBetweenReq = 200;
            public const UInt64 CallFrequencyAvgElapsedCallTime = 3000;
            public const UInt64 CallFrequencyMaxElapsedCallTime = 500;
        }
    }
}
