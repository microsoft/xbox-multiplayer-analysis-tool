// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using XMAT.DesignSystem;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests.DesignSystem
{
    // The design system's rules (XMAT.DesignSystem/RuleCatalog.cs) hold together: each has an ID that names its page and
    // says what and why, each Must rule names the tests that check it and those tests cite its ID, and each page shows
    // its rules, with examples of following and breaking them.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class RuleCatalogTests
    {
        private static readonly Regex IdPattern = new("^(?<prefix>[A-Z]+)-[1-9][0-9]*$", RegexOptions.CultureInvariant);

        private readonly CaptureUiApplicationFixture _application;

        public RuleCatalogTests(CaptureUiApplicationFixture application) => _application = application;

        // An ID is the page's prefix and a number, so a failure that cites it points at its page.
        [Fact]
        public void Rules_HaveIdsThatNameTheirPage_AndSayWhatAndWhy()
        {
            Assert.NotEmpty(RuleCatalog.Rules);
            string[] repeated = RuleCatalog.Rules.GroupBy(rule => rule.Id).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToArray();
            Assert.True(repeated.Length == 0, $"More than one rule has the ID {string.Join(", ", repeated)}.");
            foreach (DesignRule rule in RuleCatalog.Rules)
            {
                Assert.True(IdPattern.IsMatch(rule.Id), $"{rule.Id} isn't a page's prefix and a number, such as COLOR-3.");
                Assert.False(string.IsNullOrWhiteSpace(rule.Text), $"{rule.Id} doesn't say what the rule is.");
                Assert.False(string.IsNullOrWhiteSpace(rule.Why), $"{rule.Id} doesn't say why.");
            }

            foreach (IGrouping<string, DesignRule> page in RuleCatalog.Rules.GroupBy(rule => rule.Page))
            {
                string[] prefixes = page.Select(rule => IdPattern.Match(rule.Id).Groups["prefix"].Value).Distinct().ToArray();
                Assert.True(prefixes.Length == 1, $"The {page.Key} page's rules have more than one prefix: {string.Join(", ", prefixes)}.");
                Assert.DoesNotContain(RuleCatalog.Rules, rule => rule.Page != page.Key && rule.Id.StartsWith(prefixes[0] + "-", StringComparison.Ordinal));
            }
        }

        // A Must rule holds because a test fails when the product breaks it. That test exists, and its failures start
        // with the rule's ID, so its source cites it.
        [Fact]
        public void MustRules_NameTheTestsThatCheckThem_AndThoseTestsCiteTheirIds()
        {
            Type[] tests = typeof(RuleCatalogTests).Assembly.GetTypes();
            string testsRoot = Path.Combine(RepositoryPaths.Root, "XMAT.Tests");
            foreach (DesignRule rule in RuleCatalog.Rules)
            {
                Assert.True(rule.Level != RuleLevel.Must || rule.CheckedBy.Count > 0,
                    $"{rule.Id} is a Must rule, so it names the tests that check it.");
                foreach (string check in rule.CheckedBy)
                {
                    string[] parts = check.Split('.');
                    Assert.True(parts.Length == 2, $"{rule.Id}: {check} isn't Class.Method.");
                    Type[] classes = tests.Where(type => type.Name == parts[0]).ToArray();
                    Assert.True(classes.Length == 1, $"{rule.Id}: XMAT.Tests has no single class {parts[0]}.");
                    MethodInfo method = classes[0].GetMethod(parts[1], BindingFlags.Public | BindingFlags.Instance);
                    Assert.True(method?.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0,
                        $"{rule.Id}: {check} isn't a test.");

                    string[] sources = Directory.EnumerateFiles(testsRoot, parts[0] + ".cs", SearchOption.AllDirectories)
                        .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                        .ToArray();
                    Assert.True(sources.Length == 1, $"{rule.Id}: can't find {parts[0]}.cs in XMAT.Tests.");
                    Assert.True(Regex.IsMatch(File.ReadAllText(sources[0]), $@"\b{Regex.Escape(rule.Id)}\b"),
                        $"{rule.Id}: {parts[0]}'s failures don't cite {rule.Id}. Start their messages with it.");
                }
            }
        }

        // docs/design-system.md tags each rule it states with its ID, so people reading the repository find every rule.
        [Fact]
        public void EveryRule_IsInTheDocs()
        {
            string docs = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "design-system.md"));
            string[] missing = RuleCatalog.Rules.Select(rule => rule.Id)
                .Where(id => !docs.Contains($"**{id}**", StringComparison.Ordinal))
                .ToArray();
            Assert.True(missing.Length == 0,
                $"docs/design-system.md doesn't state {string.Join(", ", missing)}. Add each, starting with its ID in bold.");
        }

        // Each page shows its rules, in the catalog's order, and a rule's examples include following it and breaking it.
        [Fact]
        public void EveryRule_IsOnItsPage_WithExamplesOfFollowingAndBreakingIt()
        {
            _application.Run(_ =>
            {
                DesignSystemWindow window = DesignSystemWindowTests.Open("Light");
                try
                {
                    var titles = new HashSet<string>(StringComparer.Ordinal);
                    for (int index = 0; index < window.Stories.Count; index++)
                    {
                        window.ShowPage(index);
                        WpfTestHost.Pump();
                        Story story = window.ShownStory;
                        titles.Add(story.Title);
                        List<StoryRule> shown = DesignSystemWindowTests.Descendants(story).OfType<StoryRule>().ToList();
                        Assert.Equal(RuleCatalog.OnPage(story.Title).Select(rule => rule.Id), shown.Select(rule => rule.RuleId));
                        foreach (StoryRule rule in shown)
                        {
                            Assert.NotNull(rule.Rule);
                            List<RuleExample> examples = rule.Items.OfType<RuleExample>().ToList();
                            Assert.Equal(rule.Items.Count, examples.Count);
                            if (examples.Count == 0)
                            {
                                continue;
                            }

                            Assert.Contains(examples, example => example.Kind == RuleExampleKind.Do);
                            Assert.Contains(examples, example => example.Kind == RuleExampleKind.Dont);
                            Assert.All(examples, example =>
                            {
                                Assert.False(string.IsNullOrWhiteSpace(example.Caption), $"{rule.RuleId}: an example has no caption.");
                                Assert.True(example.ActualHeight > 0, $"{rule.RuleId}: an example doesn't show.");
                            });
                        }
                    }

                    Assert.All(RuleCatalog.Rules, rule => Assert.True(titles.Contains(rule.Page), $"{rule.Id}: there's no {rule.Page} page."));
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }
}
