// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace CaptureAnalysisEngine
{
    public class TraceAnalyzer
    {
        // TODO: remove at some point if we do not need to save output
        //public String OutputDirectory { get; set; }

        public IEnumerable<String> ConsoleList(ServiceCallData serviceCallData)
        {
            return serviceCallData.m_perConsoleData.Keys;
        }

        // TODO: needed?
        //public String CustomUserAgent { get; set; }

        public TraceAnalyzer()
        {
        }

        public bool DoesDataContainEndpoint(ServiceCallData serviceCallData, string endpoint)
        {
            foreach(var console in serviceCallData.m_perConsoleData.Values)
            {
                if(console.m_servicesHistory.ContainsKey(endpoint))
                {
                    return true;
                }
            }
            return false;
        }

        public void LoadURIMap(StreamReader mapFile)
        {
            m_converter.LoadMap(mapFile);
        }

        public void LoadURIMap(String uriMapFilePath)
        {
            using (StreamReader uriMap = new StreamReader(uriMapFilePath))
            {
                LoadURIMap(uriMap);
            }
        }

        public void LoadRules(StreamReader rules)
        {
            // NOTE: it is expected that the rules are already properly formatted
            // so any exception that results from parsing should bomb
            var rulesDocument = JsonDocument.Parse(rules.BaseStream);
            {
                JsonElement jsonObject = rulesDocument.RootElement;

                // Parse the version
                JsonElement versionObject;
                jsonObject.TryGetProperty(@"Version", out versionObject);

                // Parse the rules from the data
                JsonElement rulesObject;
                if (jsonObject.TryGetProperty(@"Rules", out rulesObject) == true)
                {
                    JsonElement.ArrayEnumerator ruleParameters = rulesObject.EnumerateArray();

                    // Loop over each rule in the array
                    foreach (JsonElement ruleDef in ruleParameters)
                    {
                        // a valid type name is required to process this rule
                        JsonElement ruleTypeObject;
                        if (!ruleDef.TryGetProperty(@"Type", out ruleTypeObject) ||
                            string.IsNullOrEmpty(ruleTypeObject.GetString()))
                        {
                            // TODO: throw an exception?
                            continue;
                        }

                        string ruleTypeName = ruleTypeObject.GetString();

                        // Using C# reflection look up the rule's type
                        // This way we can just make the rules and not worry about adding it to the RuleEngine
                        Type ruleType;
                        if (!m_ruleTypeCache.TryGetValue(ruleTypeName, out ruleType))
                        {
                            ruleType = Utils.GetRuleTypesWithRuleAttribute().FirstOrDefault(
                                type => type.Name.Equals(ruleTypeName));
                            if (ruleType != null)
                            {
                                m_ruleTypeCache.Add(ruleTypeName, ruleType);
                            }
                        }

                        // TODO: revisit custom rules when we decide to support those
                        // If the rule isn't a built in rule, check for a custom rule.

                        // Make sure that we managed to get a valid type for the parsed rule
                        if (ruleType != null)
                        {
                            // Create the rule and cast to Rule
                            Rule rule = Activator.CreateInstance(ruleType) as Rule;

                            // Fill in the data
                            JsonElement nameObject;
                            if (ruleDef.TryGetProperty(@"Name", out nameObject) && !string.IsNullOrEmpty(nameObject.GetString()))
                            {
                                rule.Name = nameObject.GetString();
                            }

                            JsonElement endpointObject;
                            if (ruleDef.TryGetProperty(@"Endpoint", out endpointObject) && !string.IsNullOrEmpty(endpointObject.GetString()))
                            {
                                rule.Endpoint = endpointObject.GetString();
                            }

                            JsonElement propertiesObject;
                            if (ruleDef.TryGetProperty(@"Properties", out propertiesObject))
                            {
                                rule.DeserializeJson(propertiesObject);
                            }

                            m_rules.Add(rule);
                        }
                        // TODO: handle a bad rule somehow
                        //else
                        //{
                        //    throw new Exception("Invalid rule type " + ruleDef["Name"] + " in rule definition file.");
                        //}
                    }
                }
            }
        }

        public void LoadRules(String rulesFilePath)
        {
            using (StreamReader rules = new StreamReader(rulesFilePath))
            {
                LoadRules(rules);
            }
        }

        public void AddRule(Rule r)
        {
            m_rules.Add(r);
        }

        // TODO: needed?
        //public void SaveRules(String rulesFilePath)
        //{
        //    m_rulesEngine.SerializeJson(rulesFilePath);
        //}

        public void AddReports(IEnumerable<Report> reports)
        {
            m_reports.AddRange(reports);
        }

        public Dictionary<string, IEnumerable<ReportDocument>> Run(RulesEngine rulesEngine, ServiceCallData serviceCallData, string reportOutputDirectory)
        {
            Dictionary<string, IEnumerable<ReportDocument>> perConsoleReports = new ();
            foreach (var console in serviceCallData.m_perConsoleData)
            {
                if (console.Value.m_servicesHistory.Count == 0)
                {
                    continue;
                }

                rulesEngine.ClearAllRules();
                rulesEngine.AddRules(m_rules);
                rulesEngine.RunRulesOnData(console.Key, console.Value);

                var reports = RunReports(rulesEngine.GetResults(console.Key), serviceCallData, reportOutputDirectory);

                perConsoleReports.Add(console.Key, reports);
            }
            return perConsoleReports;
        }

        public IEnumerable<ReportDocument> RunReports(IEnumerable<RuleResult> results, ServiceCallData serviceCallData, string reportOutputDirectory)
        {
            ConcurrentBag<ReportDocument> documents = new ();
            Parallel.ForEach(
                m_reports,
                report =>
                {
                    var reportDocument = report.RunReport(
                        results,
                        serviceCallData.m_endpointToService);
                    if (!string.IsNullOrEmpty(reportOutputDirectory))
                    {
                        var jsonText = reportDocument.ToJson();
                        var jsonPath = Path.Combine(reportOutputDirectory, $"{report.GetType().Name}.json");
                        File.WriteAllText(jsonPath, jsonText);
                    }
                    documents.Add(reportDocument);
                });
            return documents;
        }

        public void RunUriConverterOnData(ServiceCallData serviceCallData)
        {
            foreach (var console in serviceCallData.m_perConsoleData.Values)
            {
                foreach (var endpointData in console.m_servicesHistory)
                {
                    var service = m_converter.GetService(endpointData.Key);

                    if (service != null)
                    {
                        foreach (var call in endpointData.Value)
                        {
                            call.m_xsapiMethods = m_converter.GetMethod(call.Uri, call.Method == @"GET");
                        }
                    }
                }
            }

            serviceCallData.m_endpointToService = m_converter.GetServices();
            
        }

        // TODO: needed?
        //public void LoadPlugins(String pluginDir)
        //{
        //    var pluginDirectory = System.IO.Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), pluginDir);
        //    if (Directory.Exists(pluginDirectory))
        //    {
        //        foreach (var plugin in Directory.EnumerateFiles(pluginDirectory, "*.dll"))
        //        {
        //            Assembly.LoadFrom(plugin);
        //        }
        //    }
        //}

        // TODO: needed?
        //public static String CurrentVersion
        //{
        //    get
        //    {
        //        var assembly = Assembly.GetExecutingAssembly();
        //        return FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        //    }
        //}

        private List<Report> m_reports = new List<Report>();
        private List<Rule> m_rules = new ();
        private UriToMethodConverter m_converter = new ();
        private readonly Dictionary<string, Type> m_ruleTypeCache = new ();
    }
}
