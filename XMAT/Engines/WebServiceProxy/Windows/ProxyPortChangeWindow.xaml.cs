// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows;
using XMAT.SharedInterfaces;

namespace XMAT.WebServiceCapture
{
    public partial class ProxyPortChangeWindow : Window
    {
        internal UInt16 SelectedPort { get; private set; }
        internal bool ApplyNow { get; private set; }

        internal ProxyPortChangeWindow(UInt16 currentPort, UInt16 pendingPort, DeviceType deviceType)
        {
            InitializeComponent();
            Title = Localization.GetLocalizedString("PROXY_PORT_CHANGE");
            CurrentPortLabel.Text = Localization.GetLocalizedString("PROXY_PORT_CURRENT", currentPort);
            NewPortLabel.Content = Localization.GetLocalizedString("PROXY_PORT_NEW");
            NewPort.Text = (pendingPort == 0 ? currentPort : pendingPort).ToString(CultureInfo.InvariantCulture);
            Instructions.Text = Localization.GetLocalizedString(deviceType switch
            {
                DeviceType.XboxConsole => "PROXY_PORT_INSTRUCTIONS_XBOX",
                DeviceType.LocalPC => "PROXY_PORT_INSTRUCTIONS_PC",
                _ => "PROXY_PORT_INSTRUCTIONS_MANUAL"
            });
            ApplyButton.Content = Localization.GetLocalizedString("PROXY_PORT_APPLY_NOW");
            LaterButton.Content = Localization.GetLocalizedString("PROXY_PORT_LATER");
            CancelButton.Content = Localization.GetLocalizedString("CANCEL");
        }

        internal static bool TryParsePort(string value, out UInt16 port) =>
            UInt16.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port > 0;

        private void Apply_Click(object sender, RoutedEventArgs e) => Accept(true);
        private void Later_Click(object sender, RoutedEventArgs e) => Accept(false);

        private void Accept(bool applyNow)
        {
            if (!TryParsePort(NewPort.Text, out var port))
            {
                ValidationMessage.Text = Localization.GetLocalizedString("PROXY_PORT_INVALID");
                return;
            }
            SelectedPort = port;
            ApplyNow = applyNow;
            DialogResult = true;
        }
    }
}
