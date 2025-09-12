// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using System.Net;
using System.Windows;
using System.Windows.Controls;
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

        public AddDeviceWindow()
        {
            InitializeComponent();

            this.Title = Localization.GetLocalizedString("FILE_NEW_CAPTURE");
            OkButton.Content = Localization.GetLocalizedString("ADD_CAPTURE");
            CancelButton.Content = Localization.GetLocalizedString("CANCEL");

            SelectDevice.Content = Localization.GetLocalizedString("SELECT_DEVICE");
            LocalPC.Content = Localization.GetLocalizedString("LOCAL_PC");
            DefaultConsole.Content = Localization.GetLocalizedString("DEFAULT_CONSOLE");
            ConsoleAtIP.Text = Localization.GetLocalizedString("CONSOLE_AT_IPADDR");
            ConsoleAtHostname.Text = Localization.GetLocalizedString("CONSOLE_AT_HOSTNAME");
            GenericDevice.Content = Localization.GetLocalizedString("GENERIC_DEVICE");

            CaptureTypeLabel.Content = Localization.GetLocalizedString("SELECT_CAPTURE_TYPE");
            WebProxyLabel.Content = Localization.GetLocalizedString("CAPTURE_TYPE_PROXY");
            NetworkTraceLabel.Content = Localization.GetLocalizedString("CAPTURE_TYPE_NETCAP");
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (LocalPC.IsChecked.GetValueOrDefault())
            {
                SelectedDeviceType = DeviceType.LocalPC;
                SelectedDeviceName = Localization.GetLocalizedString("LOCAL_PC");
            }
            else if (CustomIpConsole.IsChecked.GetValueOrDefault())
            {
                if (!GDKXHelper.IsGDKXInstalled(false))
                {
                    Result = AddDeviceResult.Failed_NeedGDKX;
                }

                SelectedDeviceType = DeviceType.XboxConsole;
                SelectedDeviceName = ConsoleIpAddress.Text;
            }
            else if (CustomHostnameConsole.IsChecked.GetValueOrDefault())
            {
                if (!GDKXHelper.IsGDKXInstalled(false))
                {
                    Result = AddDeviceResult.Failed_NeedGDKX;
                }

                SelectedDeviceType = DeviceType.XboxConsole;
                SelectedDeviceName = ConsoleHostname.Text;
            }
            else if (DefaultConsole.IsChecked.GetValueOrDefault())
            {
                if (!GDKXHelper.IsGDKXInstalled(false))
                {
                    Result = AddDeviceResult.Failed_NeedGDKX;
                }

                SelectedDeviceType = DeviceType.XboxConsole;
                SelectedDeviceName = XboxClient.XboxClientConnection.GetDefaultConsoleAddress();

                // User may not have a default console
                if (SelectedDeviceName == null)
                {
                    Result = AddDeviceResult.Failed_NoDefaultConsole;
                }
            }
            else if (GenericDevice.IsChecked.GetValueOrDefault())
            {
                SelectedDeviceType = DeviceType.GenericProxyDevice;
                SelectedDeviceName = Localization.GetLocalizedString("GENERIC_DEVICE");
            }

            if (WebProxyLabel.IsChecked.GetValueOrDefault())
            {
                SelectedCaptureType = CaptureType.WebProxy;
            }
            else if (NetworkTraceLabel.IsChecked.GetValueOrDefault())
            {
                SelectedCaptureType = CaptureType.NetworkTrace;
            }

            DialogResult = Result == AddDeviceResult.Success;
            Close();
        }

        private void ConsoleIpAddress_TextChanged(object sender, TextChangedEventArgs e)
        {
            CustomIpConsole.IsChecked = true;
            OkButton.IsEnabled = IsConsoleIpAddressValid(ConsoleIpAddress.Text);
        }

        private void ConsoleHostname_TextChanged(object sender, TextChangedEventArgs e)
        {
            CustomHostnameConsole.IsChecked = true;
            OkButton.IsEnabled = IsConsoleHostnameValid(ConsoleHostname.Text);
        }

        private bool IsConsoleIpAddressValid(string ip)
        {
            // IPAddress accepts int64 IP addresses in string format as network order byte representations of IP's... so we have to validate the string first (e.g. "123" is a valid IP according to C#)
            return ip.Split('.').Length == 4 && IPAddress.TryParse(ip, out IPAddress ipx);
        }

        private bool IsConsoleHostnameValid(string hostname)
        {
            // Basic validation for hostname format
            return !string.IsNullOrWhiteSpace(hostname) && hostname.Length <= 255;
        }

        private void ResetOKButtonState(object sender, RoutedEventArgs e)
        {
            if (OkButton != null)
            {
                if (CustomIpConsole.IsChecked.GetValueOrDefault())
                {
                    OkButton.IsEnabled = IsConsoleIpAddressValid(ConsoleIpAddress.Text);
                }
                if (CustomHostnameConsole.IsChecked.GetValueOrDefault())
                {
                    OkButton.IsEnabled = IsConsoleHostnameValid(ConsoleHostname.Text);
                }
                else if (GenericDevice.IsChecked.GetValueOrDefault())
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
