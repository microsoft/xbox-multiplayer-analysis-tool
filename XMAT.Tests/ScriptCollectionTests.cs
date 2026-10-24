// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using XMAT.Scripting;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ScriptCollectionTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public ScriptCollectionTests(CaptureUiApplicationFixture application)
        {
            _application = application;
            Localization.LoadLanguage("en-us");
        }

        [Fact]
        public async Task TogglingScripts_ReportsActiveEventsAndRaisesPropertyChanged()
        {
            var scripts = Scripts();
            var webRequest = scripts[WebServiceCaptureScriptableEventType.WebRequest];
            var webResponse = scripts[WebServiceCaptureScriptableEventType.WebResponse];
            var changes = new List<string>();
            ((INotifyPropertyChanged)scripts).PropertyChanged += (sender, args) => changes.Add(args.PropertyName);

            Assert.Empty(scripts.ActiveScripts);
            Assert.False(scripts.HasActiveScripts);
            Assert.Equal("No scripts are enabled for this capture.", scripts.ActiveScriptsText);
            Assert.Equal(string.Empty, scripts.ActiveCountText);

            await EnableAsync(webResponse);
            await EnableAsync(webRequest);

            Assert.Equal([webRequest, webResponse], scripts.ActiveScripts);
            Assert.True(scripts.HasActiveScripts);
            Assert.Equal("Enabled scripts: Web Request, Web Response", scripts.ActiveScriptsText);
            Assert.Equal(" (2 active)", scripts.ActiveCountText);
            Assert.Contains(nameof(ScriptCollection.ActiveScripts), changes);
            Assert.Contains(nameof(ScriptCollection.HasActiveScripts), changes);
            Assert.Contains(nameof(ScriptCollection.ActiveScriptsText), changes);
            Assert.Contains(nameof(ScriptCollection.ActiveCountText), changes);

            changes.Clear();
            Disable(webRequest);

            Assert.Same(webResponse, Assert.Single(scripts.ActiveScripts));
            Assert.Equal(" (1 active)", scripts.ActiveCountText);
            Assert.Contains(nameof(ScriptCollection.ActiveCountText), changes);
        }

        [Fact]
        public async Task ReplacedScript_NoLongerCountsOrRaisesChanges()
        {
            var scripts = Scripts();
            var replaced = scripts[WebServiceCaptureScriptableEventType.WebResponse];
            scripts[WebServiceCaptureScriptableEventType.WebResponse] =
                new ScriptModel(WebServiceCaptureScriptableEventType.WebResponse);
            var changes = new List<string>();
            ((INotifyPropertyChanged)scripts).PropertyChanged += (sender, args) => changes.Add(args.PropertyName);

            await EnableAsync(replaced);

            Assert.False(scripts.HasActiveScripts);
            Assert.Empty(changes);
        }

        [Fact]
        public async Task ScriptEditorTabAndView_FollowEnabledScripts()
        {
            var scripts = Scripts();
            var webResponse = scripts[WebServiceCaptureScriptableEventType.WebResponse];
            await EnableAsync(webResponse);

            _application.Run(window =>
            {
                var controller = new WebServiceDeviceCaptureController("Test device", DeviceType.Unknown, true)
                {
                    Scripts = scripts
                };
                var view = new CaptureConnectionView { AutoScroll = false };
                view.ConnectionList.IsSynchronizedWithCurrentItem = false;
                view.DataContext = new TestCaptureContext(controller);
                WpfTestHost.Show(window, view);
                view.ScriptEditorTab.IsSelected = true;
                WpfTestHost.Pump();
                var editor = (ScriptEditorView)view.ScriptEditorTab.Content;

                Assert.Equal("Script Editor (1 active)", HeaderText(view.ScriptEditorTab));
                Assert.Equal("Enabled scripts: Web Response", editor.ActiveScriptsSummary.Text);
                Assert.Equal(Visibility.Visible, editor.AllHostsWarning.Visibility);

                Disable(webResponse);
                WpfTestHost.Pump();

                Assert.Equal("Script Editor", HeaderText(view.ScriptEditorTab));
                Assert.Equal("No scripts are enabled for this capture.", editor.ActiveScriptsSummary.Text);
                Assert.Equal(Visibility.Collapsed, editor.AllHostsWarning.Visibility);
            });
        }

        private static ScriptCollection Scripts() => new(typeof(WebServiceCaptureScriptableEventType));

        private static async Task EnableAsync(ScriptModel script) =>
            Assert.Equal(
                ScriptActivationResult.Enabled,
                await script.EnableScriptAsync<WebServiceCaptureScriptParams>());

        private static void Disable(ScriptModel script) => script.DisableScript();

        private static string HeaderText(TabItem tab) => string.Concat(
            Descendants(tab).OfType<TextBlock>().Single().Inlines.OfType<Run>().Select(run => run.Text));

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        private sealed class TestCaptureContext : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.Unknown;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "Test device";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public System.Drawing.Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; }

            internal TestCaptureContext(IDeviceCaptureController captureController)
            {
                CaptureController = captureController;
            }
        }
    }
}
