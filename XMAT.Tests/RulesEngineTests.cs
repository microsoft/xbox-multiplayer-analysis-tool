// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    /// <summary>
    /// Minimal concrete rule for testing the RulesEngine without depending on Localization.
    /// </summary>
    [Rule]
    internal class TestRule : BaseRule<TestRule>
    {
        public int RunCount { get; private set; }

        public TestRule() : base()
        {
        }

        public override void DeserializeJson(JsonElement json) { }

        public override RuleResult Run(RulesEngine engine, IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RunCount++;
            var result = new RuleResult(Name, Endpoint, "Test rule result");
            result.Results["CallCount"] = items.Count();
            return result;
        }
    }

    public class RulesEngineTests
    {
        [Fact]
        public void AddRule_AssignsNameWhenEmpty()
        {
            var engine = new RulesEngine(false);
            var rule = new TestRule { Name = "", Endpoint = "*" };

            string name = engine.AddRule(rule);

            Assert.NotEmpty(name);
            Assert.StartsWith("TestRule_", name);
        }

        [Fact]
        public void AddRule_PreservesExistingName()
        {
            var engine = new RulesEngine(false);
            var rule = new TestRule { Name = "MyCustomRule", Endpoint = "*" };

            string name = engine.AddRule(rule);
            Assert.Equal("MyCustomRule", name);
        }

        [Fact]
        public void GetRule_ReturnsRule_WhenExists()
        {
            var engine = new RulesEngine(false);
            var rule = new TestRule { Name = "FindMe", Endpoint = "*" };
            engine.AddRule(rule);

            var found = engine.GetRule("FindMe");
            Assert.NotNull(found);
            Assert.Equal("FindMe", found.Name);
        }

        [Fact]
        public void GetRule_ReturnsNull_WhenNotFound()
        {
            var engine = new RulesEngine(false);
            Assert.Null(engine.GetRule("NonExistent"));
        }

        [Fact]
        public void RemoveRule_RemovesExistingRule()
        {
            var engine = new RulesEngine(false);
            var rule = new TestRule { Name = "ToRemove", Endpoint = "*" };
            engine.AddRule(rule);

            engine.RemoveRule("ToRemove");
            Assert.Null(engine.GetRule("ToRemove"));
        }

        [Fact]
        public void RemoveRule_DoesNotThrow_WhenRuleNotFound()
        {
            var engine = new RulesEngine(false);
            engine.RemoveRule("NonExistent"); // Should not throw
        }

        [Fact]
        public void ClearAllRules_RemovesAll()
        {
            var engine = new RulesEngine(false);
            engine.AddRule(new TestRule { Name = "Rule1", Endpoint = "*" });
            engine.AddRule(new TestRule { Name = "Rule2", Endpoint = "*" });

            engine.ClearAllRules();

            Assert.Null(engine.GetRule("Rule1"));
            Assert.Null(engine.GetRule("Rule2"));
        }

        [Fact]
        public void AddRules_ClonesRules()
        {
            var engine = new RulesEngine(false);
            var rules = new List<TestRule>
            {
                new TestRule { Name = "R1", Endpoint = "*" },
                new TestRule { Name = "R2", Endpoint = "*" }
            };

            engine.AddRules(rules);

            Assert.NotNull(engine.GetRule("R1"));
            Assert.NotNull(engine.GetRule("R2"));
        }

        [Fact]
        public void IsInternal_ReflectsConstructorParameter()
        {
            var internalEngine = new RulesEngine(true);
            var externalEngine = new RulesEngine(false);

            Assert.True(internalEngine.IsInternal);
            Assert.False(externalEngine.IsInternal);
        }

        [Fact]
        public void RunRulesOnData_ExecutesRules()
        {
            var engine = new RulesEngine(false);
            var rule = new TestRule { Name = "ExecRule", Endpoint = "test.host.com" };
            engine.AddRule(rule);

            var consoleData = new ServiceCallData.PerConsoleData();
            var callItems = new LinkedList<ServiceCallItem>();
            callItems.AddLast(new ServiceCallItem(1)
            {
                Host = "test.host.com",
                Uri = "https://test.host.com/api",
                ConsoleIP = "10.0.0.1",
                ReqTimeUTC = (ulong)DateTime.UtcNow.ToFileTimeUtc(),
                ElapsedCallTimeMs = 100,
                HttpStatusCode = 200
            });
            consoleData.m_servicesHistory["test.host.com"] = callItems;
            consoleData.m_servicesStats["test.host.com"] = new ServiceCallStats(callItems);

            engine.RunRulesOnData("Console1", consoleData);

            var results = engine.GetResults("Console1");
            Assert.NotEmpty(results);
        }
    }
}
