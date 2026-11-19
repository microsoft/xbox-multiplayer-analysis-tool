// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using XMAT.Operations.Captures;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture
{
    public partial class NetworkShapingWindow : Window
    {
        private static readonly string[] ShapedPresets = { "min", "avg", "exc" };

        internal NetworkShapingProfile SelectedProfile { get; private set; }
        internal int BodyTimeoutSeconds { get; private set; }

        internal NetworkShapingWindow(NetworkShapingProfile current, int timeout, bool running)
        {
            InitializeComponent();
            Title = Localization.GetLocalizedString("SHAPING_TITLE");
            Instructions.Text = Localization.GetLocalizedString("SHAPING_HELP");
            PresetLabel.Content = Localization.GetLocalizedString("SHAPING_PRESET");
            DownLabel.Content = Localization.GetLocalizedString("SHAPING_DOWN");
            UpLabel.Content = Localization.GetLocalizedString("SHAPING_UP");
            LatencyLabel.Content = Localization.GetLocalizedString("SHAPING_LATENCY");
            JitterLabel.Content = Localization.GetLocalizedString("SHAPING_JITTER");
            TimeoutLabel.Content = Localization.GetLocalizedString("SHAPING_TIMEOUT");
            ApplyButton.Content = Localization.GetLocalizedString("SHAPING_APPLY");
            CancelButton.Content = Localization.GetLocalizedString("CANCEL");
            Preset.ItemsSource = new[] { "off", "min", "avg", "exc", "custom", "broken", "disconnect" };
            Preset.SelectedItem = current.Mode == NetworkShapingMode.Shape
                ? Array.Find(ShapedPresets, name => NetworkShapingProfile.Preset(name) == current) ?? "custom"
                : current.Mode.ToString().ToLowerInvariant();
            ShowValues(current);
            Timeout.Text = timeout.ToString(CultureInfo.InvariantCulture);
            Timeout.IsEnabled = !running;
        }

        // Rates apply only to shaping; off, broken and disconnect never record the edited values.
        private bool Shaped => Preset.SelectedItem is string name && (name == "custom" || Array.IndexOf(ShapedPresets, name) >= 0);

        private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Preset.SelectedItem is string name && name != "custom")
            {
                ShowValues(NetworkShapingProfile.Preset(name));
            }
            Down.IsEnabled = Up.IsEnabled = Latency.IsEnabled = Jitter.IsEnabled = Shaped;
        }

        private void ShowValues(NetworkShapingProfile profile)
        {
            Down.Text = profile.DownKbps.ToString(CultureInfo.InvariantCulture);
            Up.Text = profile.UpKbps.ToString(CultureInfo.InvariantCulture);
            Latency.Text = profile.LatencyMs.ToString(CultureInfo.InvariantCulture);
            Jitter.Text = profile.JitterMs.ToString(CultureInfo.InvariantCulture);
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (TryReadSelection())
            {
                DialogResult = true;
            }
        }

        internal bool TryReadSelection()
        {
            try
            {
                NetworkShapingProfile profile = Shaped
                    ? new NetworkShapingProfile(
                        NetworkShapingMode.Shape, Number(Down), Number(Up), Number(Latency), Number(Jitter))
                    : NetworkShapingProfile.Preset((string)Preset.SelectedItem);
                int timeout = Number(Timeout);
                new NetworkShapingPlan(1, profile, BodyTimeoutSeconds: timeout).Validate();
                SelectedProfile = profile;
                BodyTimeoutSeconds = timeout;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                ValidationMessage.Text = Localization.GetLocalizedString("SHAPING_INVALID");
                return false;
            }
        }

        private static int Number(TextBox input) =>
            int.Parse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
