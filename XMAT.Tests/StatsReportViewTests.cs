// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CaptureAnalysisEngine;
using Microsoft.Web.WebView2.Wpf;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport;

namespace XMAT.Tests
{
    // StatsReportView loads XAML through the process-wide WPF Application, so these
    // tests share the capture UI fixture's application instead of their own thread.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class StatsReportViewTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public StatsReportViewTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        public static TheoryData<string> DetachedContexts => new()
        {
            "null",
            "otherReport",
        };

        [Theory]
        [MemberData(nameof(DetachedContexts))]
        public void LeavingStatsReportContext_KeepsCurrentReportSource(string detachedContext)
        {
            _application.Run(_ =>
            {
                var model = CreateStatsReportModel(@"C:\synthetic\first");
                var view = new StatsReportView { DataContext = model };
                WpfTestHost.Pump();
                var webView = GetWebView(view);
                Assert.Equal(new Uri(model.ReportUrl), webView.Source);

                view.DataContext = detachedContext == "null" ? null : new object();
                WpfTestHost.Pump();

                Assert.Equal(new Uri(model.ReportUrl), webView.Source);
            });
        }

        [Fact]
        public void ChangingToAnotherStatsReport_UpdatesSource()
        {
            _application.Run(_ =>
            {
                var first = CreateStatsReportModel(@"C:\synthetic\first");
                var second = CreateStatsReportModel(@"C:\synthetic\second");
                var view = new StatsReportView { DataContext = first };
                WpfTestHost.Pump();

                view.DataContext = null;
                view.DataContext = second;
                WpfTestHost.Pump();

                Assert.Equal(new Uri(second.ReportUrl), GetWebView(view).Source);
            });
        }

        [Fact]
        public void JsonTemplateStreaming_MatchesReadReplaceWriteBytes()
        {
            const string Token = "${ReportJson}";
            string json =
                JsonSerializer.Serialize(
                    new
                    {
                        Text =
                            "é雪 " +
                            Token +
                            " \"quoted\"\n\t\u0001",
                    });
            var cases =
                new (string Name, string Template, Encoding Encoding)[]
                {
                    (
                        "zero-token.js",
                        "π no marker\r\n雪",
                        new UnicodeEncoding(
                            bigEndian: false,
                            byteOrderMark: true,
                            throwOnInvalidBytes: true)),
                    (
                        "two-tokens.js",
                        "π before " +
                            Token +
                            "\r\nmiddle " +
                            Token +
                            " after 雪",
                        new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: true,
                            throwOnInvalidBytes: true)),
                };
            string directory =
                AnalysisReportTestPaths
                    .CreateOwnedDirectory(
                        "jst");
            var utf8NoBom =
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true);

            try
            {
                foreach (var testCase in cases)
                {
                    string path =
                        Path.Combine(
                            directory,
                            testCase.Name);
                    File.WriteAllText(
                        path,
                        testCase.Template,
                        testCase.Encoding);
                    var document =
                        new TemplateReportDocument(
                            json);

                    ReportViewModel.WriteJsonTemplate(
                        path,
                        Token,
                        document);

                    Assert.Equal(
                        1,
                        document.ToJsonCalls);
                    Assert.Equal(
                        utf8NoBom.GetBytes(
                            testCase.Template.Replace(
                                Token,
                                json)),
                        File.ReadAllBytes(path));
                }
                Assert.DoesNotContain(
                    Directory.GetFiles(
                        directory),
                    path => path.EndsWith(
                        ".json.tmp",
                        StringComparison.Ordinal));
            }
            finally
            {
                AnalysisReportTestPaths
                    .DeleteOwnedDirectory(
                        directory);
            }
        }

        // The real constructor copies WPF pack resources, which requires the product
        // to be the process entry assembly; only the report URL matters to the view.
        private static StatsReportModel CreateStatsReportModel(string analysisDirectory)
        {
            var model = (StatsReportModel)RuntimeHelpers.GetUninitializedObject(typeof(StatsReportModel));
            typeof(StatsReportModel)
                .GetField("<ReportUrl>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(model, $"file:///{analysisDirectory}/index.html");
            return model;
        }

        private static WebView2 GetWebView(StatsReportView view)
        {
            return Assert.IsType<WebView2>(view.FindName("webView"));
        }

        private sealed class TemplateReportDocument :
            ReportDocument
        {
            private readonly string _json;

            internal TemplateReportDocument(
                string json)
            {
                _json = json;
            }

            internal int ToJsonCalls { get; private set; }

            public override string ToJson()
            {
                ToJsonCalls++;
                return _json;
            }
        }
    }
}
