// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for AddDeviceWindow.xaml
    /// </summary>
    public partial class AddDeviceWindow : Window
    {
        public enum AddDeviceResult
        {
            Success = 0,
            Failed_NoDefaultConsole = 1,
            Failed_NeedGDKX = 2
        }

        public AddDeviceResult Result { get; private set; } = AddDeviceResult.Success;
        public DeviceType SelectedDeviceType { get; private set; }
        public CaptureType SelectedCaptureType { get; private set; }
        public string SelectedDeviceName { get; private set; }
        internal CaptureDeviceKind SelectedDeviceKind { get; private set; }
        internal CaptureDeviceRequest Request => new(
            SelectedDeviceKind, SelectedCaptureType,
            SelectedDeviceKind == CaptureDeviceKind.XboxConsole ? SelectedDeviceName : null);

        public AddDeviceWindow()
        {
            InitializeComponent();

            this.Title = Localization.GetLocalizedString("FILE_NEW_CAPTURE");
            OkButton.Content = Localization.GetLocalizedString("ADD_CAPTURE");
            CancelButton.Content = Localization.GetLocalizedString("CANCEL");

            SelectDevice.Content = Localization.GetLocalizedString("SELECT_DEVICE");
            LocalPC.Content = Localization.GetLocalizedString("LOCAL_PC");
            DefaultConsole.Content = Localization.GetLocalizedString("DEFAULT_CONSOLE");
            ConsoleAtIPOrHostname.Text = Localization.GetLocalizedString("CONSOLE_AT_IP_HOSTNAME");
            GenericDevice.Content = Localization.GetLocalizedString("GENERIC_DEVICE");
            WindowsHandheldOrRemoteDevice.Content = Localization.GetLocalizedString("WINDOWS_HANDHELD_REMOTE_DEVICE");

            CaptureTypeLabel.Content = Localization.GetLocalizedString("SELECT_CAPTURE_TYPE");
            WebProxyLabel.Content = Localization.GetLocalizedString("CAPTURE_TYPE_PROXY");
            NetworkTraceLabel.Content = Localization.GetLocalizedString("CAPTURE_TYPE_NETCAP");
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (LocalPC.IsChecked.GetValueOrDefault())
            {
                SelectedDeviceType = DeviceType.LocalPC;
                SelectedDeviceKind = CaptureDeviceKind.LocalPC;
                SelectedDeviceName = Localization.GetLocalizedString("LOCAL_PC");
            }
            else if (CustomIpHostnameConsole.IsChecked.GetValueOrDefault())
            {
                SelectedDeviceType = DeviceType.XboxConsole;
                SelectedDeviceKind = CaptureDeviceKind.XboxConsole;
                SelectedDeviceName = ConsoleIpOrHostname.Text;
            }
            else if (DefaultConsole.IsChecked.GetValueOrDefault())
            {
                SelectedDeviceType = DeviceType.XboxConsole;
                SelectedDeviceKind = CaptureDeviceKind.DefaultXboxConsole;
            }
            else if (GenericDevice.IsChecked.GetValueOrDefault())
            {
                SelectedDeviceType = DeviceType.GenericProxyDevice;
                SelectedDeviceKind = CaptureDeviceKind.GenericProxyDevice;
                SelectedDeviceName = Localization.GetLocalizedString("GENERIC_DEVICE");
            }
            else if (WindowsHandheldOrRemoteDevice.IsChecked.GetValueOrDefault())
            {
                // Treat the same as a generic device internally
                SelectedDeviceType = DeviceType.GenericProxyDevice;
                SelectedDeviceKind = CaptureDeviceKind.WindowsHandheldOrRemoteDevice;
                SelectedDeviceName = Localization.GetLocalizedString("WINDOWS_HANDHELD_REMOTE_DEVICE");
            }

            if (WebProxyLabel.IsChecked.GetValueOrDefault())
            {
                SelectedCaptureType = CaptureType.WebProxy;
            }
            else if (NetworkTraceLabel.IsChecked.GetValueOrDefault())
            {
                SelectedCaptureType = CaptureType.NetworkTrace;
            }

            DialogResult = true;
            Close();
        }

        private void ConsoleIpOrHostname_TextChanged(object sender, TextChangedEventArgs e)
        {
            CustomIpHostnameConsole.IsChecked = true;
            OkButton.IsEnabled = IsConsoleIPOrHostnameValid(ConsoleIpOrHostname.Text);
        }

        private bool IsConsoleIPOrHostnameValid(string text)
        {
            return CaptureDevices.IsValidConsoleAddress(text);
        }

        private void ResetOKButtonState(object sender, RoutedEventArgs e)
        {
            if (OkButton != null)
            {
                if (CustomIpHostnameConsole.IsChecked.GetValueOrDefault())
                {
                    OkButton.IsEnabled = IsConsoleIPOrHostnameValid(ConsoleIpOrHostname.Text);
                }
                else if (GenericDevice.IsChecked.GetValueOrDefault())
                {
                    OkButton.IsEnabled = !NetworkTraceLabel.IsChecked.GetValueOrDefault();
                }
                else if (WindowsHandheldOrRemoteDevice.IsChecked.GetValueOrDefault())
                {
                    OkButton.IsEnabled = !NetworkTraceLabel.IsChecked.GetValueOrDefault();
                }
                else
                {
                    OkButton.IsEnabled = true;
                }
            }
        }
    }
}
