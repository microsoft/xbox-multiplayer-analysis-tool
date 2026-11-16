// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XMAT.SharedInterfaces;

namespace XMAT
{
    // New Capture in the new look: the device to capture from and how to capture it, each chosen from cards. It opens on
    // This PC and Web Proxy. An option that doesn't work with the rest of the choice stays in view, dimmed, with a note
    // saying why, and a choice that stops working moves to one that does. The dialog is no taller than its screen and
    // stays on it: the cards scroll when they don't fit, and the dialog moves back onto the screen when it would run
    // past an edge, as when the address field appears. MainWindow opens it in the new look and AddDeviceWindow in the
    // original look; both report the choice as INewCaptureDialog.
    public partial class NewCaptureWindow : Window, INewCaptureDialog
    {
        internal enum Device
        {
            ThisPc,
            DefaultConsole,
            ConsoleByAddress,
            GenericDevice,
            HandheldOrRemotePc,
        }

        // How long typing pauses before an address that isn't valid shows its error.
        internal static readonly TimeSpan AddressPause = TimeSpan.FromMilliseconds(800);

        private readonly Func<string> _defaultConsoleAddress;
        private bool _addressErrorShown;

        public NewCaptureWindow()
            : this(GDKXHelper.IsGDKXInstalled(false), XboxClient.XboxClientConnection.GetDefaultConsoleAddress)
        {
        }

        // GDK INSTALLED makes the Xbox consoles available; DEFAULT CONSOLE ADDRESS looks up the default console when
        // the capture is added.
        internal NewCaptureWindow(bool gdkInstalled, Func<string> defaultConsoleAddress)
        {
            InitializeComponent();
            _defaultConsoleAddress = defaultConsoleAddress;
            AddressPauseTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = AddressPause };
            AddressPauseTimer.Tick += (_, _) =>
            {
                AddressPauseTimer.Stop();
                ShowAddressError();
            };
            Closed += (_, _) => AddressPauseTimer.Stop();

            Title = Text("FILE_NEW_CAPTURE");
            DeviceHeading.Text = Text("NEW_CAPTURE_DEVICE");
            CaptureTypeHeading.Text = Text("NEW_CAPTURE_CAPTURE_TYPE");
            Describe(ThisPcCard, "NEW_CAPTURE_THIS_PC");
            Describe(DefaultConsoleCard, "NEW_CAPTURE_DEFAULT_CONSOLE");
            Describe(ConsoleByAddressCard, "NEW_CAPTURE_CONSOLE_BY_ADDRESS");
            Describe(GenericDeviceCard, "NEW_CAPTURE_GENERIC_DEVICE");
            Describe(HandheldCard, "NEW_CAPTURE_HANDHELD");
            Describe(WebProxyCard, "NEW_CAPTURE_WEB_PROXY");
            Describe(NetworkTraceCard, "NEW_CAPTURE_NETWORK_TRACE");
            AddressLabel.Text = Text("NEW_CAPTURE_ADDRESS_LABEL");
            AddressExample.Text = Text("NEW_CAPTURE_ADDRESS_EXAMPLE");
            AddButton.Content = Text("NEW_CAPTURE_ADD");
            CancelButton.Content = Text("CANCEL");

            if (!gdkInstalled)
            {
                foreach (ChoiceCard console in new[] { DefaultConsoleCard, ConsoleByAddressCard })
                {
                    console.IsEnabled = false;
                    SetNote(console, "NEW_CAPTURE_CONSOLE_UNAVAILABLE", "InfoNoteIcon");
                }
            }

            ThisPcCard.IsChecked = true;
            WebProxyCard.IsChecked = true;
            UpdateChoices();
            Loaded += (_, _) => ThisPcCard.Focus();
            SourceInitialized += (_, _) =>
            {
                Rect workArea = WorkArea();
                if (!workArea.IsEmpty)
                {
                    MaxHeight = workArea.Height;
                }
            };
            SizeChanged += (_, _) => KeepOnScreen();
            ContentRendered += (_, _) => KeepOnScreen();
        }

        public AddDeviceWindow.AddDeviceResult Result { get; private set; } = AddDeviceWindow.AddDeviceResult.Success;

        public DeviceType SelectedDeviceType { get; private set; }

        public CaptureType SelectedCaptureType { get; private set; }

        public string SelectedDeviceName { get; private set; }

