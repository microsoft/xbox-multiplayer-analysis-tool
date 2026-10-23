// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using XMAT.Scripting;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ScriptEditorViewTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private readonly CaptureUiApplicationFixture _application;

        public ScriptEditorViewTests(CaptureUiApplicationFixture application)
        {
            _application = application;
            Localization.LoadLanguage("en-us");
        }

        // The event selector uses DisplayMemberPath, which WPF applies through ItemTemplateSelector, so the
        // app's ComboBox template must hand that selector to its selection box.
        [Fact]
        public void EventSelector_ShowsTheSelectedEventName()
        {
            _application.Run(window =>
            {
                window.Resources[typeof(ComboBox)] = AppComboBoxStyle();
                var scripts = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType));
                var view = new ScriptEditorView { ScriptCollection = scripts };
                WpfTestHost.Show(window, view);
                view.EventSelector.SelectedItem = scripts[WebServiceCaptureScriptableEventType.WebRequest];
                WpfTestHost.Pump();

                var selectionBox = (ContentPresenter)view.EventSelector.Template.FindName("ContentSite", view.EventSelector);
                Assert.Equal("Web Request", Descendants(selectionBox).OfType<TextBlock>().Single().Text);
            });
        }

        [Fact]
        public void BusyState_DisablesToggleAndPreventsDuplicateEnable()
        {
            _application.Run(window =>
            {
                var gate = new ViewCompileGate();
                var scripts = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType));
                var model = new ScriptModel(
                    WebServiceCaptureScriptableEventType.WebRequest,
                    new ScriptHost(gate.WaitAsync))
                {
                    Script = "// slow\nParams.Continue = true;"
                };
                scripts[WebServiceCaptureScriptableEventType.WebRequest] = model;
                var view = new ScriptEditorView { ScriptCollection = scripts };
                WpfTestHost.Show(window, view);
                view.EventSelector.SelectedItem = model;
                WpfTestHost.Pump();

                ScriptCommands.ValidateScript.Execute(model, view);
                PumpUntil(() => gate.Started.Task.IsCompleted);

                Assert.True(model.IsCompiling);
                Assert.False(model.IsEnabled);
                Assert.False(scripts.HasActiveScripts);
                Assert.False(view.ScriptEnabled.IsEnabled);
                Assert.True(view.ScriptEditor.IsEnabled);
                Assert.True(view.EventSelector.IsEnabled);
                Assert.Equal("Compiling…", ToggleText(view));
                Assert.False(ScriptCommands.ValidateScript.CanExecute(model, view));
                Assert.False(ScriptCommands.LoadScript.CanExecute(model, view));
                Assert.False(ScriptCommands.SaveScript.CanExecute(model, view));

                ScriptCommands.ValidateScript.Execute(model, view);
                WpfTestHost.Pump();
                Assert.Equal(1, gate.InvocationCount);

                view.EventSelector.SelectedItem =
                    scripts[WebServiceCaptureScriptableEventType.WebResponse];
                WpfTestHost.Pump();
                Assert.True(view.ScriptEnabled.IsEnabled);
                Assert.Equal("Enable", ToggleText(view));
                Assert.True(ScriptCommands.LoadScript.CanExecute(
                    scripts[WebServiceCaptureScriptableEventType.WebResponse],
                    view));

                view.EventSelector.SelectedItem = model;
                WpfTestHost.Pump();
                Assert.False(view.ScriptEnabled.IsEnabled);
                Assert.Equal("Compiling…", ToggleText(view));

                gate.Release();
                PumpUntil(() => model.IsEnabled);

                Assert.False(model.IsCompiling);
                Assert.True(view.ScriptEnabled.IsChecked.GetValueOrDefault());
                Assert.Equal("Disable", ToggleText(view));
                Assert.Equal("Script compiled successfully.", view.ScriptSuccess.Text);
                Assert.False(ScriptCommands.LoadScript.CanExecute(model, view));
                Assert.False(ScriptCommands.SaveScript.CanExecute(model, view));
            });
        }

        [Fact]
        public void EditDuringCompilation_InvalidatesOldWorkAndRefreshesCommands()
        {
            _application.Run(window =>
            {
                var gate = new ViewCompileGate();
                var scripts = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType));
                var model = new ScriptModel(
                    WebServiceCaptureScriptableEventType.WebRequest,
                    new ScriptHost(gate.WaitAsync))
                {
                    Script = "// slow\nParams.Continue = false;"
                };
                scripts[WebServiceCaptureScriptableEventType.WebRequest] = model;
                var view = new ScriptEditorView { ScriptCollection = scripts };
                WpfTestHost.Show(window, view);
                view.EventSelector.SelectedItem = model;
                WpfTestHost.Pump();

                ScriptCommands.ValidateScript.Execute(model, view);
                PumpUntil(() => gate.Started.Task.IsCompleted);

                model.Script = "Params.Continue = true;";
                WpfTestHost.Pump();

                Assert.False(model.IsCompiling);
                Assert.False(model.IsEnabled);
                Assert.True(view.ScriptEnabled.IsEnabled);
                Assert.Equal("Enable", ToggleText(view));
                Assert.True(ScriptCommands.LoadScript.CanExecute(model, view));
                Assert.True(ScriptCommands.SaveScript.CanExecute(model, view));
                Assert.Equal(Visibility.Collapsed, view.ScriptSuccess.Visibility);
                Assert.Equal(Visibility.Collapsed, view.ScriptOutput.Visibility);

                ScriptCommands.ValidateScript.Execute(model, view);
                PumpUntil(() => model.IsEnabled);
                gate.Release();
                WpfTestHost.Pump();

                Assert.True(model.IsEnabled);
                Assert.False(model.IsCompiling);
                Assert.Equal(2, gate.InvocationCount);
            });
        }

        [Fact]
        public async Task Status_FollowsSelectedModelAndShowsWarnings()
        {
            var scripts = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType));
            ScriptModel warning = scripts[WebServiceCaptureScriptableEventType.WebRequest];
            warning.Script = "#warning xmat-test\nParams.Continue = true;";
            Assert.Equal(
                ScriptActivationResult.Enabled,
                await warning.EnableScriptAsync<WebServiceCaptureScriptParams>());
            ScriptModel error = scripts[WebServiceCaptureScriptableEventType.WebResponse];
            error.Script = "Params.Continue = ;";
            Assert.Equal(
                ScriptActivationResult.Failed,
                await error.EnableScriptAsync<WebServiceCaptureScriptParams>());

            _application.Run(window =>
            {
                var view = new ScriptEditorView { ScriptCollection = scripts };
                WpfTestHost.Show(window, view);

                view.EventSelector.SelectedItem = warning;
                WpfTestHost.Pump();
                Assert.True(warning.IsEnabled);
                Assert.Equal(Visibility.Visible, view.ScriptSuccess.Visibility);
                Assert.Equal(
                    "Script compiled successfully with warnings.",
                    view.ScriptSuccess.Text);
                Assert.Equal(Visibility.Visible, view.ScriptOutput.Visibility);
                Assert.NotEmpty(view.ScriptOutput.Items);
                Assert.False(ScriptCommands.LoadScript.CanExecute(warning, view));
                Assert.False(ScriptCommands.SaveScript.CanExecute(warning, view));

                view.EventSelector.SelectedItem = error;
                WpfTestHost.Pump();
                Assert.Equal(Visibility.Collapsed, view.ScriptSuccess.Visibility);
                Assert.Equal(Visibility.Visible, view.ScriptOutput.Visibility);
                Assert.NotEmpty(view.ScriptOutput.Items);
                Assert.True(ScriptCommands.LoadScript.CanExecute(error, view));
                Assert.True(ScriptCommands.SaveScript.CanExecute(error, view));

                view.EventSelector.SelectedItem =
                    scripts[WebServiceCaptureScriptableEventType.SslConnectionRequest];
                WpfTestHost.Pump();
                Assert.Equal(Visibility.Collapsed, view.ScriptSuccess.Visibility);
                Assert.Equal(Visibility.Collapsed, view.ScriptOutput.Visibility);
            });
        }

        [Fact]
        public void UnexpectedCompileFailure_ShowsInlineAndRestoresCommands()
        {
            _application.Run(window =>
            {
                var gate = new ViewCompileGate();
                var scripts = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType));
                var model = new ScriptModel(
                    WebServiceCaptureScriptableEventType.WebRequest,
                    new ScriptHost(gate.WaitAsync))
                {
                    Script = "// unexpected-fault\nParams.Continue = true;"
                };
                scripts[WebServiceCaptureScriptableEventType.WebRequest] = model;
                var view = new ScriptEditorView { ScriptCollection = scripts };
                WpfTestHost.Show(window, view);
                view.EventSelector.SelectedItem = model;
                WpfTestHost.Pump();

                ScriptCommands.ValidateScript.Execute(model, view);
                PumpUntil(() => model.HasCompilationMessage);

                Assert.False(model.IsEnabled);
                Assert.False(model.IsCompiling);
                Assert.True(view.ScriptEnabled.IsEnabled);
                Assert.False(view.ScriptEnabled.IsChecked.GetValueOrDefault());
                Assert.Equal(
                    "Could not compile; script remains disabled. See the app log.",
                    view.ScriptSuccess.Text);
                Assert.Equal(Visibility.Visible, view.ScriptSuccess.Visibility);
                Assert.Equal(Visibility.Collapsed, view.ScriptOutput.Visibility);
                Assert.True(ScriptCommands.LoadScript.CanExecute(model, view));
                Assert.True(ScriptCommands.SaveScript.CanExecute(model, view));
            });
        }

        private static string ToggleText(ScriptEditorView view) =>
            ((StackPanel)view.ScriptEnabled.Content).Children
                .OfType<TextBlock>()
                .Single()
                .Text;

        private static void PumpUntil(Func<bool> condition)
        {
            var timeout = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(
                    timeout.Elapsed < TimeSpan.FromSeconds(5),
                    "Timed out waiting for the WPF state change.");
                WpfTestHost.Pump();
                Thread.Sleep(10);
            }
            WpfTestHost.Pump();
        }

        private static Style AppComboBoxStyle()
        {
            using var stream = typeof(ScriptEditorViewTests).Assembly.GetManifestResourceStream("XMAT.Tests.App.xaml");
            Assert.NotNull(stream);
            var document = XDocument.Load(stream);
            var style = new XElement(document.Descendants(Presentation + "Style")
                .Single(element => (string)element.Attribute("TargetType") == "ComboBox"));
            foreach (var declaration in document.Root.Attributes().Where(attribute =>
                attribute.IsNamespaceDeclaration && !attribute.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)))
            {
                style.SetAttributeValue(declaration.Name, declaration.Value);
            }
            return (Style)XamlReader.Parse(style.ToString());
        }

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

        private sealed class ViewCompileGate
        {
            private readonly TaskCompletionSource<bool> _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _invocationCount;

            internal TaskCompletionSource<bool> Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal int InvocationCount => Volatile.Read(ref _invocationCount);

            internal async Task WaitAsync(string script)
            {
                Interlocked.Increment(ref _invocationCount);
                if (script.Contains("// unexpected-fault", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Unexpected compile failure.");
                }
                if (!script.Contains("// slow", StringComparison.Ordinal))
                    return;

                Started.TrySetResult(true);
                await _release.Task;
            }

            internal void Release() => _release.TrySetResult(true);
        }
    }
}
