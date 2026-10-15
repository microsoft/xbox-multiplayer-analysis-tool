// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
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
        }

        public static string ReadOnlyLabel
        {
            get { return Localization.GetLocalizedString("CAPTURE_DEVICES_TAB_READONLY"); }
        }

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
                CaptureAppModel.AppModel.CanModifyCaptureData &&
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
