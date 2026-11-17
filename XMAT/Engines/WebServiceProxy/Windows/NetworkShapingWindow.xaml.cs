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
        internal NetworkShapingProfile SelectedProfile { get; private set; }
        internal int BodyTimeoutSeconds { get; private set; }

        internal NetworkShapingWindow(NetworkShapingProfile current, int timeout, bool running)
        {
            InitializeComponent();
            Title = Localization.GetLocalizedString("SHAPING_TITLE");
            Instructions.Text = Localization.GetLocalizedString("SHAPING_HELP");
            DownLabel.Content = Localization.GetLocalizedString("SHAPING_DOWN");
            UpLabel.Content = Localization.GetLocalizedString("SHAPING_UP");
            LatencyLabel.Content = Localization.GetLocalizedString("SHAPING_LATENCY");
            JitterLabel.Content = Localization.GetLocalizedString("SHAPING_JITTER");
            TimeoutLabel.Content = Localization.GetLocalizedString("SHAPING_TIMEOUT");
            ApplyButton.Content = Localization.GetLocalizedString("SHAPING_APPLY");
            CancelButton.Content = Localization.GetLocalizedString("CANCEL");
            Preset.ItemsSource = new[] { "off", "min", "avg", "exc", "custom", "broken", "disconnect" };
            Preset.SelectedItem = current.Mode == NetworkShapingMode.Shape
                ? "custom" : current.Mode.ToString().ToLowerInvariant();
            ShowValues(current);
            Timeout.Text = timeout.ToString(CultureInfo.InvariantCulture);
            Timeout.IsEnabled = !running;
        }

        private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Preset.SelectedItem is string name && name != "custom")
            {
                ShowValues(NetworkShapingProfile.Preset(name));
            }
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
            try
            {
                string preset = Preset.SelectedItem as string;
                NetworkShapingMode mode = preset is "off" or "broken" or "disconnect"
                    ? NetworkShapingProfile.Preset(preset).Mode
                    : NetworkShapingMode.Shape;
                var profile = new NetworkShapingProfile(
                    mode, Number(Down), Number(Up), Number(Latency), Number(Jitter));
                int timeout = Number(Timeout);
                new NetworkShapingPlan(1, profile, BodyTimeoutSeconds: timeout).Validate();
                SelectedProfile = profile;
                BodyTimeoutSeconds = timeout;
                DialogResult = true;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                ValidationMessage.Text = Localization.GetLocalizedString("SHAPING_INVALID");
            }
        }

        private static int Number(TextBox input) =>
            int.Parse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
