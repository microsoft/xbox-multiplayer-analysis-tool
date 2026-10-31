// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XMAT.Tests.Fixtures
{
    internal static class SyntheticAnalysisAssets
    {
        private const string ApiMapFile =
            "XboxLiveTraceAnalyzer.APIMap.csv";
        private const string RulesFile =
            "XboxLiveTraceAnalyzer.Rules.json";

        internal static void CopyShipped(string root)
        {
            string data =
                Path.Combine(
                    root,
                    "Data");
            Directory.CreateDirectory(data);
            foreach (string name in
                new[]
                {
                    ApiMapFile,
                    RulesFile,
                })
            {
                File.Copy(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "Data",
                        name),
                    Path.Combine(
                        data,
                        name));
            }
        }

        internal static void AddHandEditedRules(
            string root)
        {
            AddHandEditedRule(
                root,
                "StatsRecorderRule",
                "HandEditedMissingEndpoint",
                null);
            AddHandEditedRule(
                root,
                "StatsRecorderRule",
                null,
                "*");
            AddHandEditedRule(
                root,
                "UnknownHandEditedRule",
                "HandEditedUnknownType",
                "*");
            AddHandEditedRule(
                root,
                null,
                "HandEditedMissingType",
                "*");
        }

        internal static void AddHandEditedRule(
            string root,
            string type,
            string name,
            string endpoint)
        {
            string path =
                Path.Combine(
                    root,
                    "Data",
                    RulesFile);
            JsonObject document =
                JsonNode.Parse(
                        File.ReadAllText(path))
                    ?.AsObject() ??
                throw new InvalidOperationException(
                    "The shipped rules file is not an object.");
            JsonArray rules =
                document["Rules"]
                    ?.AsArray() ??
                throw new InvalidOperationException(
                    "The shipped rules file has no rules array.");
            var rule =
                new JsonObject();
            if (type != null)
            {
                rule["Type"] = type;
            }
            if (name != null)
            {
                rule["Name"] = name;
            }
            if (endpoint != null)
            {
                rule["Endpoint"] = endpoint;
            }
            rules.Add(rule);
            File.WriteAllText(
                path,
                document.ToJsonString(
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                    }));
        }

        internal static void AddMalformedApiMapLine(
            string root)
        {
            File.AppendAllText(
                Path.Combine(
                    root,
                    "Data",
                    ApiMapFile),
                Environment.NewLine +
                "malformed,line,only");
        }
    }
}
