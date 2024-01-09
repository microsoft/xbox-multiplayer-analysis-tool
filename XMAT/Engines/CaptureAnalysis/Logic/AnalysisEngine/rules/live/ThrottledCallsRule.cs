// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using XMAT;

namespace CaptureAnalysisEngine
{
    [Rule]
    public class ThrottledCallsRule : BaseRule<ThrottledCallsRule>
    {
        public static string TotalCallsDataKey { get { return Localization.GetLocalizedString("LTA_THROTTLE_CALLS_TOTAL"); } }
        public static string ThrottledCallsDataKey { get { return Localization.GetLocalizedString("LTA_THROTTLE_CALLS_THROTTLED"); } }
        public static string PercentageDataKey { get { return Localization.GetLocalizedString("LTA_THROTTLE_CALLS_PERCENT"); } }

        public static String DisplayName { get { return Localization.GetLocalizedString("LTA_THROTTLE_CALLS_TITLE"); } }
        public static String Description { get { return Localization.GetLocalizedString("LTA_THROTTLE_CALLS_DESC"); } }

        public ThrottledCallsRule() : base()
        {
        }

        public Int32 ThrottledCallCount
        {
            get { return m_throttledCallsCount; }
        }

        public Int32 TotalCalls
        {
            get { return m_totalCalls; }
        }

        public override void DeserializeJson(JsonElement json)
        {
            // nothing to deserialize for now
        }

        // TODO: ignoring serialization for now
        //public override Newtonsoft.Json.Linq.JObject SerializeJson()
        //{
        //    return new Newtonsoft.Json.Linq.JObject();
        //}

        public override RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RuleResult result = InitializeResult(DisplayName, Description);

            m_totalCalls = items.Count();
            m_throttledCallsCount = items.Where(call => call.HttpStatusCode == 429).Count();
            
            // We need to search over all of the calls to the endpoint
            for(int i = 0; i < m_totalCalls;)
            {
                var call = items.ElementAt(i);

                // If its not a throttled call, move to the next call
                if(call.HttpStatusCode != 429)
                {
                    ++i;
                    continue;
                }

                // If it is throttled, start a list
                List<ServiceCallItem> throttledCallSet = new List<ServiceCallItem>();
                throttledCallSet.Add(items.ElementAt(i));
                var throttledCall = throttledCallSet.First();
                string throttleGuidline = string.Empty;

                try
                {
                    if (!string.IsNullOrEmpty(throttledCall.RspBody))
                    {
                        var doc = JsonDocument.Parse(throttledCall.ReqBody);
                        var rootElement = doc.RootElement;
                        JsonElement maxRequestsObject;
                        JsonElement periodInSecondsObject;
                        if (rootElement.TryGetProperty(@"maxRequests", out maxRequestsObject) &&
                            rootElement.TryGetProperty(@"periodInSeconds", out periodInSecondsObject))
                        {
                            throttleGuidline = Localization.GetLocalizedString("LTA_THROTTLE_CALLS_GUIDELINE", maxRequestsObject, periodInSecondsObject);
                        }
                    }
                }
                catch (Exception)
                {
                }
                
                // If there are 2 or more throttled calls in a row the title is not properly handling the response
                while (++i < m_throttledCallsCount)
                {
                    var nextCall = items.ElementAt(i);

                    if (call.HttpStatusCode != 429)
                    {
                        ++i;
                        break;
                    }
                    else
                    {
                        throttledCallSet.Add(call);
                    }
                }

                // One call is a warning as we expect that they back off after getting the 429 response
                if(throttledCallSet.Count == 1)
                {
                    result.AddViolation(ViolationLevel.Warning, Localization.GetLocalizedString("LTA_THROTTLE_CALLS_VIOLATION_SINGLE") + throttleGuidline, throttledCall);
                }
                // More that one in a row means that the title didn't handle the 429 and we want them to fix that.
                else
                {
                    result.AddViolation(ViolationLevel.Warning, Localization.GetLocalizedString("LTA_THROTTLE_CALLS_VIOLATION_MULTI") + throttleGuidline, throttledCallSet);
                }

                throttledCallSet.Clear();
            }

             
            result.Results.Add(TotalCallsDataKey, TotalCalls);
            result.Results.Add(ThrottledCallsDataKey, ThrottledCallCount);
            result.Results.Add(PercentageDataKey, ((double)ThrottledCallCount) / TotalCalls);

            return result;
        }

        private Int32 m_throttledCallsCount = 0;
        private Int32 m_totalCalls = 0;
    }
}
