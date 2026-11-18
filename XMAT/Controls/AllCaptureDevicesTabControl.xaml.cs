// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for AllCaptureDevicesTabControl.xaml
    /// </summary>
    public partial class AllCaptureDevicesTabControl : UserControl
    {
        public AllCaptureDevicesTabControl()
        {
            InitializeComponent();
            AddCloseTabShortcuts(this, CaptureDevicesTabs);
            AddTabReordering();
        }

        // Ctrl+F4 and Ctrl+W close the selected tab of TABS: the keyboard way to close a tab, since tab close buttons
        // are not tab stops.
        internal static void AddCloseTabShortcuts(UIElement owner, Selector tabs)
        {
            foreach (Key key in new[] { Key.F4, Key.W })
            {
                owner.InputBindings.Add(SelectedTabShortcut(ApplicationCommands.Close, key, ModifierKeys.Control, tabs));
            }
        }

        // A shortcut whose command takes the selected tab of TABS. The parameter is set as the selection changes rather
        // than bound to it: nothing in the product binds a command's parameter in code (ComposerRawRecordingTests).
        internal static KeyBinding SelectedTabShortcut(ICommand command, Key key, ModifierKeys modifiers, Selector tabs)
        {
            var shortcut = new KeyBinding(command, key, modifiers) { CommandParameter = tabs.SelectedItem };
            tabs.SelectionChanged += (_, _) => shortcut.CommandParameter = tabs.SelectedItem;
            return shortcut;
        }

        public static string ReadOnlyLabel
        {
            get { return Localization.GetLocalizedString("CAPTURE_DEVICES_TAB_READONLY"); }
        }

        public static string NewCaptureLabel => Localization.GetLocalizedString("FILE_NEW_CAPTURE");

        // The name of a tab's close button, with {0} for the tab's name.
        public static string CloseTabFormat => Localization.GetLocalizedString("TAB_CLOSE_NAME", "{0}");

        private void TabClose_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ICaptureDeviceContext deviceContext ||
                deviceContext.CaptureController == null)
            {
                return;
            }

            RaiseDeviceTabClosedEvent(deviceContext);
        }

        private void TabClose_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            var deviceContext = e.Parameter as ICaptureDeviceContext;
            e.CanExecute =
                deviceContext != null &&
                CaptureAppModel.AppModel.CanModifyCaptureData &&
                !(deviceContext?.CaptureController is XMAT.WebServiceCapture.WebServiceDeviceCaptureController { IsTransitioning: true }) &&
                !(deviceContext?.CaptureController is NetworkTraceCaptureController
                {
                    IsRunning: true
                });
        }

        //
        // DeviceTabClosed
        //
        public static readonly RoutedEvent DeviceTabClosedEvent = EventManager.RegisterRoutedEvent(
            "DeviceTabClosed", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(AllCaptureDevicesTabControl));

        public event RoutedEventHandler DeviceTabClosed
        {
            add { AddHandler(DeviceTabClosedEvent, value); }
            remove { RemoveHandler(DeviceTabClosedEvent, value); }
        }
        void RaiseDeviceTabClosedEvent(ICaptureDeviceContext captureDeviceContext)
        {
            RaiseEvent(new TabClosedRoutedEventArgs(DeviceTabClosedEvent, captureDeviceContext));
        }
    }

    public class TabClosedRoutedEventArgs : RoutedEventArgs
    {
        public ICaptureDeviceContext CaptureDeviceContext { get; set; }
        public TabClosedRoutedEventArgs(RoutedEvent re, ICaptureDeviceContext context) : base(re)
        {
            CaptureDeviceContext = context;
        }
    }
}
