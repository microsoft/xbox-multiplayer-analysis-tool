// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using CaptureAnalysisEngine;
using XMAT.Models;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.NetworkTraceCaptureAnalysis.Models;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels;
using XMAT.XboxLiveCaptureAnalysis.ReportViews.RuleDataViews;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class AnalysisReportAccessibilityTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public AnalysisReportAccessibilityTests(CaptureUiApplicationFixture application) => _application = application;

        // A count is not told by color alone: the new look shows its level, and both looks name it.
        [Fact]
        public void IssueCounts_NameTheirLevelAndTakeTheThemeColorsForIt()
        {
            _application.Run(window =>
            {
                bool newLook = LookState.Current.IsNewLook;
                try
                {
                    LookState.Current.IsNewLook = true;
                    var view = new PerEndpointReportView { DataContext = new PerEndpointReportModel(new CaptureAppModel(), "source", Report()) };
                    WpfTestHost.Show(window, view);
                    List<Button> counts = Descendants(view).OfType<Button>().Where(button => IssueBadge.GetCount(button) != null).ToList();
                    Button errors = counts.Single(button => IssueBadge.GetCount(button).RuleName == BatchFrequencyRule.DisplayName);
                    Button warning = counts.Single(button => IssueBadge.GetCount(button).RuleName == RepeatedCallsRule.DisplayName);
                    Button none = counts.First(button => IssueBadge.GetCount(button).IssueLevel == ViolationLevel.Info);
                    TextBlock detail = Descendants(view).OfType<TextBlock>()
                        .Single(text => IssueBadge.GetCount(text)?.RuleName == BatchFrequencyRule.DisplayName);
                    string threeErrors = Localization.GetLocalizedString("LTA_ISSUE_COUNT_ERRORS", 3);
                    string name = UIElementAutomationPeer.CreatePeerForElement(errors).GetName();

                    Assert.Equal(threeErrors, errors.Content);
                    Assert.Equal(Localization.GetLocalizedString("LTA_ISSUE_COUNT_WARNING", 1), warning.Content);
                    Assert.Equal("0", none.Content);
                    Assert.Equal(threeErrors, detail.Text);
                    Assert.Contains(BatchFrequencyRule.DisplayName, name);
                    Assert.Contains(threeErrors, name);
                    Assert.Equal(name, errors.ToolTip);
                    Assert.Equal(threeErrors, UIElementAutomationPeer.CreatePeerForElement(detail).GetName());
                    Assert.DoesNotContain("NO_IMPL_", name + Localization.GetLocalizedString("LTA_ISSUE_COUNT_NONE"));
                    // The new look colors each level with its status role; Info counts are good news.
                    foreach (var (element, role) in new (FrameworkElement, string)[]
                        { (errors, "Error"), (warning, "Warning"), (none, "Success"), (detail, "Error") })
                    {
                        Assert.Same(element.FindResource($"{role}SoftBrush"),
                            element is TextBlock text ? text.Background : ((Control)element).Background);
                        Assert.Same(element.FindResource($"{role}TextBrush"),
                            element is TextBlock label ? label.Foreground : ((Control)element).Foreground);
                    }

                    // The original look keeps its issue colors.
                    LookState.Current.IsNewLook = false;
                    window.Resources["IssueErrorBackgroundBrush"] = Brushes.Purple;
                    WpfTestHost.Pump();
                    Assert.Equal("3", errors.Content);
                    Assert.Equal("3", detail.Text);
                    Assert.Equal(name, UIElementAutomationPeer.CreatePeerForElement(errors).GetName());
                    Assert.Same(Brushes.Purple, errors.Background);
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
                }
            });
        }

        [Fact]
        public void RuleViolations_NameTheIconAndTheRequestButtons()
        {
            _application.Run(window =>
            {
                var view = new XboxLiveCaptureAnalysis.ReportViews.Controls.RuleViolationDetail
                {
                    DataContext = Report().EndpointReportData[0].RuleReportData[0].Violations[0]
                };
                WpfTestHost.Show(window, view);
                ((Expander)view.FindName("EndpointRuleExpanderTemplate")).IsExpanded = true;
                WpfTestHost.Pump();

                string icon = UIElementAutomationPeer.CreatePeerForElement(Descendants(view).OfType<Image>().Single()).GetName();
                Assert.Equal(Localization.GetLocalizedString("LTA_VIOLATION_ICON_NAME"), icon);
                Button request = Descendants(view).OfType<Button>().Single();
                string name = UIElementAutomationPeer.CreatePeerForElement(request).GetName();
                Assert.Equal(Localization.GetLocalizedString("LTA_GO_TO_REQUEST", 42), name);
                Assert.Equal(name, request.ToolTip);
                Assert.DoesNotContain("NO_IMPL_", icon + name);
            });
        }

        [Fact]
        public void AnalysisTables_HaveNamesAndNetworkChecksTakeTheIssueColors()
        {
            _application.Run(window =>
            {
                var report = new PerEndpointReportView { DataContext = new PerEndpointReportModel(new CaptureAppModel(), "source", Report()) };
                WpfTestHost.Show(window, report);
                AssertNamed((DataGrid)report.FindName("EndpointSummary"));

                var ruleData = new BatchFrequencyRuleDataView
                {
                    DataContext = new BatchFrequencyRuleDataModel(Report().EndpointReportData[0].RuleReportData[0])
                };
                WpfTestHost.Show(window, ruleData);
                Assert.Contains(BatchFrequencyRule.DisplayName, AssertNamed(Descendants(ruleData).OfType<DataGrid>().Single()));

                var analysis = new NetworkTraceAnalyzerModel();
                analysis.NumericStatsLists.Add(new NetworkTraceAnalyzerResultsModel
                {
                    Topic = "Packets sent by source",
                    Values = new[] { new NetworkTraceAnalyzerResultsDataModel { Name = "192.168.1.20", Value = 12 } }
                });
                analysis.NumericStats.Add(new NetworkTraceAnalyzerResultsDataModel { Name = "Packets over the MTU", Value = 3 });
                analysis.NumericStats.Add(new NetworkTraceAnalyzerResultsDataModel { Name = "Duplicate packets", Value = 0 });
                bool newLook = LookState.Current.IsNewLook;
                try
                {
                    LookState.Current.IsNewLook = true;
                    ResourceDictionary look = ThemeManager.CreateLook(newLook: true);
                    ThemeManager.LoadTheme(look, "Light", newLook: true, highContrast: false);
                    window.Resources = look;
                    var network = new NetworkTraceAnalysisView { DataContext = new { AnalysisData = analysis } };
                    WpfTestHost.Show(window, network);
                    Assert.StartsWith(
                        "Packets sent by source", AssertNamed(Descendants(network).OfType<HistogramView>().Single().Rows));
                    // Info checks are good news, so the new look gives their counts the success colors.
                    foreach (var (check, role) in new[] { ("Packets over the MTU", "Error"), ("Duplicate packets", "Success") })
                    {
                        ContentControl count = CheckCount(network, check);
                        Assert.Same(count.FindResource($"{role}SoftBrush"), count.Background);
                        Assert.Same(count.FindResource($"{role}TextBrush"), count.Foreground);
                    }

                    // The original look keeps its table, named after the list.
                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();
                    Assert.Equal("Packets sent by source", AssertNamed(Descendants(network).OfType<DataGrid>().Single()));
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
                }
            });
        }

        private static string AssertNamed(UIElement element)
        {
            string name = UIElementAutomationPeer.CreatePeerForElement(element).GetName();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.DoesNotContain("NO_IMPL_", name);
            return name;
        }

        // The count of the network analysis's check named CHECK, in the new look.
        private static ContentControl CheckCount(NetworkTraceAnalysisView view, string check)
        {
            var checks = (ItemsControl)view.FindName("Checks");
            object item = checks.Items.Cast<NetworkTraceAnalyzerResultsDataModel>().Single(stat => stat.Name == check);
            var presenter = (ContentPresenter)checks.ItemContainerGenerator.ContainerFromItem(item);
            return (ContentControl)presenter.ContentTemplate.FindName("Count", presenter);
        }

        // One endpoint: Batch Frequency found 3 errors, one with a call, and Repeated Calls 1 warning.
        private static PerEndpointReportDocument Report()
        {
            Localization.LoadLanguage("en-us");
            var endpoint = new EndpointReportData
            {
                UriService = "sessiondirectory.xboxlive.com",
                Kind = EndpointKind.Captured,
                ActionableHost = "sessiondirectory.xboxlive.com"
            };
            var batch = new RuleReportData { Name = BatchFrequencyRule.DisplayName, Description = "Batch calls" };
            batch.Metadata.Errors = 3;
            foreach (string key in new[]
            {
                BatchFrequencyRule.TotalBatchCallsDataKey, BatchFrequencyRule.AllowedTimeBetweenCallsDataKey,
                BatchFrequencyRule.TimesExceededDataKey, BatchFrequencyRule.PotentialReducedCallCountDataKey
            })
            {
                batch.ResultData.Add(key, "3");
            }
            var violation = new RuleViolationData
            {
                Level = Localization.GetLocalizedString("LTA_VIOLATION_LEVEL_ERROR"),
                Summary = "Batch calls close together"
            };
            violation.CallData.Add(new ViolationCallData { Id = 42, UriMethod = "GET https://sessiondirectory.xboxlive.com/" });
            batch.Violations.Add(violation);
            var repeated = new RuleReportData { Name = RepeatedCallsRule.DisplayName, Description = "Repeated calls" };
            repeated.Metadata.Warnings = 1;
            foreach (string key in new[]
                { RepeatedCallsRule.TotalCallsDataKey, RepeatedCallsRule.DuplicatesDataKey, RepeatedCallsRule.PercentageDataKey })
            {
                repeated.ResultData.Add(key, "1");
            }
            endpoint.RuleReportData.Add(batch);
            endpoint.RuleReportData.Add(repeated);
            var document = new PerEndpointReportDocument();
            document.EndpointReportData.Add(endpoint);
            return document;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject source)
        {
            yield return source;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
            {
                foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(source, index)))
                {
                    yield return child;
                }
            }
        }
    }
}
