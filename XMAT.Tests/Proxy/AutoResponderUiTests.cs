// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Proxy
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class AutoResponderUiTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void PresetCanBeAddedEditedAndDisabled()
        {
            application.Run(window =>
            {
                var rules = new AutoResponderRules();
                var view = new AutoResponderView { Rules = rules };
                window.Content = view;
                window.Show();
                Find<ComboBox>(view, "ScenarioBox").SelectedIndex = 1;
                Find<TextBox>(view, "PatternBox").Text = "https://example.com/api/*";
                Click(view, "AddButton");
                AutoResponderRule added = Assert.Single(rules.Items);
                Assert.Equal(429, added.Status);
                Assert.Equal("Retry-After: 30\r\nContent-Type: application/json", added.HeadersText);

                Find<ListBox>(view, "RuleList").SelectedItem = added;
                Find<TextBox>(view, "StatusBox").Text = "503";
                Click(view, "UpdateButton");
                Assert.Equal(503, Assert.Single(rules.Items).Status);
                Find<CheckBox>(view, "EnabledBox").IsChecked = false;
                Assert.Null(rules.Match("GET", new Uri("https://example.com/api/a")));
            });
        }

        [Fact]
        public void CapturedResponse_ExcludesConnectionNominatedHeaders()
        {
            application.Run(window =>
            {
                var capture = new ProxyConnectionModel(1, "200", DateTime.Now, TimeSpan.Zero,
                    "https", "example.com", "GET", "/", "GET / HTTP/1.1\r\n", Array.Empty<byte>(),
                    "HTTP/1.1 200 OK\r\nConnection: X-Private\r\nX-Private: hop\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\n",
                    new byte[] { 0, 255 }, "127.0.0.1");
                var view = new AutoResponderView { Rules = new AutoResponderRules(), SelectedCapture = capture };
                window.Content = view;
                window.Show();
                Click(view, "CapturedResponseButton");
                string headers = Find<TextBox>(view, "HeadersBox").Text;
                Assert.Contains("Content-Type: text/plain", headers);
                Assert.DoesNotContain("X-Private", headers);
                Assert.DoesNotContain("Content-Length", headers);
                Assert.Equal("AP8=", Find<TextBox>(view, "BodyBox").Text);
                Assert.True(Find<CheckBox>(view, "Base64Box").IsChecked);
            });
        }

        private static T Find<T>(AutoResponderView view, string name) where T : FrameworkElement =>
            Assert.IsType<T>(view.FindName(name));

        private static void Click(AutoResponderView view, string name)
        {
            Button button = Find<Button>(view, name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }
    }
}
