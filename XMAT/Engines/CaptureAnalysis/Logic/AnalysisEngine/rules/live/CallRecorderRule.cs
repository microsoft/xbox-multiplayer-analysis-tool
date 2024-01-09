// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CaptureAnalysisEngine
{
    [Rule]
    internal class CallRecorderRule : BaseRule<CallRecorderRule>
    {
        public const string CallsDataKey = "Calls";

        public static string DisplayName { get { return @"CallRecorder"; } }
        public static string Description { get { return string.Empty; } }

        public CallRecorderRule() : base()
        {
        }

        public override RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RuleResult result = InitializeResult(DisplayName, Description);
            result.Results.Add(CallsDataKey, items.AsEnumerable());
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
