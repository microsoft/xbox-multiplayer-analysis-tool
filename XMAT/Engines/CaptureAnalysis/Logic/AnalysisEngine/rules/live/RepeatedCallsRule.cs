// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using XMAT;

namespace CaptureAnalysisEngine
{
    [Rule]
    public class RepeatedCallsRule : BaseRule<RepeatedCallsRule>
    {
        public static String TotalCallsDataKey { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DATA_TOTAL"); } }
        public static String DuplicatesDataKey { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DATA_DUPS"); } }
        public static String PercentageDataKey { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DATA_PERCENT"); } }

        public static String DisplayName { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_TITLE"); } }
        public static String Description { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DESC"); } }

        public UInt32 m_minAllowedRepeatIntervalMs = 2000;

        public Int32 m_totalCallsChecked = 0;
        public Int32 m_numberOfRepeats = 0;

        public RepeatedCallsRule() : base()
        {
        }

        override public RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RuleResult result = InitializeResult(DisplayName, Description);
            if (items.Count() == 0)
            {
                return result;
            }

            //check invalid log versions (TODO: does this matter?)
            //if (items.Count(item => item.m_logVersion == Constants.Version1509) > 0)
            //{
            //    result.AddViolation(ViolationLevel.Warning, "Data version does not support this rule. You need an updated Xbox Live SDK to support this rule");
            //    return result;
            //}

            StringBuilder description = new StringBuilder();

            List<ServiceCallItem> repeats = new List<ServiceCallItem>();

            m_totalCallsChecked = items.Count();

            foreach (ServiceCallItem thisItem in items.Where(item => item.IsShoulderTap == false))
            {
                if (repeats.Contains(thisItem))
                {
                    continue;
                }

                var timeWindow = from item in items.Where(item => item.IsShoulderTap == false)
                                 where (item.ReqTimeUTC > thisItem.ReqTimeUTC && ((item.ReqTimeUTC - thisItem.ReqTimeUTC) / TimeSpan.TicksPerMillisecond) < m_minAllowedRepeatIntervalMs)
                                 select item;

                List<ServiceCallItem> repeatedCalls = new List<ServiceCallItem>();

                repeatedCalls.Add(thisItem);

                foreach (var call in timeWindow)
                {
                    if (thisItem.ReqBodyHash == call.ReqBodyHash && thisItem.Uri == call.Uri)
                    {
                        repeatedCalls.Add(call);
                    }
                }

                if (repeatedCalls.Count > 1)
                {
                    description.Clear();
                    description.AppendFormat(Localization.GetLocalizedString("LTA_REPEATED_CALLS_VIOLATION", repeatedCalls.Count));

                    result.AddViolation(ViolationLevel.Warning, description.ToString(), repeatedCalls);

                    repeats.AddRange(repeatedCalls);
                }
            }

            m_numberOfRepeats = repeats.Count;

            result.Results.Add(TotalCallsDataKey, m_totalCallsChecked);
            result.Results.Add(DuplicatesDataKey, m_numberOfRepeats);
            result.Results.Add(PercentageDataKey, ((double)m_numberOfRepeats) / m_totalCallsChecked);

            return result;
        }

        // TODO: ignoring serialization for now
        //public override Newtonsoft.Json.Linq.JObject SerializeJson()
        //{
        //    var json = new Newtonsoft.Json.Linq.JObject();
        //    json["MinAllowedRepeatIntervalMs"] = m_minAllowedRepeatIntervalMs;
        //    return json;
        //}

        public override void DeserializeJson(JsonElement json)
        {
            Utils.SafeAssign(json, @"MinAllowedRepeatIntervalMs", ref m_minAllowedRepeatIntervalMs);
        }
    }
}
