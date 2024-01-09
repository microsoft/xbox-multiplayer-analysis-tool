// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.IO;

namespace CaptureAnalysisEngine
{
    // TODO: separate out the "Live" implementation stuff from the base rules engine stuff
    public class RulesEngine
    {
        private Dictionary<String, LinkedList<Rule>> m_ruleDefines = new Dictionary<String, LinkedList<Rule>>();
        private Dictionary<String, LinkedList<Rule>> m_endpointRuleMap = new Dictionary<String, LinkedList<Rule>>();
        private Dictionary<String,ConcurrentBag<RuleResult>> m_results = new Dictionary<string, ConcurrentBag<RuleResult>>();
        private String m_version = String.Empty;
        private String m_ruleFile = String.Empty;

        // TODO: move this to a utility
        private static String WildCardToRegular(String value)
        {
            return "^" + Regex.Escape(value).Replace("\\?", ".").Replace("\\*", ".*") + "$";
        }

        public String RuleFile
        {
            get { return m_ruleFile; }
        }

        public String Version
        {
            get { return m_version; }
        }

        private uint m_counter = 0;

        public bool IsInternal { get; }

        public RulesEngine(bool isInternal)
        {
            IsInternal = isInternal;
        }

        void MapRules(IEnumerable<String> endpoints)
        {
            foreach (string endpoint in endpoints)
            {
                var rules = new LinkedList<Rule>();

                foreach (var typeRules in m_ruleDefines)
                {
                    foreach (var rule in typeRules.Value)
                    {
                        var regex = new Regex(WildCardToRegular(rule.Endpoint));
                        if (regex.IsMatch(endpoint))
                        {
                            var newRule = rule.Clone();
                            newRule.Endpoint = endpoint;
                            rules.AddLast(newRule);

                            // We only apply the first rule that matches for each type
                            break;
                        }
                    }
                }

                m_endpointRuleMap.Add(endpoint, rules);
            }
        }

        public void RunRulesOnData(String console, ServiceCallData.PerConsoleData data)
        {
            // Expand the wildcard (*) endpoint rules out to match the actual endpoints
            MapRules(data.m_servicesHistory.Keys);

            if(!m_results.ContainsKey(console))
            {
                m_results.Add(console, new ConcurrentBag<RuleResult>());
            }

            // Now the rules can be run in parallel
            Parallel.ForEach(GetAllRules(), rule => 
            {
                if (data.m_servicesHistory.ContainsKey(rule.Endpoint))
                {
                    m_results[console].Add(rule.Run(this, data.m_servicesHistory[rule.Endpoint], data.m_servicesStats[rule.Endpoint]));
                }
            });

            return;
        }

        public void AddRules(IEnumerable<Rule> rules)
        {
            foreach (var rule in rules)
            {
                AddRule(rule.Clone());
            }
        }

        public String AddRule(Rule rule)
        {
            ++m_counter;
            if(rule.Name == "")
            {
                rule.Name = rule.RuleID + "_" + m_counter;
            }

            // Sort the rules by rule id 
            if (!m_ruleDefines.ContainsKey(rule.RuleID))
            {
                m_ruleDefines.Add(rule.RuleID, new LinkedList<Rule>());
            }

            m_ruleDefines[rule.RuleID].AddLast(rule);
            return rule.Name;
        }

        public Rule GetRule(String ruleId)
        {
            foreach(var ruleType in m_ruleDefines.Values)
            {
                foreach (var rule in ruleType)
                {
                    if(ruleId == rule.Name)
                    {
                        return rule;
                    }
                }
            }
            return null;
        }

        public void RemoveRule(String ruleId)
        {
            foreach (var ruleType in m_ruleDefines.Values)
            {
                foreach (var rule in ruleType)
                {
                    if (ruleId == rule.Name)
                    {
                        ruleType.Remove(rule);
                        return;
                    }
                }
            }
        }

        public void ClearAllRules()
        {
            m_ruleDefines.Clear();
            m_endpointRuleMap.Clear();
        }

        public List<Rule> GetAllRules()
        {
            List<Rule> result = new List<Rule>();
            foreach (var ruleType in m_endpointRuleMap)
            {
                result.AddRange(ruleType.Value);
            }
            return result;
        }

        public List<RuleResult> GetResults(String console)
        {
            return m_results[console].ToList();
        }

