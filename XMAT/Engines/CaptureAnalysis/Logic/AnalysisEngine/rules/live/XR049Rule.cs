// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

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
            RuleResult result = InitializeResult(DisplayName, Description);
            bool richpresenceFound = false;
            foreach (var call in items)
            {
                if (string.IsNullOrWhiteSpace(call.ReqBody))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(call.ReqBody);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty(@"activity", out _))
                    {
                        richpresenceFound = true;
                        break;
                    }
                }
                catch (JsonException exception)
                {
                    result.AddViolation(ViolationLevel.Warning, exception.Message, call);
                }
            }

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
