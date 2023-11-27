// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
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

        private async void TabClose_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var deviceContext = e.Parameter as ICaptureDeviceContext;

            if (await deviceContext.CaptureController.CanCloseAsync())
            {
                RaiseDeviceTabClosedEvent(deviceContext);
            }
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
