// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CaptureAnalysisEngine
{
    // TODO: separate out the "Live" implementation stuff from the base rules engine stuff
    public class RulesEngine
    {
        private Dictionary<String, LinkedList<Rule>> m_ruleDefines = new Dictionary<String, LinkedList<Rule>>();
        private readonly List<String> m_ruleDefineOrder = new();
        private Dictionary<EndpointKey, LinkedList<Rule>> m_endpointRuleMap = new Dictionary<EndpointKey, LinkedList<Rule>>();
        private Dictionary<String, ConcurrentBag<RuleResult>> m_results = new Dictionary<string, ConcurrentBag<RuleResult>>();
        private String m_version = String.Empty;
        private String m_ruleFile = String.Empty;

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
            : this(
                isInternal,
                new AnalysisWorkBudget())
        {
        }

        internal RulesEngine(
            bool isInternal,
            AnalysisWorkBudget workBudget)
        {
            ArgumentNullException.ThrowIfNull(
                workBudget);
            IsInternal = isInternal;
            WorkBudget = workBudget;
        }

        internal AnalysisWorkBudget WorkBudget { get; }

        internal CancellationToken CancellationToken { get; private set; }

        // Rules are classified by an endpoint's logical name; each endpoint bucket keeps its own rule instances.
        void MapRules(
            IEnumerable<EndpointKey> endpoints,
            CancellationToken cancellationToken)
        {
            foreach (EndpointKey endpoint in endpoints
                .OrderBy(
                    item => item.LogicalName,
                    StringComparer.Ordinal)
                .ThenBy(item => item.Kind))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rules = new LinkedList<Rule>();

                foreach (string ruleId in
                    m_ruleDefineOrder)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (Rule rule in
                        m_ruleDefines[ruleId])
                    {
                        if (WildcardMatcher.IsMatch(
                                rule.Endpoint,
                                endpoint.LogicalName))
                        {
                            var newRule = rule.Clone();
                            newRule.Endpoint = endpoint.LogicalName;
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
            RunRulesOnData(console, data, CancellationToken.None);
        }

        internal void RunRulesOnData(
            String console,
            ServiceCallData.PerConsoleData data,
            CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;

            // Expand the wildcard (*) endpoint rules out to match the actual endpoints
            MapRules(data.m_servicesHistory.Keys, cancellationToken);

            if (!m_results.ContainsKey(console))
            {
                m_results.Add(console, new ConcurrentBag<RuleResult>());
            }

            var endpointRules = m_endpointRuleMap
                .OrderBy(
                    endpoint => endpoint.Key.LogicalName,
                    StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.Key.Kind)
                .SelectMany(endpoint => endpoint.Value.Select(rule => (Endpoint: endpoint.Key, Rule: rule)))
                .ToList();
            var actionableHosts = data.m_servicesHistory.ToDictionary(
                endpoint => endpoint.Key,
                endpoint => ServiceCallData.GetActionableHost(endpoint.Key, endpoint.Value));

            foreach (var endpointRule in endpointRules.Where(
                item => RetainsWorkBudget(item.Rule)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RunRule(
                    console,
                    data,
                    endpointRule,
                    actionableHosts);
            }

            // Budget-retaining rules have completed in stable ordinal endpoint/rule order. The remaining rules do not
            // charge or retain the shared budget and can still run in parallel.
            Parallel.ForEach(
                endpointRules.Where(item =>
                    !RetainsWorkBudget(item.Rule)),
                new ParallelOptions
                {
                    CancellationToken =
                        cancellationToken,
                },
                endpointRule => RunRule(
                    console,
                    data,
                    endpointRule,
                    actionableHosts));

            return;
        }

        private static bool RetainsWorkBudget(
            Rule rule) =>
            rule is PollingDetectionRule or
                RepeatedCallsRule;

        private void RunRule(
            string console,
            ServiceCallData.PerConsoleData data,
            (
                EndpointKey Endpoint,
                Rule Rule) endpointRule,
            IReadOnlyDictionary<
                EndpointKey,
                string> actionableHosts)
        {
            if (data.m_servicesHistory.TryGetValue(
                    endpointRule.Endpoint,
                    out var history))
            {
                RuleResult result = endpointRule.Rule.Run(
                    this,
                    history,
                    data.m_servicesStats[
                        endpointRule.Endpoint]);
                if (result != null)
                {
                    // Identity and provenance travel with the result for report projection.
                    result.EndpointKey =
                        endpointRule.Endpoint;
                    result.ActionableHost =
                        actionableHosts[
                            endpointRule.Endpoint];
                }
                m_results[console].Add(result);
            }
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
            if (rule.Name == "")
            {
                rule.Name = rule.RuleID + "_" + m_counter;
            }

            // Sort the rules by rule id 
            if (!m_ruleDefines.ContainsKey(rule.RuleID))
            {
                m_ruleDefines.Add(rule.RuleID, new LinkedList<Rule>());
                m_ruleDefineOrder.Add(
                    rule.RuleID);
            }

            m_ruleDefines[rule.RuleID].AddLast(rule);
            return rule.Name;
        }

        public Rule GetRule(String ruleId)
        {
            foreach (var ruleType in m_ruleDefines.Values)
            {
                foreach (var rule in ruleType)
                {
                    if (ruleId == rule.Name)
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
            m_ruleDefineOrder.Clear();
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
            Directory.CreateDirectory(
                Path.GetDirectoryName(filename));
            File.WriteAllLines(
                filename,
                EnumerateServiceCallData(
                    serviceCallData));
        }

        private static IEnumerable<string>
            EnumerateServiceCallData(
                ServiceCallData serviceCallData)
        {
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
                        yield return item.Stringify();
                    }
                }

                var serviceCallStatsKeys = perConsoleData.m_servicesStats.Keys.ToList();
                serviceCallStatsKeys.Sort();
                foreach (var serviceCallStatsKey in serviceCallStatsKeys)
                {
                    var serviceCallStats = perConsoleData.m_servicesStats[serviceCallStatsKey];
                    yield return serviceCallStats.Stringify();
                }
            }
        }

        public void DumpServiceCallItems(string filename, List<ServiceCallItem> serviceCallItems)
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(filename));
            File.WriteAllLines(
                filename,
                EnumerateServiceCallItems(
                    serviceCallItems));
        }

        private static IEnumerable<string>
            EnumerateServiceCallItems(
                IReadOnlyCollection<ServiceCallItem> items)
        {
            yield return
                $"Total of [{items.Count}] service call items.";
            foreach (ServiceCallItem item in items)
            {
                yield return item.Stringify();
            }
        }

        public void DumpResultsToFile(string filename)
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(filename));
            File.WriteAllLines(
                filename,
                EnumerateResults());
        }

        private IEnumerable<string> EnumerateResults()
        {
            yield return $"Devices: {m_results.Count}";
            var deviceKeys = m_results.Keys.ToList();
            deviceKeys.Sort();
            foreach (var deviceKey in deviceKeys)
            {
                yield return $". {deviceKey}";
                yield return
                    $". Rule Results: {m_results[deviceKey].Count}";
                var deviceResults = m_results[deviceKey].ToList();
                deviceResults.Sort((a, b) => (a.RuleName + a.Endpoint).CompareTo(b.RuleName + b.Endpoint));
                foreach (var ruleResult in deviceResults)
                {
                    yield return
                        $"... {ruleResult.RuleName}";
                    yield return
                        $"... {ruleResult.Endpoint}";
                    yield return
                        $"... {ruleResult.RuleDescription}";

                    yield return
                        $"... Violations: {ruleResult.ViolationCount}";
                    foreach (var violation in ruleResult.Violations)
                    {
                        yield return
                            $"..... {violation.m_level}";
                        yield return
                            $"..... {violation.m_endpoint}";
                        yield return
                            violation.m_summary.ToString();
                        yield return
                            $"..... Violating Calls: {violation.m_violatingCalls.Count}";
                        foreach (var violatingCall in violation.m_violatingCalls)
                        {
                            yield return
                                $"....... {violatingCall.Id}";
                            yield return
                                $"....... {violatingCall.Uri}";
                        }
                    }

                    yield return
                        $"... Results: {ruleResult.Results.Count}";
                    var resultKeys = ruleResult.Results.Keys.ToList();
                    resultKeys.Sort();
                    foreach (var resultKey in resultKeys)
                    {
                        object value = ruleResult.Results[resultKey];
                        yield return $"..... {resultKey}";
                        if (value.GetType().IsValueType ||
                            value.GetType().IsPrimitive ||
                            value.GetType() == typeof(string))
                        {
                            yield return $"..... {value}";
                        }
                        else
                        {
                            yield return "..... [OBJECT]";
                        }
                    }
                }
            }
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
