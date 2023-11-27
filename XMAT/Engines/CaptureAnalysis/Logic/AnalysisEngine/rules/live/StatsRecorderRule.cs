// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Text.Json;

namespace CaptureAnalysisEngine
{
    [Rule]
    internal class StatsRecorderRule : BaseRule<StatsRecorderRule>
    {
        public const string StatsDataKey = "Stats";

        public static string DisplayName { get { return @"StatsRecorder"; } }
        public static string Description { get { return string.Empty; } }

        public StatsRecorderRule() : base()
        {
        }

        public override RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RuleResult result = InitializeResult(DisplayName, Description);
            result.Results.Add(StatsDataKey, stats);
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
