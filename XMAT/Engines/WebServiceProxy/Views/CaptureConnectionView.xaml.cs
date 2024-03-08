// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Interaction logic for CaptureConnectionView.xaml
    /// </summary>
    public partial class CaptureConnectionView : UserControl
    {
        private bool _notAtBottom;

        public bool AutoScroll
        {
            get { return (bool)this.GetValue(AutoScrollProperty); }
            set { this.SetValue(AutoScrollProperty, value); }
        }
        public static readonly DependencyProperty AutoScrollProperty =
            DependencyProperty.Register("AutoScroll", typeof(bool), typeof(CaptureConnectionView), new PropertyMetadata(true));

        public static string StartTraceText
        {
            get { return Localization.GetLocalizedString("WEBCAP_START"); }
        }

        public static string StopTraceText
        {
            get { return Localization.GetLocalizedString("WEBCAP_STOP"); }
        }

        public CaptureConnectionView()
        {
            InitializeComponent();

            EnableButton.Content = Localization.GetLocalizedString("WEBCAP_ENABLE");
            DisableButton.Content = Localization.GetLocalizedString("WEBCAP_DISABLE");
            CheckButton.Content = Localization.GetLocalizedString("WEBCAP_CHECK");
            AnalyzeButton.Content = Localization.GetLocalizedString("WEBCAP_ANALYZE");
            ClearButton.Content = Localization.GetLocalizedString("WEBCAP_CLEAR");
            AutoScrollButton.Content = Localization.GetLocalizedString("WEBCAP_AUTOSCROLL");
            FiltersText.Text = Localization.GetLocalizedString("GENERIC_FILTERS");

            StatusFilters.FilterDesc = Localization.GetLocalizedString("WEBCAP_FILTER_STATUS");
            HostFilters.FilterDesc = Localization.GetLocalizedString("WEBCAP_FILTER_HOST");
            MethodFilters.FilterDesc = Localization.GetLocalizedString("WEBCAP_FILTER_METHOD");

            DetailsText.Text = Localization.GetLocalizedString("PROXY_DETAILS");
            PortText.Text = String.Format("{0}: ", Localization.GetLocalizedString("PROXY_PORT"));

            ReqestResponseTab.Header = Localization.GetLocalizedString("WEBCAP_REQRES_TAB");
            ScriptEditorTab.Header = Localization.GetLocalizedString("WEBCAP_EDITOR_TAB");

            RequestViewControl.Title = Localization.GetLocalizedString("WEBCAP_REQRES_TITLE_REQUEST");
            ResponseViewControl.Title = Localization.GetLocalizedString("WEBCAP_REQRES_TITLE_RESPONSE");

            RequestIdLabel.Text = Localization.GetLocalizedString("WEBCAP_REQRES_LABEL_REQID");
            TimeLabel.Text = Localization.GetLocalizedString("WEBCAP_REQRES_LABEL_TIME");
            DurationLabel.Text = Localization.GetLocalizedString("WEBCAP_REQRES_LABEL_DURATION");

            ConnectionList.Columns[0].Header = Localization.GetLocalizedString("WEBCAP_HEADER_NUMBER");
            ConnectionList.Columns[1].Header = Localization.GetLocalizedString("WEBCAP_HEADER_STATUS");
            ConnectionList.Columns[2].Header = Localization.GetLocalizedString("WEBCAP_HEADER_METHOD");
            ConnectionList.Columns[3].Header = Localization.GetLocalizedString("WEBCAP_HEADER_SCHEME");
            ConnectionList.Columns[4].Header = Localization.GetLocalizedString("WEBCAP_HEADER_HOST");
            ConnectionList.Columns[5].Header = Localization.GetLocalizedString("WEBCAP_HEADER_PATH");
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            ((DataContext as ICaptureDeviceContext).CaptureController
                as WebServiceDeviceCaptureController).PropertyChanged += CaptureConnectionView_ControllerChanged;
            (ConnectionList.Items as INotifyCollectionChanged).CollectionChanged += CaptureConnectionView_CollectionChanged;

            ConnectionList.Items.Filter =
                (item) =>
                {
                    bool includeStatus = true;
                    bool includeHost = true;
                    bool includeMethod = true;

                    var model = item as ProxyConnectionModel;

                    if (StatusFilters.IsFilterEnabled)
                        includeStatus = Filter(model.Status, StatusFilters.ItemsSource as ObservableCollection<CheckedListItem>);

                    if (HostFilters.IsFilterEnabled)
                        includeHost = Filter(model.Host, HostFilters.ItemsSource as ObservableCollection<CheckedListItem>);

                    if (MethodFilters.IsFilterEnabled)
                        includeMethod = Filter(model.Method, MethodFilters.ItemsSource as ObservableCollection<CheckedListItem>);

                    return includeStatus && includeHost && includeMethod;
                };
        }

        private void CaptureConnectionView_ControllerChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == @"SelectedConnectionModel")
            {
                WebServiceDeviceCaptureController controller =
                    (sender as WebServiceDeviceCaptureController);

                if (controller.SelectedConnectionModel != null)
                {
                    ConnectionList.ScrollIntoView(controller.SelectedConnectionModel);
                }
            }
        }

        private void CaptureConnectionView_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // we want to update the filters as items changed
            // hook up the PropertyChanged event so we can update the filter list
            // every time a ProxyConnectionModel updates
            // mostly, this fixes the Status filter since that data comes in after the object gets added
            // to the observable collection
            if (e.NewItems != null)
            {
                foreach (ProxyConnectionModel item in e.NewItems)
                {
                    item.PropertyChanged += ProxyConnectionModel_PropertyChanged;
                    UpdateFilters(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (ProxyConnectionModel item in e.OldItems)
                    item.PropertyChanged -= ProxyConnectionModel_PropertyChanged;
            }

            if (AutoScroll && ConnectionList.Items.Count > 0 && !_notAtBottom)
                ConnectionList.ScrollIntoView(ConnectionList.Items[^1]);
        }

        private void ProxyConnectionModel_PropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            UpdateFilters(sender as ProxyConnectionModel);
        }

        private bool Filter(string text, ObservableCollection<CheckedListItem> list)
        {
            var include = list?.Where(item => item?.Text == text).FirstOrDefault();
            return (include != null && include.IsChecked);
        }

        private void OnFiltersChanged(object sender, RoutedEventArgs e)
        {
            // force all items to be re-filtered using the Filter delegate
            CollectionViewSource.GetDefaultView(ConnectionList.ItemsSource).Refresh();
        }

        private void UpdateFilter(string text, ObservableCollection<CheckedListItem> list)
        {
            if (!string.IsNullOrEmpty(text) && !list.Where(cli => cli.Text == text).Any())
                list.Add(new CheckedListItem(text));
        }

        private void UpdateFilters(ProxyConnectionModel item)
        {
            UpdateFilter(item.Status, StatusFilters.ItemsSource as ObservableCollection<CheckedListItem>);
            UpdateFilter(item.Host, HostFilters.ItemsSource as ObservableCollection<CheckedListItem>);
            UpdateFilter(item.Method, MethodFilters.ItemsSource as ObservableCollection<CheckedListItem>);
        }

        private async void EnableDevice_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            var wsdcc = captureDeviceContext.CaptureController as WebServiceDeviceCaptureController;
            switch (captureDeviceContext.DeviceType)
            {
                case DeviceType.LocalPC:
                    {
                        wsdcc.EnableProxyingPC();
                        wsdcc.StartProxy(false);
                        break;
                    }

                case DeviceType.GenericProxyDevice:
                    {
                        wsdcc.StartProxy(true);
                        break;
                    }

                case DeviceType.XboxConsole:
                    {
                        string proxyHostIpAddress = "";
                        bool shouldEnableProxy = false;

                        IPAddress destinationAddress = await PublicUtilities.ResolveIP4AddressAsync(wsdcc.DeviceName);
                        if(destinationAddress == null)
                        {
                            MessageBox.Show(Localization.GetLocalizedString("DNS_RESOLVE_ERROR_MESSAGE", wsdcc.DeviceName), Localization.GetLocalizedString("DNS_RESOLVE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        IEnumerable<IPAddress> sourceAddresses = PublicUtilities.GetMyIp4Addresses();

                        if (sourceAddresses.Count() == 0)
                        {
                            // TODO: show a message box saying that we have no valid
                            // source address
                            break;
                        }
                        else if (sourceAddresses.Count() == 1)
                        {
                            proxyHostIpAddress = sourceAddresses.First().ToString();
                            shouldEnableProxy = true;
                        }
                        else
                        {
                            // find the first source address from which we can ping
                            // the console device's IP address directly

                            var sourceAddressModels = new List<PingableSourceAddressModel>();
                            foreach (var sourceAddress in sourceAddresses)
                            {
                                sourceAddressModels.Add(
                                    new PingableSourceAddressModel(
                                        sourceAddress,
                                        destinationAddress,
                                        sourceAddressModels.Count == 0));
                            }

                            var selectorWindow = new SourceAddressWindow
                            {
                                Owner = Application.Current.MainWindow,
                                DataContext = sourceAddressModels
                            };

                            // show the list of available source addresses
                            var confirmed = selectorWindow.ShowDialog() ?? false;

                            if (confirmed)
                            {
                                // TODO: do we need to explicitly set the source address on
                                // the proxy engine itself?
                                proxyHostIpAddress = selectorWindow.SelectedSourceAddress.SourceAddress.ToString();
                                shouldEnableProxy = true;
                            }
                        }

                        if (shouldEnableProxy)
                        {
                            bool bWasEnabled = await wsdcc.EnableProxyingXbox(proxyHostIpAddress);

                            if (bWasEnabled)
                            {
                                wsdcc.StartProxy(false);
                            }
                        }
                        // TODO: if we 've gotten this far, then that means we
                        // were not able to find a suitable source address to use
                        // and we should message the user
                        break;
                    }

                default:
                    throw new NotImplementedException(
                $"Device type of {captureDeviceContext.DeviceType} for {captureDeviceContext.DeviceName} is not supported.");
            }
        }

        private async void DisableDevice_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            var wsdcc = captureDeviceContext.CaptureController as WebServiceDeviceCaptureController;
            await wsdcc.DisableProxying();
            wsdcc.StopProxy();
        }

        private async void CheckDevice_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            var wsdcc = captureDeviceContext.CaptureController as WebServiceDeviceCaptureController;

            if (!GDKXHelper.IsGDKXInstalled(true)) { return; }

            string strProxyStatusMessageLocKey = "PROXY_CHECK_STATUS_MESSAGE_CONSOLE_UNREACHABLE";

            XboxClient.XboxClientConnection.EProxyEnabledCheckResult proxyCheckResult = await wsdcc.IsProxyEnabled();
            if (proxyCheckResult == XboxClient.XboxClientConnection.EProxyEnabledCheckResult.ConsoleUnreachable)
            {
                strProxyStatusMessageLocKey = "PROXY_CHECK_STATUS_MESSAGE_CONSOLE_UNREACHABLE";
            }
            else if (proxyCheckResult == XboxClient.XboxClientConnection.EProxyEnabledCheckResult.ProxyDisabled)
            {
                strProxyStatusMessageLocKey = "PROXY_CHECK_STATUS_MESSAGE_DISABLED";
            }
            else if (proxyCheckResult == XboxClient.XboxClientConnection.EProxyEnabledCheckResult.ProxyEnabled)
            {
                strProxyStatusMessageLocKey = "PROXY_CHECK_STATUS_MESSAGE_ENABLED";
            }
            else if (proxyCheckResult == XboxClient.XboxClientConnection.EProxyEnabledCheckResult.ProxyingGenericDevice)
            {
                wsdcc.ShowGenericProxyDetails();
                return;
            }

            MessageBox.Show(XMAT.Localization.GetLocalizedString(strProxyStatusMessageLocKey), XMAT.Localization.GetLocalizedString("PROXY_CHECK_STATUS_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void StartStopCapture_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var stop = e.Parameter as bool?;
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            var wsdcc = captureDeviceContext.CaptureController as WebServiceDeviceCaptureController;

            if (captureDeviceContext?.DeviceType == DeviceType.XboxConsole)
            {
                if (!GDKXHelper.IsGDKXInstalled(true)) { return; }
            }

            if (stop.GetValueOrDefault())
            {
                if (captureDeviceContext?.DeviceType == DeviceType.LocalPC)
                {
                    wsdcc.EnableProxyingPC();
                }
                wsdcc.StartProxy(captureDeviceContext?.DeviceType == DeviceType.GenericProxyDevice);
            }
            else
            {
                wsdcc.StopProxy();
                if (captureDeviceContext?.DeviceType == DeviceType.LocalPC)
                {
                    await wsdcc.DisableProxying();
                }
            }
        }

        private void StartStopCapture_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            if (captureDeviceContext != null)
            {
                e.CanExecute = !captureDeviceContext.IsReadOnly;
            }
        }

        private void Enable_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            if (captureDeviceContext != null &&
                captureDeviceContext.DeviceType == DeviceType.XboxConsole &&
                !captureDeviceContext.IsReadOnly)
            {
                e.CanExecute = true;
            }
            else
            {
                e.CanExecute = false;
            }
        }

        private void Disable_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            var captureDeviceContext = this.DataContext as ICaptureDeviceContext;
            if (captureDeviceContext != null &&
                captureDeviceContext.DeviceType == DeviceType.XboxConsole &&
                !captureDeviceContext.IsReadOnly)
            {
                e.CanExecute = true;
            }
            else
            {
                e.CanExecute = false;
            }
        }

        private void ConnectionList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // we are at the bottom if the ExtentHeight + ViewPortHeight ~= VerticalOffset
            // if they are not ~=, we are not at the bottom, so above, we won't auto scroll
            _notAtBottom = (e.VerticalOffset + 1) < e.ExtentHeight - e.ViewportHeight;
        }
    }
}
