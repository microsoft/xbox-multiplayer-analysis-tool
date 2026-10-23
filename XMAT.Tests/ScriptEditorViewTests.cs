// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

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
    }
}
