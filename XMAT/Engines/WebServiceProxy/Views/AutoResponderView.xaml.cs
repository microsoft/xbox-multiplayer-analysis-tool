// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture
{
    public partial class AutoResponderView : UserControl
    {
        public static readonly DependencyProperty RulesProperty = DependencyProperty.Register(
            nameof(Rules), typeof(AutoResponderRules), typeof(AutoResponderView));
        public static readonly DependencyProperty SelectedCaptureProperty = DependencyProperty.Register(
            nameof(SelectedCapture), typeof(ProxyConnectionModel), typeof(AutoResponderView));

        public AutoResponderRules Rules { get => (AutoResponderRules)GetValue(RulesProperty); set => SetValue(RulesProperty, value); }
        public ProxyConnectionModel SelectedCapture { get => (ProxyConnectionModel)GetValue(SelectedCaptureProperty); set => SetValue(SelectedCaptureProperty, value); }

        public AutoResponderView()
        {
            InitializeComponent();
            EnabledBox.Content = Text("AUTORESPONDER_ENABLED");
            HelpText.Text = Text("AUTORESPONDER_HELP");
            ScenarioLabel.Text = Text("AUTORESPONDER_SCENARIO");
            PatternLabel.Text = Text("AUTORESPONDER_PATTERN");
            MethodLabel.Text = Text("AUTORESPONDER_METHOD");
            StatusLabel.Text = Text("AUTORESPONDER_STATUS");
            DelayLabel.Text = Text("AUTORESPONDER_DELAY");
            HeadersLabel.Text = Text("AUTORESPONDER_HEADERS");
            BodyLabel.Text = Text("AUTORESPONDER_BODY");
            Base64Box.Content = Text("AUTORESPONDER_BASE64");
            BodyFileButton.Content = Text("AUTORESPONDER_BODY_FILE");
            CapturedResponseButton.Content = Text("AUTORESPONDER_CAPTURED");
            AddButton.Content = Text("AUTORESPONDER_ADD");
            UpdateButton.Content = Text("AUTORESPONDER_UPDATE");
            RemoveButton.Content = Text("AUTORESPONDER_REMOVE");
            SaveButton.Content = Text("AUTORESPONDER_SAVE");
            LoadButton.Content = Text("AUTORESPONDER_LOAD");
            ScenarioBox.Items.Add(Text("AUTORESPONDER_CUSTOM"));
            ScenarioBox.Items.Add(Text("AUTORESPONDER_429"));
            ScenarioBox.Items.Add(Text("AUTORESPONDER_401"));
            ScenarioBox.Items.Add(Text("AUTORESPONDER_503"));
            ScenarioBox.Items.Add(Text("AUTORESPONDER_DELAYED"));
            ScenarioBox.SelectedIndex = 0;
        }

        private static string Text(string key) => Localization.GetLocalizedString(key);

        private void Scenario_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScenarioBox.SelectedIndex <= 0) return;
            var scenario = (AutoResponderScenario)(ScenarioBox.SelectedIndex - 1);
            AutoResponderRule preset = AutoResponderRule.FromScenario(scenario, "https://example.com/*");
            MethodBox.Text = preset.Method;
            StatusBox.Text = preset.Status.ToString(CultureInfo.InvariantCulture);
            HeadersBox.Text = preset.HeadersText;
            BodyBox.Text = preset.BodyText;
            Base64Box.IsChecked = false;
            DelayBox.Text = preset.DelayMilliseconds.ToString(CultureInfo.InvariantCulture);
        }

        private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RuleList.SelectedItem is not AutoResponderRule rule) return;
            ScenarioBox.SelectedIndex = 0;
            PatternBox.Text = rule.Pattern;
            MethodBox.Text = rule.Method;
            StatusBox.Text = rule.Status.ToString(CultureInfo.InvariantCulture);
            DelayBox.Text = rule.DelayMilliseconds.ToString(CultureInfo.InvariantCulture);
            HeadersBox.Text = rule.HeadersText;
            BodyBox.Text = rule.BodyText;
            Base64Box.IsChecked = rule.BodyIsBase64;
            ErrorText.Text = string.Empty;
        }

        private AutoResponderRule ReadRule()
        {
            if (!int.TryParse(StatusBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int status) ||
                !int.TryParse(DelayBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int delay))
                throw new ArgumentException(Text("AUTORESPONDER_INVALID_NUMBER"));
            return new AutoResponderRule(PatternBox.Text, MethodBox.Text.Trim(), status, HeadersBox.Text, BodyBox.Text, delay, Base64Box.IsChecked == true);
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (Rules == null) return;
            try { Rules.Add(ReadRule()); ErrorText.Text = string.Empty; }
            catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            if (Rules == null || RuleList.SelectedItem is not AutoResponderRule selected)
            { ErrorText.Text = Text("AUTORESPONDER_SELECT_RULE"); return; }
            try
            {
                AutoResponderRule updated = ReadRule();
                Rules.Replace(selected, updated);
                RuleList.SelectedItem = updated;
                ErrorText.Text = string.Empty;
            }
            catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (RuleList.SelectedItem is AutoResponderRule rule) Rules?.Remove(rule);
        }

        private void BodyFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                byte[] bytes = ReadFile(dialog.FileName, 1024 * 1024, "AUTORESPONDER_BODY_TOO_LARGE");
                BodyBox.Text = Convert.ToBase64String(bytes);
                Base64Box.IsChecked = true;
                ErrorText.Text = string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { ErrorText.Text = ex.Message; }
        }

        private void CapturedResponse_Click(object sender, RoutedEventArgs e)
        {
            ProxyConnectionModel capture = SelectedCapture;
            if (capture == null || string.IsNullOrEmpty(capture.ResponseLineAndHeaders) || capture.IsResponseStreaming ||
                capture.ResponseBodyTruncatedAt != 0 || capture.ResponseBody == null || capture.ResponseBody.Length > 1024 * 1024)
            { ErrorText.Text = Text("AUTORESPONDER_CAPTURE_INVALID"); return; }
            string[] lines = capture.ResponseLineAndHeaders.Split("\r\n");
            if (lines.Skip(1).Any(line => line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)))
            { ErrorText.Text = Text("AUTORESPONDER_CAPTURE_FRAMED"); return; }
            var capturedHeaders = new HeaderCollection();
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) capturedHeaders.Add(line[..colon], line[(colon + 1)..].Trim());
            }
            var connectionHeaders = ProxyHeaderUtilities.GetConnectionHeaderNames(capturedHeaders);
            var headers = new StringBuilder();
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line[..colon];
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || ProxyHeaderUtilities.IsHopByHopHeader(name, connectionHeaders)) continue;
                headers.AppendLine(line);
            }
            StatusBox.Text = capture.Status;
            HeadersBox.Text = headers.ToString();
            BodyBox.Text = Convert.ToBase64String(capture.ResponseBody);
            Base64Box.IsChecked = true;
            ErrorText.Text = Text("AUTORESPONDER_CAPTURE_LOADED");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (Rules == null) return;
            if (MessageBox.Show(Window.GetWindow(this), Text("AUTORESPONDER_SAVE_WARNING"), Text("AUTORESPONDER_TITLE"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "xmat-rules.json" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try { File.WriteAllText(dialog.FileName, Rules.Export(), new UTF8Encoding(false)); ErrorText.Text = string.Empty; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { ErrorText.Text = ex.Message; }
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            if (Rules == null) return;
            var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            if (MessageBox.Show(Window.GetWindow(this), Text("AUTORESPONDER_LOAD_WARNING"), Text("AUTORESPONDER_TITLE"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                byte[] bytes = ReadFile(dialog.FileName, 4 * 1024 * 1024, "AUTORESPONDER_FILE_TOO_LARGE");
                int start = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
                Rules.Import(new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start));
                ErrorText.Text = string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { ErrorText.Text = ex.Message; }
        }

        private static byte[] ReadFile(string path, int limit, string errorKey)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > limit) throw new ArgumentException(Text(errorKey));
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + count > limit) throw new ArgumentException(Text(errorKey));
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
    }
}
