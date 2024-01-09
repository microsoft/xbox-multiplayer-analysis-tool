// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using XMAT;

namespace CaptureAnalysisEngine
{
    [Rule]
    public class BurstDetectionRule : BaseRule<BurstDetectionRule>
    {
        public static String AvgCallsPerSecDataKey { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_AVERAGE"); } }
        public static String StdDeviationDataKey { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_DEVIATION"); } }
        public static String BurstSizeDataKey { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_SIZE"); } }
        public static String BurstWindowDataKey { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_WINDOW"); } }
        public static String TotalBurstsDataKey { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_TOTAL"); } }

        public static String DisplayName { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_TITLE"); } }
        public static String Description { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_DESC"); } }

        public UInt32 m_burstDetectionWindowMs = 0;
        public UInt32 m_burstSizeToDetect = 0;

        public double m_avgCallsPerSecond = 0;
        public double m_callStdDeviationPerSecond = 0;

        public BurstDetectionRule() : base()
        {
        }

        override public RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RuleResult result = InitializeResult(DisplayName, Description);

            StringBuilder description = new StringBuilder();

            m_avgCallsPerSecond = 1000.0 * 1.0 / stats.m_avgTimeBetweenReqsMs;
            m_callStdDeviationPerSecond = 1000.0 * 1.0 / Math.Sqrt(stats.m_varTimeBetweenReqsMs);

            const float factor = 2.0f;
            UInt32 burstSize = (UInt32)Math.Ceiling(m_avgCallsPerSecond) + (UInt32)Math.Ceiling(factor * m_callStdDeviationPerSecond);

            var allBurstsDetected = Utils.GetExcessCallsForTimeWindow(items, m_burstDetectionWindowMs, burstSize);
            // burst - is a list of calls (or just one call) that has exceeded the average requests per second rate
            foreach (var burst in allBurstsDetected)
            {
                if (burst.Count >= m_burstSizeToDetect)
                {
                    description.Clear();
                    description.Append(Localization.GetLocalizedString("LTA_BURST_CALLS_VIOLATION", burst.Count));

                    result.AddViolation(ViolationLevel.Warning, description.ToString(), burst);
                }
            }
            
            if (double.IsInfinity(m_avgCallsPerSecond))
            {
                result.Results.Add(AvgCallsPerSecDataKey, Localization.GetLocalizedString("LTA_BURST_CALLS_NOTAPPLICABLE"));
                result.Results.Add(StdDeviationDataKey, Localization.GetLocalizedString("LTA_BURST_CALLS_NOTAPPLICABLE"));
                result.Results.Add(BurstSizeDataKey, m_burstSizeToDetect);
                result.Results.Add(BurstWindowDataKey, m_burstDetectionWindowMs);
                result.Results.Add(TotalBurstsDataKey, result.ViolationCount);
            }
            else
            {
                result.Results.Add(AvgCallsPerSecDataKey, m_avgCallsPerSecond);
                result.Results.Add(StdDeviationDataKey, m_callStdDeviationPerSecond);
                result.Results.Add(BurstSizeDataKey, m_burstSizeToDetect);
                result.Results.Add(BurstWindowDataKey, m_burstDetectionWindowMs);
                result.Results.Add(TotalBurstsDataKey, result.ViolationCount);
            }

            return result;
        }

        // TODO: ignoring serialization for now
        //public override Newtonsoft.Json.Linq.JObject SerializeJson()
        //{
        //    var json = new Newtonsoft.Json.Linq.JObject();
        //    json["BurstDetectionWindowMs"] = m_burstDetectionWindowMs;
        //    json["BurstSizeToDetect"] = m_burstSizeToDetect;
        //    return json;
        //}

        public override void DeserializeJson(JsonElement json)
        {
            Utils.SafeAssign(json, @"BurstDetectionWindowMs", ref m_burstDetectionWindowMs);
            Utils.SafeAssign(json, @"BurstSizeToDetect", ref m_burstSizeToDetect);
        }
    }
}
