// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CaptureAnalysisEngine
{
    [Rule]
    public class SmallBatchDetectionRule : BaseRule<SmallBatchDetectionRule>
    {
        public static string TotalBatchCallsDataKey { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_TOTAL"); } }
        public static string MinUsersAllowedDataKey { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_MIN"); } }
        public static string CallsBelowCountDataKey { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_COUNT"); } }
        public static string PercentBelowCountDataKey { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_PERCENT"); } }

        public static string DisplayName { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_TITLE"); } }
        public static string Description { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_DESC"); } }

        public UInt32 m_minBatchXUIDsPerBatchCall = 0;
        public Dictionary<string, string> m_MatchPatterns = new Dictionary<string, string>();
        public List<Tuple<int, int>> m_patternInstancesFound = new List<Tuple<int, int>>();

        public SmallBatchDetectionRule() : base()
        {
        }

        // TODO: ignoring serialization for now
        //public override JObject SerializeJson()
        //{
        //    var json = new JObject();
        //    json["MinBatchXUIDsPerBatchCall"] = m_minBatchXUIDsPerBatchCall;
        //    return json;
        //}

        public override void DeserializeJson(JsonElement json)
        {
            Utils.SafeAssign(json, @"MinBatchXUIDsPerBatchCall", ref m_minBatchXUIDsPerBatchCall);
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

        public Tuple<int, int> PatternsFoundSumAsTuple()
        {
            int totalPatternInstancesFound = 0;
            int totalLowXUIDPatternsFound = 0;

            foreach (var tuple in m_patternInstancesFound)
            {
                totalPatternInstancesFound += tuple.Item1;
                totalLowXUIDPatternsFound += tuple.Item2;
            }

            return new Tuple<int, int>(totalPatternInstancesFound, totalLowXUIDPatternsFound);
        }

        public override RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RuleResult result = InitializeResult(DisplayName, Description);
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

                int patternInstancesFound = 0;
                int lowXUIDInstancesFound = 0;
                
                // This first section reports on violations which are from batch calls made with not enough XUIDs in the request body
                foreach (ServiceCallItem thisItem in items)
                {
                    Match match = Regex.Match(thisItem.Uri, pattern.Key);
                    if (match.Success)
                    {
                        patternInstancesFound++;
                        try
                        {
                            var doc = JsonDocument.Parse(thisItem.ReqBody);
                            var element = doc.SelectElement(pattern.Value);

                            if (element.HasValue && element.Value.ValueKind == JsonValueKind.Array)
                            {
                                var count = element.Value.EnumerateArray().Count();
                                matchesFoundDict.Add(thisItem, count);
                                if (count < m_minBatchXUIDsPerBatchCall)
                                {
                                    lowXUIDInstancesFound++;
                                    description.Clear();
                                    description.Append(Localization.GetLocalizedString("LTA_SMALL_CALLS_VIOLATION", count));
                                    result.AddViolation(ViolationLevel.Warning, description.ToString(), thisItem);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                }  // finished traversing calls made to endpoint

                m_patternInstancesFound.Add(new Tuple<int, int>(patternInstancesFound, lowXUIDInstancesFound));
            } // end of foreach pattern in patterns

            var finalStats = PatternsFoundSumAsTuple();

            result.Results.Add(TotalBatchCallsDataKey, finalStats.Item1);
            result.Results.Add(MinUsersAllowedDataKey, m_minBatchXUIDsPerBatchCall);
            result.Results.Add(CallsBelowCountDataKey, finalStats.Item2);
            result.Results.Add(PercentBelowCountDataKey,(double)(finalStats.Item2) / finalStats.Item1);

            return result;
        }
    }
}