        internal DispatcherTimer AddressPauseTimer { get; }

        internal Device SelectedDevice =>
            DefaultConsoleCard.IsChecked == true ? Device.DefaultConsole :
            ConsoleByAddressCard.IsChecked == true ? Device.ConsoleByAddress :
            GenericDeviceCard.IsChecked == true ? Device.GenericDevice :
            HandheldCard.IsChecked == true ? Device.HandheldOrRemotePc :
            Device.ThisPc;

        private static string Text(string key) => Localization.GetLocalizedString(key);

        // The top of a window HEIGHT tall at TOP that keeps it within WORK AREA, the screen it is on: a window that runs
        // past the bottom moves up, and one that starts above the top moves down, so its title bar stays in view. A
        // window off the screen, above or below it, stays where it is.
        internal static double TopWithin(Rect workArea, double top, double height) =>
            top < workArea.Bottom && top + height > workArea.Top
                ? Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - height))
                : top;

        private void KeepOnScreen()
        {
            Rect workArea = WorkArea();
            if (!workArea.IsEmpty && !double.IsNaN(Top))
            {
                double top = TopWithin(workArea, Top, ActualHeight);
                if (top != Top)
                {
                    Top = top;
                }
            }
        }

        // The work area of the screen the window is on, without the taskbar, in the units of the window's position;
        // empty until the window has a handle.
        private Rect WorkArea()
        {
            if (PresentationSource.FromVisual(this) is not HwndSource { CompositionTarget: { } target } source)
            {
                return Rect.Empty;
            }

            var monitor = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            IntPtr screen = NativeMethods.MonitorFromWindow(source.Handle, NativeMethods.MonitorDefaultToNearest);
            if (!NativeMethods.GetMonitorInfo(screen, ref monitor))
            {
                return Rect.Empty;
            }

            Matrix fromDevice = target.TransformFromDevice;
            return new Rect(
                fromDevice.Transform(new Point(monitor.Work.Left, monitor.Work.Top)),
                fromDevice.Transform(new Point(monitor.Work.Right, monitor.Work.Bottom)));
        }

        private static void Describe(ChoiceCard card, string key)
        {
            card.Title = Text(key);
            card.Description = Text(key + "_DESCRIPTION");
        }

        private static void SetNote(ChoiceCard card, string key, string icon)
        {
            card.Note = Text(key);
            card.SetResourceReference(ChoiceCard.NoteIconProperty, icon);
        }

        private void Device_Checked(object sender, RoutedEventArgs e)
        {
            UpdateChoices();

            // Choosing the address card with the pointer goes on to its field; the keyboard stays on the cards, and
            // Tab reaches the field.
            if (sender == ConsoleByAddressCard && InputManager.Current.MostRecentInputDevice is not KeyboardDevice)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => AddressBox.Focus()));
            }
        }

        // Each capture type's note follows the device: what the capture needs there, or why it isn't available.
        private void UpdateChoices()
        {
            Device device = SelectedDevice;
            bool console = device is Device.DefaultConsole or Device.ConsoleByAddress;
            bool traceAvailable = device == Device.ThisPc || console;

            if (device == Device.ThisPc)
            {
                SetNote(WebProxyCard, "NEW_CAPTURE_NOTE_PC_PROXY", "CertificateNoteIcon");
                SetNote(NetworkTraceCard, "NEW_CAPTURE_NOTE_PC_TRACE", "AdministratorNoteIcon");
            }
            else if (console)
            {
                SetNote(WebProxyCard, "NEW_CAPTURE_NOTE_CONSOLE_PROXY", "RestartNoteIcon");
                SetNote(NetworkTraceCard, "NEW_CAPTURE_NOTE_CONSOLE_TRACE", "DownloadNoteIcon");
            }
            else
            {
                SetNote(WebProxyCard, "NEW_CAPTURE_NOTE_MANUAL_PROXY", "CertificateNoteIcon");
                SetNote(NetworkTraceCard,
                    device == Device.GenericDevice
                        ? "NEW_CAPTURE_TRACE_UNAVAILABLE_GENERIC"
                        : "NEW_CAPTURE_TRACE_UNAVAILABLE_HANDHELD",
                    "InfoNoteIcon");
            }

            if (!traceAvailable && NetworkTraceCard.IsChecked == true)
            {
                WebProxyCard.IsChecked = true;
            }

            NetworkTraceCard.IsEnabled = traceAvailable;
            UpdateAddress();
        }

        private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            AddressPauseTimer.Stop();
            if (AddressBox.Text.Length > 0 && !ConsoleAddress.IsValid(AddressBox.Text))
            {
                AddressPauseTimer.Start();
            }

            UpdateAddress();
        }

        private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            AddressPauseTimer.Stop();
            ShowAddressError();
        }

        private void ShowAddressError()
        {
            _addressErrorShown = AddressBox.Text.Length > 0 && !ConsoleAddress.IsValid(AddressBox.Text);
            UpdateAddress();
        }

        // The field's example while it's empty, its check or error, and whether the capture can be added. An error, once
        // shown, stays until the address is valid or the field is empty.
        private void UpdateAddress()
        {
            string address = AddressBox.Text;
            bool valid = ConsoleAddress.IsValid(address);
            if (valid || address.Length == 0)
            {
                _addressErrorShown = false;
            }

            AddressExample.Visibility = address.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            AddressValidIcon.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
            AddressErrorIcon.Visibility = _addressErrorShown ? Visibility.Visible : Visibility.Collapsed;
            AddressErrorOutline.Visibility = AddressErrorIcon.Visibility;
            AddressError.Text = _addressErrorShown ? Text("NEW_CAPTURE_ADDRESS_ERROR") : string.Empty;
            AutomationProperties.SetHelpText(AddressBox, _addressErrorShown ? AddressError.Text : AddressExample.Text);
            AddButton.IsEnabled = ConsoleByAddressCard.IsChecked != true || valid;
        }

        private void CaptureType_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && sender is ChoiceCard { IsEnabled: true, IsChecked: true } &&
                AddButton.IsEnabled)
            {
                e.Handled = true;
                AddCapture();
            }
        }

        private void AddCapture_Click(object sender, RoutedEventArgs e) => AddCapture();

        // The device's type and name, as the capture's tab names it, and the capture type. A default console that
        // isn't set fails, and MainWindow says so.
        private void AddCapture()
        {
            if (!AddButton.IsEnabled)
            {
                return;
            }

            switch (SelectedDevice)
            {
                case Device.DefaultConsole:
                    SelectedDeviceType = DeviceType.XboxConsole;
                    SelectedDeviceName = _defaultConsoleAddress();
                    if (string.IsNullOrEmpty(SelectedDeviceName))
                    {
                        Result = AddDeviceWindow.AddDeviceResult.Failed_NoDefaultConsole;
                    }

                    break;
                case Device.ConsoleByAddress:
                    SelectedDeviceType = DeviceType.XboxConsole;
                    SelectedDeviceName = AddressBox.Text;
                    break;
                case Device.GenericDevice:
                    SelectedDeviceType = DeviceType.GenericProxyDevice;
                    SelectedDeviceName = Text("GENERIC_DEVICE");
                    break;
                case Device.HandheldOrRemotePc:
                    // A generic device to XMAT, named for what it is.
                    SelectedDeviceType = DeviceType.GenericProxyDevice;
                    SelectedDeviceName = Text("WINDOWS_HANDHELD_REMOTE_DEVICE");
                    break;
                default:
                    SelectedDeviceType = DeviceType.LocalPC;
                    SelectedDeviceName = Text("LOCAL_PC");
                    break;
            }

            SelectedCaptureType = NetworkTraceCard.IsChecked == true ? CaptureType.NetworkTrace : CaptureType.WebProxy;
            DialogResult = Result == AddDeviceWindow.AddDeviceResult.Success;
        }

        private static class NativeMethods
        {
            internal const uint MonitorDefaultToNearest = 2;

            [StructLayout(LayoutKind.Sequential)]
            internal struct ScreenRect
            {
                internal int Left;
                internal int Top;
                internal int Right;
                internal int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct MonitorInfo
            {
                internal int Size;
                internal ScreenRect Monitor;
                internal ScreenRect Work;
                internal uint Flags;
            }

            [DllImport("user32.dll")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

            [DllImport("user32.dll")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        }
    }

    // What New Capture chose, as both looks' New Capture windows report it.
    internal interface INewCaptureDialog
    {
        AddDeviceWindow.AddDeviceResult Result { get; }

        DeviceType SelectedDeviceType { get; }

        CaptureType SelectedCaptureType { get; }

        string SelectedDeviceName { get; }
    }
}
