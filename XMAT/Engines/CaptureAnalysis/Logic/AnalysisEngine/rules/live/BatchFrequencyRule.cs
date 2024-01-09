// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XMAT;

namespace CaptureAnalysisEngine
{
    [Rule]
    public class BatchFrequencyRule : BaseRule<BatchFrequencyRule> 
    {
        public static String TotalBatchCallsDataKey { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_DATA_TOTAL"); } }
        public static String AllowedTimeBetweenCallsDataKey { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_DATA_ALLOWED"); } }
        public static String TimesExceededDataKey { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_DATA_EXCEEDED"); } }
        public static String PotentialReducedCallCountDataKey { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_DATA_REDUCE"); } }

        public static String DisplayName { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_TITLE"); } }
        public static String Description { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_DESC"); } }

        public UInt32 m_BatchSetDetectionWindowsMs = Constants.BatchDetectionWindowPeriod;
        public Dictionary<string, string> m_MatchPatterns = new Dictionary<string, string>();
        public UInt32 m_totalBatchCallCount = 0;

        public BatchFrequencyRule() : base()
        {
        }

        // TODO: ignoring serialization for now
        //public override JObject SerializeJson()
        //{
        //    var json = new JObject();
        //    json["BatchSetDetectionWindowMs"] = m_BatchSetDetectionWindowsMs;
        //    return json;
        //}

        public override void DeserializeJson(JsonElement json)
        {
            Utils.SafeAssign(json, @"BatchSetDetectionWindowMs", ref m_BatchSetDetectionWindowsMs);
            JsonElement matchPatternsObject;
            if (json.TryGetProperty(@"MatchPatterns", out matchPatternsObject))
            {
                JsonElement.ArrayEnumerator patterns = matchPatternsObject.EnumerateArray();
                string batchUri = string.Empty, xuidListClass = string.Empty;
                foreach (JsonElement pattern in patterns)
                {
                    Utils.SafeAssign(pattern, @"BatchURI", ref batchUri);
                    Utils.SafeAssign(pattern, @"XUIDListClass", ref xuidListClass);
                    m_MatchPatterns.Add(batchUri, xuidListClass);
                }
            }
        }

        public override RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RuleResult result = InitializeResult(DisplayName,Description);
            //check invalid log versions (TODO: does this matter?)
            //if (items.Count(item => item.m_logVersion == Constants.Version1509) > 0)
            //{
            //    result.AddViolation(ViolationLevel.Warning, "Data version does not support this rule. You need an updated Xbox Live SDK to support this rule");
            //    return result;
            //}

            StringBuilder description = new StringBuilder();

            // Traverse through each pattern set found in rule parameter
            foreach (var pattern in m_MatchPatterns)
            {
                Dictionary<ServiceCallItem, int> matchesFoundDict = new Dictionary<ServiceCallItem, int>();
                
                foreach (ServiceCallItem thisItem in items)
                {
                    Match match = Regex.Match(thisItem.Uri, pattern.Key);
                    if (match.Success)
                    {
                        try
                        {
                            var doc = JsonDocument.Parse(thisItem.ReqBody);
                            var element = doc.SelectElement(pattern.Value);

                            if (element.HasValue && element.Value.ValueKind == JsonValueKind.Array)
                            {
                                var count = element.Value.EnumerateArray().Count();
                                matchesFoundDict.Add(thisItem, count);
                            }
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                }  // finished traversing calls made to endpoint

                m_totalBatchCallCount = (UInt32)matchesFoundDict.Count;

                // For all the matches found, report on batch sets which happened within a specific time window
                int numDictItems = matchesFoundDict.Count;
                if (numDictItems >= 2)
                {
                    int startWindowIdx = 0;
                    List<ServiceCallItem> callsWithinWindow = new List<ServiceCallItem>();
                    int totalXUIDsForWindow = 0; 
                    totalXUIDsForWindow += matchesFoundDict.Values.ElementAt(startWindowIdx);
                    callsWithinWindow.Add(matchesFoundDict.Keys.ElementAt(startWindowIdx));
                    for (int endWindowIdx = 1; endWindowIdx < matchesFoundDict.Count(); ++endWindowIdx)
                    {
                        UInt64 timeElapsed = (UInt64)Math.Abs((float)
                            (matchesFoundDict.Keys.ElementAt(endWindowIdx).ReqTimeUTC - matchesFoundDict.Keys.ElementAt(startWindowIdx).ReqTimeUTC) / TimeSpan.TicksPerMillisecond);
                        if (timeElapsed <= m_BatchSetDetectionWindowsMs)
                        {
                            callsWithinWindow.Add(matchesFoundDict.Keys.ElementAt(endWindowIdx));
                            totalXUIDsForWindow += matchesFoundDict.Values.ElementAt(endWindowIdx);
                        }
                        else //exceeded window
                        {
                            if (callsWithinWindow.Count >= 2)
                            {
                                result.AddViolation(ViolationLevel.Warning, Localization.GetLocalizedString("LTA_BATCH_CALLS_VIOLATION", callsWithinWindow.Count, m_BatchSetDetectionWindowsMs, totalXUIDsForWindow), callsWithinWindow);
                            }

                            startWindowIdx = endWindowIdx; // shift window
                            //reset figures
                            totalXUIDsForWindow = 0;
                            callsWithinWindow.Clear();
                        }
                    }
                    // in case we exited the last for early because we never exceeded the time window, then call
                    // the following function once more to handle any dangling reports.
                    if (callsWithinWindow.Count >= 2)
                    {
                        result.AddViolation(ViolationLevel.Warning, Localization.GetLocalizedString("LTA_BATCH_CALLS_VIOLATION", callsWithinWindow.Count, m_BatchSetDetectionWindowsMs, totalXUIDsForWindow), callsWithinWindow);
                    }
                }
            } // end of foreach pattern in patterns

            result.Results.Add(TotalBatchCallsDataKey, m_totalBatchCallCount);
            result.Results.Add(AllowedTimeBetweenCallsDataKey, m_BatchSetDetectionWindowsMs);
            result.Results.Add(TimesExceededDataKey, result.ViolationCount);
            result.Results.Add(PotentialReducedCallCountDataKey, m_totalBatchCallCount - result.ViolationCount);

            return result;
        }

        private class Constants
        {
            public const UInt32 BatchDetectionWindowPeriod = 2000;
        }
    }
}
