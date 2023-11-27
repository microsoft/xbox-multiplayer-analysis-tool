// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CaptureAnalysisEngine
{
    // TODO: if this is a deprecated rule, it should not have the [Rule] attribute
    [Rule]
    internal class XR049Rule : BaseRule<XR049Rule>
    {
        public static string DisplayName { get { return @"XR049Rule"; } }
        public static string Description { get { return string.Empty; } }

        public XR049Rule() : base()
        {
        }

        public override RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            bool richpresenceFound = false;
            var richPresenceJsonObj = new { activity = new Object() };
            foreach (var call in items)
            {
                var doc = JsonDocument.Parse(call.ReqBody);
                JsonElement activityObject;
                if (doc.RootElement.TryGetProperty(@"activity", out activityObject))
                {
                    richpresenceFound = true;
                    break;
                }
            }

            RuleResult result = InitializeResult(DisplayName, Description);
            if (!richpresenceFound)
            {
                result.Violations.Add(new RuleViolation());
            }
            return result;
        }

        public override void DeserializeJson(JsonElement json)
        {
            // nothing to deserialize for now
        }

        // TODO: ignoring serialization for now
        //public override JObject SerializeJson()
        //{
        //    return null;
        //}
    }
}
