// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Interaction logic for CaptureConnectionView.xaml
    /// </summary>
    public partial class CaptureConnectionView : UserControl
    {
        private static readonly TimeSpan StopDialogDelay =
            TimeSpan.FromMilliseconds(500);
        private bool _notAtBottom;
        private readonly Predicate<object> _connectionFilter;
        private readonly HashSet<ProxyConnectionModel> _observedConnections = new(ReferenceEqualityComparer.Instance);
        private readonly CaptureFilterIndex _statusValues = new();
        private readonly CaptureFilterIndex _hostValues = new();
        private readonly CaptureFilterIndex _methodValues = new();
        private WebServiceDeviceCaptureController _attachedController;
        private ProxyConnectionsCollection _attachedConnections;
        private INotifyCollectionChanged _attachedGridItems;
        private DispatcherOperation _pendingFilterRefresh;

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
            _connectionFilter = IncludeConnection;
            InitializeComponent();

            EnableButton.Content = Localization.GetLocalizedString("WEBCAP_ENABLE");
            DisableButton.Content = Localization.GetLocalizedString("WEBCAP_DISABLE");
            DiscardPortButton.Content = Localization.GetLocalizedString("PROXY_PORT_DISCARD");
            CancelPortButton.Content = Localization.GetLocalizedString("PROXY_PORT_CANCEL_APPLY");
            CheckButton.Content = Localization.GetLocalizedString("WEBCAP_CHECK");
            AnalyzeButton.Content = Localization.GetLocalizedString("WEBCAP_ANALYZE");
            AnalyzeButton.ToolTip = Localization.GetLocalizedString("ANALYZE_CAPTURE_REQUIRES_STOP_TOOLTIP");
            ClearButton.Content = Localization.GetLocalizedString("WEBCAP_CLEAR");
            AutoScrollButton.Content = Localization.GetLocalizedString("WEBCAP_AUTOSCROLL");
            FiltersText.Text = Localization.GetLocalizedString("GENERIC_FILTERS");

            StatusFilters.FilterDesc = Localization.GetLocalizedString("WEBCAP_FILTER_STATUS");
            HostFilters.FilterDesc = Localization.GetLocalizedString("WEBCAP_FILTER_HOST");
            MethodFilters.FilterDesc = Localization.GetLocalizedString("WEBCAP_FILTER_METHOD");

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
            AttachContext();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e) => DetachContext();

        private void UserControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsLoaded)
            {
                AttachContext();
            }
            else
            {
                DetachContext();
            }
            CommandManager.InvalidateRequerySuggested();
        }

        private void AttachContext()
        {
            if (!TryGetCaptureContext(DataContext, out var context, out var controller))
            {
                DetachContext();
                return;
            }
            CheckButton.Content = Localization.GetLocalizedString(
                context.DeviceType == DeviceType.GenericProxyDevice ? "PROXY_SETUP_DETAILS" : "WEBCAP_CHECK");
            if (ReferenceEquals(controller, _attachedController) &&
                ReferenceEquals(controller.ProxyConnections, _attachedConnections))
            {
                return;
            }
            DetachContext();
            _attachedController = controller;
            controller.PropertyChanged += CaptureConnectionView_ControllerChanged;
            UpdateFilterSources();
            AttachConnections(controller.ProxyConnections);
            _attachedGridItems = ConnectionList.Items;
            _attachedGridItems.CollectionChanged += CaptureConnectionView_CollectionChanged;
            RequestFilterRefresh();
        }

        private void DetachContext()
        {
            _pendingFilterRefresh?.Abort();
            _pendingFilterRefresh = null;
            if (_attachedGridItems != null)
            {
                _attachedGridItems.CollectionChanged -= CaptureConnectionView_CollectionChanged;
                _attachedGridItems = null;
            }
            if (_attachedController != null)
            {
                _attachedController.PropertyChanged -= CaptureConnectionView_ControllerChanged;
                _attachedController = null;
            }
            DetachConnections();
            if (ConnectionList != null && ReferenceEquals(ConnectionList.Items.Filter, _connectionFilter))
            {
                ConnectionList.Items.Filter = null;
            }
            UpdateFilterSources();
        }

        private void AttachConnections(ProxyConnectionsCollection connections)
        {
            DetachConnections();
            _attachedConnections = connections;
            if (connections != null)
            {
                connections.CollectionChanged += CaptureConnections_CollectionChanged;
                foreach (var model in connections)
                {
                    ObserveConnection(model);
                }
            }
        }

        private void DetachConnections()
        {
            if (_attachedConnections != null)
            {
                _attachedConnections.CollectionChanged -= CaptureConnections_CollectionChanged;
                _attachedConnections = null;
            }
            DetachObservedItems();
        }

        private void DetachObservedItems()
        {
            foreach (var model in _observedConnections)
            {
                model.PropertyChanged -= ProxyConnectionModel_PropertyChanged;
            }
            _observedConnections.Clear();
        }

        private void ObserveConnection(ProxyConnectionModel model)
        {
            if (_observedConnections.Add(model))
            {
                model.PropertyChanged += ProxyConnectionModel_PropertyChanged;
                UpdateFilters(model);
            }
        }

        private void CaptureConnections_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _attachedConnections))
            {
                return;
            }
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                DetachObservedItems();
                foreach (var model in _attachedConnections)
                {
                    ObserveConnection(model);
                }
                return;
            }
            if (e.OldItems != null)
            {
                foreach (ProxyConnectionModel model in e.OldItems)
                {
                    if (_observedConnections.Remove(model))
                    {
                        model.PropertyChanged -= ProxyConnectionModel_PropertyChanged;
                    }
                }
            }
            if (e.NewItems != null)
            {
                foreach (ProxyConnectionModel model in e.NewItems)
                {
                    ObserveConnection(model);
                }
            }
        }

        private bool IncludeConnection(object item)
        {
            if (item is not ProxyConnectionModel model)
            {
                return false;
            }
            return (!StatusFilters.IsFilterEnabled ||
                _statusValues.IsChecked(model.Status)) &&
                (!HostFilters.IsFilterEnabled ||
                _hostValues.IsChecked(model.Host)) &&
                (!MethodFilters.IsFilterEnabled ||
                _methodValues.IsChecked(model.Method));
        }

        private void CaptureConnectionView_ControllerChanged(object sender, PropertyChangedEventArgs args)
        {
            if (!ReferenceEquals(sender, _attachedController))
            {
                return;
            }
            if (string.IsNullOrEmpty(args.PropertyName) ||
                args.PropertyName is nameof(WebServiceDeviceCaptureController.ProxyConnections) or
                    nameof(WebServiceDeviceCaptureController.StatusFilterList) or
                    nameof(WebServiceDeviceCaptureController.HostFilterList) or
                    nameof(WebServiceDeviceCaptureController.MethodFilterList))
            {
                UpdateFilterSources();
                if (!ReferenceEquals(_attachedConnections, _attachedController.ProxyConnections))
                {
                    AttachConnections(_attachedController.ProxyConnections);
                }
                RequestFilterRefresh();
            }
            if (string.IsNullOrEmpty(args.PropertyName) ||
                args.PropertyName == nameof(WebServiceDeviceCaptureController.SelectedConnectionModel))
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
            if (AutoScroll && ConnectionList.Items.Count > 0 && !_notAtBottom)
                ConnectionList.ScrollIntoView(ConnectionList.Items[^1]);
        }

        private void ProxyConnectionModel_PropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (sender is not ProxyConnectionModel model || !_observedConnections.Contains(model))
            {
                return;
            }
            if (string.IsNullOrEmpty(args.PropertyName) ||
                args.PropertyName is nameof(ProxyConnectionModel.Status) or
                    nameof(ProxyConnectionModel.Host) or nameof(ProxyConnectionModel.Method))
            {
                UpdateFilters(model);
            }
        }

        private void OnFiltersChanged(object sender, RoutedEventArgs e)
        {
            RequestFilterRefresh();
        }

        private void RequestFilterRefresh()
        {
            if (_attachedController != null &&
                _pendingFilterRefresh?.Status != DispatcherOperationStatus.Pending)
            {
                _pendingFilterRefresh = Dispatcher.BeginInvoke(
                    DispatcherPriority.Background, new Action(ApplyConnectionFilter));
            }
        }

        private void ApplyConnectionFilter()
        {
            _pendingFilterRefresh = null;
            if (_attachedController == null || ConnectionList.ItemsSource == null)
            {
                return;
            }
            var filter = StatusFilters.IsFilterEnabled || HostFilters.IsFilterEnabled || MethodFilters.IsFilterEnabled
                ? _connectionFilter
                : null;
            if (!ReferenceEquals(ConnectionList.Items.Filter, filter))
            {
                ConnectionList.Items.Filter = filter;
            }
            else if (filter != null)
            {
                CollectionViewSource.GetDefaultView(ConnectionList.ItemsSource).Refresh();
            }
        }

        private void UpdateFilterSources()
        {
            _statusValues.SetSource(_attachedController?.StatusFilterList);
            _hostValues.SetSource(_attachedController?.HostFilterList);
            _methodValues.SetSource(_attachedController?.MethodFilterList);
        }

        private void UpdateFilters(ProxyConnectionModel item)
        {
            UpdateFilterSources();
            var controller = _attachedController;
            _statusValues.EnsureValue(item.Status);
            if (!ReferenceEquals(controller, _attachedController) || !_observedConnections.Contains(item))
            {
                return;
            }
            _hostValues.EnsureValue(item.Host);
            if (!ReferenceEquals(controller, _attachedController) || !_observedConnections.Contains(item))
            {
                return;
            }
            _methodValues.EnsureValue(item.Method);
        }

        private async void ChangePort_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (TryGetCaptureContext(DataContext, out var context, out var controller))
            {
                await ShowPortChangeAsync(context, controller);
            }
        }

        internal static bool CanChangeProxyPort(object dataContext) =>
            TryGetCaptureContext(dataContext, out var context, out var controller) &&
            !context.IsReadOnly && controller.CanBeginTransition &&
            CaptureAppModel.AppModel.CanModifyCaptureData;

        internal static async Task ShowPortChangeAsync(
            ICaptureDeviceContext context, WebServiceDeviceCaptureController controller)
        {
            if (context.IsReadOnly || !controller.CanBeginTransition ||
                !CaptureAppModel.AppModel.CanModifyCaptureData)
            {
                return;
            }
            try
            {
                var dialog = new ProxyPortChangeWindow(controller.Port, controller.PendingPort, context.DeviceType)
                {
                    Owner = Application.Current.MainWindow
                };
                if (dialog.ShowDialog() != true)
                {
                    return;
                }
                if (!CaptureAppModel.AppModel.CanModifyCaptureData)
                {
                    throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_OPERATION_BUSY"));
                }
                controller.QueuePortChange(dialog.SelectedPort, SavePortSettings);
                if (!dialog.ApplyNow || !controller.HasPendingPort)
                {
                    return;
                }
                if (context.DeviceType == DeviceType.XboxConsole && !GDKXHelper.IsGDKXInstalled(true))
                {
                    return;
                }
                var manual = context.DeviceType == DeviceType.GenericProxyDevice;
                if (manual && MessageBox.Show(
                    Localization.GetLocalizedString("PROXY_PORT_MANUAL_CONFIRM", controller.Port, controller.PendingPort),
                    Localization.GetLocalizedString("PROXY_PORT_CHANGE"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }
                var host = context.DeviceType switch
                {
                    DeviceType.LocalPC => "127.0.0.1",
                    DeviceType.GenericProxyDevice => PublicUtilities.GetMyIp4Addresses().FirstOrDefault()?.ToString()
                        ?? throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_NO_LOCAL_ADDRESS")),
                    _ => await SelectProxyHostAsync(controller.DeviceName)
                };
                if (host == null)
                {
                    return;
                }
                await controller.ApplyPortChangeAsync(host, SavePortSettings, manual);
                if (manual)
                {
                    controller.ShowGenericProxyDetails();
                }
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
            {
                // The controller retains the phase-specific recovery status.
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                ShowProxyOperationError(ex);
            }
            finally
            {
                controller.RaisePropertyChange(nameof(controller.IsRunning));
            }
        }

        private static void SavePortSettings() => CaptureAppSettings.SerializeChecked(CaptureAppModel.AppModel);

        private void DiscardPort_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (TryGetCaptureContext(DataContext, out var context, out var controller) &&
                !context.IsReadOnly && CaptureAppModel.AppModel.CanModifyCaptureData)
            {
                try
                {
                    controller.DiscardPortChange(SavePortSettings);
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    ShowProxyOperationError(ex);
                }
            }
        }

        private void CancelPort_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (TryGetCaptureContext(DataContext, out _, out var controller))
            {
                controller.CancelPortChange();
            }
        }

        private static async Task<string> SelectProxyHostAsync(string device)
        {
            var sourceAddresses = PublicUtilities.GetMyIp4Addresses().ToArray();
            if (sourceAddresses.Length == 0)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_NO_LOCAL_ADDRESS"));
            }
            if (sourceAddresses.Length == 1)
            {
                return sourceAddresses[0].ToString();
            }
            var destinationAddress = await PublicUtilities.ResolveIP4AddressAsync(device);
            if (destinationAddress == null)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("DNS_RESOLVE_ERROR_MESSAGE", device));
            }
            var models = sourceAddresses.Select((address, index) =>
                new PingableSourceAddressModel(address, destinationAddress, index == 0)).ToList();
            var selector = new SourceAddressWindow
            {
                Owner = Application.Current.MainWindow,
                DataContext = models
            };
            return selector.ShowDialog() == true ? selector.SelectedSourceAddress.SourceAddress.ToString() : null;
        }

        private async void EnableDevice_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!TryGetCaptureContext(DataContext, out var captureDeviceContext, out var wsdcc) ||
                !wsdcc.CanBeginTransition)
            {
                return;
            }

            if (wsdcc.HasPendingPort)
            {
                await ShowPortChangeAsync(captureDeviceContext, wsdcc);
                return;
            }

            if (!CaptureAppModel.AppModel.TryBeginCaptureDataOperation(out var operationLease))
            {
                return;
            }

            using var operationLeaseScope = operationLease;
            if (!wsdcc.CanBeginTransition)
            {
                return;
            }

            using var transitionLease = wsdcc.BeginTransition();
            try
            {
                switch (captureDeviceContext.DeviceType)
                {
                    case DeviceType.LocalPC:
                    {
                        // The listener must be confirmed before the system proxy points at it.
                        await wsdcc.StartProxyAsync(false);
                        await wsdcc.EnableDeviceProxyingOrRollBackAsync(
                            async () => { await Task.Run(wsdcc.EnableProxyingPC); return true; });
                        break;
                    }

                    case DeviceType.GenericProxyDevice:
                    {
                        await wsdcc.StartProxyAsync(true);
                        break;
                    }

                    case DeviceType.XboxConsole:
                    {
                        var proxyHostIpAddress = await SelectProxyHostAsync(wsdcc.DeviceName);
                        if (proxyHostIpAddress != null)
                        {
                            await wsdcc.StartProxyAsync(false);
                            await wsdcc.EnableDeviceProxyingOrRollBackAsync(
                                () => wsdcc.EnableProxyingXbox(proxyHostIpAddress));
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
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                ShowProxyOperationError(ex);
            }
        }

        private async void DisableDevice_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanStopCaptures)
            {
                return;
            }

            if (!TryGetCaptureContext(DataContext, out _, out var wsdcc) ||
                wsdcc.IsTransitioning)
            {
                return;
            }

            if (!CaptureAppModel.AppModel.TryBeginCaptureDataOperation(
                    out var operationLease))
            {
                return;
            }

            using (operationLease)
            {
                if (wsdcc.IsTransitioning)
                {
                    return;
                }

                using var transitionLease = wsdcc.BeginDeviceTransition();
                try
                {
                    if (!await wsdcc.DisableProxying())
                    {
                        return;
                    }
                    await wsdcc.StopProxyAsync();
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    ShowProxyOperationError(ex);
                }
            }
        }

        private async void CheckDevice_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!CanCheckDevice(DataContext) ||
                !TryGetCaptureContext(DataContext, out var captureDeviceContext, out var wsdcc))
            {
                return;
            }

            try
            {
                if (captureDeviceContext.DeviceType == DeviceType.GenericProxyDevice)
                {
                    wsdcc.ShowGenericProxyDetails();
                    return;
                }
                if (captureDeviceContext.DeviceType == DeviceType.XboxConsole && !GDKXHelper.IsGDKXInstalled(true))
                {
                    return;
                }
                var configuration = await wsdcc.CreateDeviceConfiguration().ReadAsync();
                var message = configuration.State switch
                {
                    ProxyConfigurationState.Enabled => Localization.GetLocalizedString(
                        "PROXY_CHECK_ENDPOINT", configuration.Endpoint, wsdcc.Port) +
                        (wsdcc.HasPendingPort ? "\n" + Localization.GetLocalizedString("PROXY_PORT_PENDING", wsdcc.PendingPort) : string.Empty),
                    ProxyConfigurationState.Disabled => Localization.GetLocalizedString("PROXY_CHECK_STATUS_MESSAGE_DISABLED"),
                    ProxyConfigurationState.Unreachable => Localization.GetLocalizedString("PROXY_CHECK_STATUS_MESSAGE_CONSOLE_UNREACHABLE"),
                    _ => Localization.GetLocalizedString("PROXY_CHECK_STATUS_MESSAGE_UNKNOWN", configuration.Detail)
                };
                MessageBox.Show(message, Localization.GetLocalizedString("PROXY_CHECK_STATUS_TITLE"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                ShowProxyOperationError(ex);
            }
        }

        private async void StartStopCapture_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!TryGetCaptureContext(DataContext, out var captureDeviceContext, out var wsdcc))
            {
                return;
            }

            var start = e.Parameter as bool?;

            try
            {
                if (start.GetValueOrDefault())
                {
                    if (wsdcc.HasPendingPort)
                    {
                        await ShowPortChangeAsync(captureDeviceContext, wsdcc);
                        return;
                    }
                    if (!CaptureAppModel.AppModel.TryBeginCaptureDataOperation(
                        out var operationLease))
                    {
                        return;
                    }

                    using (operationLease)
                    {
                        if (wsdcc.IsRunning || wsdcc.IsTransitioning)
                        {
                            return;
                        }

                        if (captureDeviceContext.DeviceType == DeviceType.XboxConsole &&
                            !GDKXHelper.IsGDKXInstalled(true))
                        {
                            return;
                        }

                        await wsdcc.StartProxyAsync(
                            captureDeviceContext.DeviceType == DeviceType.GenericProxyDevice);
                        if (captureDeviceContext.DeviceType == DeviceType.LocalPC)
                        {
                            await wsdcc.EnableDeviceProxyingOrRollBackAsync(
                                async () => { await Task.Run(wsdcc.EnableProxyingPC); return true; });
                        }
                    }
                }
                else
                {
                    if (!CaptureAppModel.AppModel.CanStopCaptures)
                    {
                        return;
                    }

                    if (!CaptureAppModel.AppModel.TryBeginCaptureDataOperation(
                            out var operationLease))
                    {
                        return;
                    }

                    using (operationLease)
                    {
                        if (!wsdcc.CanBeginStopTransition)
                        {
                            return;
                        }

                        using var transitionLease = wsdcc.BeginStopTransition();
                        await StopCaptureWithStatusAsync(
                            captureDeviceContext,
                            wsdcc);
                    }
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                ShowProxyOperationError(ex);
            }
            finally
            {
                wsdcc.RaisePropertyChange(nameof(wsdcc.IsRunning));
            }
        }

        private static async Task StopCaptureWithStatusAsync(
            ICaptureDeviceContext captureDeviceContext,
            WebServiceDeviceCaptureController controller)
        {
            var stopStopwatch = Stopwatch.StartNew();
            var progressModel = new WebProxyStopProgressModel();
            var progress = new Progress<WebProxyStopProgress>(
                progressModel.Update);

            var stopTask = StopCaptureCoreAsync(
                captureDeviceContext,
                controller,
                progress,
                stopStopwatch);

            var shouldShowProgress =
                await WebProxyStopProgressWindow.ShouldShowAsync(
                    stopTask,
                    StopDialogDelay);
            if (shouldShowProgress && !stopTask.IsCompleted)
            {
                var progressWindow = new WebProxyStopProgressWindow(
                    stopTask,
                    progressModel)
                {
                    Owner = Application.Current.MainWindow
                };
                progressWindow.ShowDialog();
            }

            await stopTask;
        }

        private static async Task StopCaptureCoreAsync(
            ICaptureDeviceContext captureDeviceContext,
            WebServiceDeviceCaptureController controller,
            IProgress<WebProxyStopProgress> progress,
            Stopwatch stopStopwatch)
        {
            await controller.StopProxyAsync(progress);
            if (captureDeviceContext.DeviceType == DeviceType.LocalPC)
            {
                progress.Report(new WebProxyStopProgress(
                    WebProxyStopPhase.DisablingSystemProxy,
                    stopStopwatch.Elapsed));
                await controller.DisableProxying();
            }

            progress.Report(new WebProxyStopProgress(
                WebProxyStopPhase.Complete,
                stopStopwatch.Elapsed));
        }

        private static void ShowProxyOperationError(Exception ex)
        {
            PublicUtilities.AppLog(LogLevel.ERROR, $"Web proxy operation failed: {ex}");
            MessageBox.Show(
                CaptureUtilities.GetProxyOperationErrorMessage(ex),
                Localization.GetLocalizedString("PROXY_CAPTURE_ERROR_TITLE"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void ChangePort_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CanChangeProxyPort(DataContext);
        }

        private void DiscardPort_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            ChangePort_CanExecute(sender, e);
            e.CanExecute = e.CanExecute &&
                TryGetCaptureContext(DataContext, out _, out var controller) && controller.HasPendingPort;
        }

        private void CancelPort_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = TryGetCaptureContext(DataContext, out _, out var controller) &&
                controller.IsPortChangeInProgress;
        }

        private void StartStopCapture_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = false;
            if (TryGetCaptureContext(DataContext, out var captureDeviceContext, out var wsdcc))
            {
                e.CanExecute = !captureDeviceContext.IsReadOnly &&
                    !wsdcc.IsTransitioning &&
                    (wsdcc.IsRunning
                        ? CaptureAppModel.AppModel.CanStopCaptures
                        : CaptureAppModel.AppModel.CanModifyCaptureData);
            }
        }

        internal static bool CanCheckDevice(object dataContext) =>
            TryGetCaptureContext(dataContext, out var context, out _) &&
            !context.IsReadOnly &&
            context.DeviceType is DeviceType.XboxConsole or DeviceType.GenericProxyDevice;

        private void CheckDevice_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CanCheckDevice(DataContext);
        }

        private void Enable_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute =
                TryGetCaptureContext(DataContext, out var captureDeviceContext, out var wsdcc) &&
                captureDeviceContext.DeviceType == DeviceType.XboxConsole &&
                !captureDeviceContext.IsReadOnly &&
                wsdcc.CanBeginTransition &&
                CaptureAppModel.AppModel.CanModifyCaptureData;
        }

        private void Disable_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute =
                TryGetCaptureContext(DataContext, out var captureDeviceContext, out var wsdcc) &&
                captureDeviceContext.DeviceType == DeviceType.XboxConsole &&
                !captureDeviceContext.IsReadOnly &&
                !wsdcc.IsTransitioning &&
                CaptureAppModel.AppModel.CanStopCaptures;
        }

        internal static bool TryGetCaptureContext(
            object dataContext,
            out ICaptureDeviceContext captureDeviceContext,
            out WebServiceDeviceCaptureController captureController)
        {
            if (dataContext is ICaptureDeviceContext context &&
                context.CaptureController is WebServiceDeviceCaptureController controller)
            {
                captureDeviceContext = context;
                captureController = controller;
                return true;
            }

            captureDeviceContext = null;
            captureController = null;
            return false;
        }

        private void ConnectionList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // we are at the bottom if the ExtentHeight + ViewPortHeight ~= VerticalOffset
            // if they are not ~=, we are not at the bottom, so above, we won't auto scroll
            _notAtBottom = (e.VerticalOffset + 1) < e.ExtentHeight - e.ViewportHeight;
        }
    }
}