        public void DumpServiceCallData(string filename, ServiceCallData serviceCallData)
        {
            List<string> dataLines = new List<string>();

            var perConsoleDataKeys = serviceCallData.m_perConsoleData.Keys.ToList();
            perConsoleDataKeys.Sort();
            foreach (var perConsoleDataKey in perConsoleDataKeys)
            {
                var perConsoleData = serviceCallData.m_perConsoleData[perConsoleDataKey];

                var serviceHistoryKeys = perConsoleData.m_servicesHistory.Keys.ToList();
                serviceHistoryKeys.Sort();
                foreach (var serviceHistoryKey in serviceHistoryKeys)
                {
                    var serviceHistory = perConsoleData.m_servicesHistory[serviceHistoryKey];
                    foreach (var item in serviceHistory)
                    {
                        dataLines.Add(item.Stringify());
                    }
                }

                var serviceCallStatsKeys = perConsoleData.m_servicesStats.Keys.ToList();
                serviceCallStatsKeys.Sort();
                foreach (var serviceCallStatsKey in serviceCallStatsKeys)
                {
                    var serviceCallStats = perConsoleData.m_servicesStats[serviceCallStatsKey];
                    dataLines.Add(serviceCallStats.Stringify());
                }
            }

            string directory = Path.GetDirectoryName(filename);
            Directory.CreateDirectory(Path.GetDirectoryName(filename));
            File.WriteAllLines(filename, dataLines);
        }

        public void DumpServiceCallItems(string filename, List<ServiceCallItem> serviceCallItems)
        {
            List<string> itemLines = new List<string>();

            itemLines.Add($"Total of [{serviceCallItems.Count}] service call items.");

            foreach (var item in serviceCallItems)
            {
                itemLines.Add(item.Stringify());
            }

            string directory = Path.GetDirectoryName(filename);
            Directory.CreateDirectory(Path.GetDirectoryName(filename));
            File.WriteAllLines(filename, itemLines);
        }

        public void DumpResultsToFile(string filename)
        {
            List<string> resultLines = new List<string>();

            resultLines.Add($"Devices: {m_results.Count}");
            var deviceKeys = m_results.Keys.ToList();
            deviceKeys.Sort();
            foreach (var deviceKey in deviceKeys)
            {
                resultLines.Add($". {deviceKey}");
                resultLines.Add($". Rule Results: {m_results[deviceKey].Count}");
                var deviceResults = m_results[deviceKey].ToList();
                deviceResults.Sort((a, b) => (a.RuleName+a.Endpoint).CompareTo(b.RuleName+b.Endpoint));
                foreach (var ruleResult in deviceResults)
                {
                    resultLines.Add($"... {ruleResult.RuleName}");
                    resultLines.Add($"... {ruleResult.Endpoint}");
                    resultLines.Add($"... {ruleResult.RuleDescription}");

                    resultLines.Add($"... Violations: {ruleResult.ViolationCount}");
                    foreach (var violation in ruleResult.Violations)
                    {
                        resultLines.Add($"..... {violation.m_level}");
                        resultLines.Add($"..... {violation.m_endpoint}");
                        resultLines.Add(violation.m_summary.ToString());
                        resultLines.Add($"..... Violating Calls: {violation.m_violatingCalls.Count}");
                        foreach (var violatingCall in violation.m_violatingCalls)
                        {
                            resultLines.Add($"....... {violatingCall.Id}");
                            resultLines.Add($"....... {violatingCall.Uri}");
                        }
                    }

                    resultLines.Add($"... Results: {ruleResult.Results.Count}");
                    var resultKeys = ruleResult.Results.Keys.ToList();
                    resultKeys.Sort();
                    foreach (var resultKey in resultKeys)
                    {
                        object value = ruleResult.Results[resultKey];
                        resultLines.Add($"..... {resultKey}");
                        if (value.GetType().IsValueType ||
                            value.GetType().IsPrimitive ||
                            value.GetType() == typeof(string))
                        {
                            resultLines.Add($"..... {value}");
                        }
                        else
                        {
                            resultLines.Add($"..... [OBJECT]");
                        }
                    }
                }
            }

            string directory = Path.GetDirectoryName(filename);
            Directory.CreateDirectory(Path.GetDirectoryName(filename));
            File.WriteAllLines(filename, resultLines);
        }

        // TODO: ignoring serialization for now
        //public void SerializeJson(String filePath)
        //{
        //    JObject ruleJson = new JObject();
        //    ruleJson["Version"] = m_version;
        //    JArray rules = new JArray();
        //    foreach (var rule in GetAllRules())
        //    {
        //        var ruleObject = new JObject();
        //        ruleObject["Type"] = rule.RuleID;
        //        ruleObject["Name"] = rule.Name;
        //        ruleObject["Endpoint"] = rule.Endpoint;
        //        ruleObject["Properties"] = rule.SerializeJson();
        //        rules.Add(ruleObject);
        //    }
        //
        //    ruleJson["Rules"] = rules;
        //    using (StreamWriter ruleFile = new StreamWriter(filePath))
        //    {
        //        using (JsonTextWriter writer = new JsonTextWriter(ruleFile))
        //        {
        //            writer.Formatting = Formatting.Indented;
        //            ruleJson.WriteTo(writer);
        //        }
        //    }
        //}
    }
}
