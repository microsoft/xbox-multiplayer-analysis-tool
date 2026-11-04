// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Replay;
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
        private ReplayRecordingTarget _replayTarget;

        // The Replay menu run in progress, for Cancel replay, and whether the user cancelled it. Used on the UI thread.
        private static CancellationTokenSource _reissueCancellation;
        private static bool _reissueCancelledByUser;

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

            EditAndResendMenuItem.Header = Localization.GetLocalizedString("COMPOSER_TITLE");
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
            ReplayOfLabel.Text = Localization.GetLocalizedString("REPLAY_OF_LABEL");
            TimeLabel.Text = Localization.GetLocalizedString("WEBCAP_REQRES_LABEL_TIME");
            DurationLabel.Text = Localization.GetLocalizedString("WEBCAP_REQRES_LABEL_DURATION");

            ConnectionList.Columns[0].Header = Localization.GetLocalizedString("WEBCAP_HEADER_NUMBER");
            ConnectionList.Columns[1].Header = Localization.GetLocalizedString("WEBCAP_HEADER_STATUS");
            ConnectionList.Columns[2].Header = Localization.GetLocalizedString("WEBCAP_HEADER_METHOD");
            ConnectionList.Columns[3].Header = Localization.GetLocalizedString("WEBCAP_HEADER_SCHEME");
            ConnectionList.Columns[4].Header = Localization.GetLocalizedString("WEBCAP_HEADER_HOST");
            ConnectionList.Columns[5].Header = Localization.GetLocalizedString("WEBCAP_HEADER_PATH");
        }

        private void EditAndResend_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = RequestReplayDraft.CanReplay(e.Parameter as ProxyConnectionModel);
            e.Handled = true;
        }

        private void EditAndResend_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!RequestReplayDraft.CanReplay(e.Parameter as ProxyConnectionModel))
                return;
            try
            {
                var composer = new RequestComposerWindow(
                    (ProxyConnectionModel)e.Parameter, recording: ReplayTarget, sourceName: SourceName)
                {
                    Owner = Window.GetWindow(this)
                };
                composer.Show();
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                MessageBox.Show(Window.GetWindow(this), Localization.GetLocalizedString("COMPOSER_ERROR", exception.Message),
                    Localization.GetLocalizedString("COMPOSER_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            e.Handled = true;
        }

        private void ConnectionList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Right-click need not select a row by default; replay the clicked request, not a previous selection. A row
            // that is already part of the selection keeps the selection, so several rows can be reissued together.
            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(ConnectionList, source) is DataGridRow row &&
                !row.IsSelected)
            {
                ConnectionList.SelectedItem = row.Item;
            }
        }

        // The rows that a Replay menu item applies to: the selection, in list order, when the clicked row is part of
        // it; otherwise only the clicked row.
        internal IReadOnlyList<ProxyConnectionModel> CapturesFor(DataGridRow row)
        {
            if (row?.Item is not ProxyConnectionModel clicked)
                return Array.Empty<ProxyConnectionModel>();
            var selected = new HashSet<ProxyConnectionModel>(ConnectionList.SelectedItems.OfType<ProxyConnectionModel>());
            return selected.Contains(clicked)
                ? ConnectionList.Items.OfType<ProxyConnectionModel>().Where(selected.Contains).ToList()
                : new[] { clicked };
        }

        private MenuItem CreateReplayMenu(IReadOnlyList<ProxyConnectionModel> captures)
        {
            MenuItem Item(ReissueRequest request, string key, params object[] args) => new()
            {
                Header = Localization.GetLocalizedString(key, args),
                Command = WebServiceProxyCommands.Reissue,
                CommandTarget = this,
                CommandParameter = request,
            };

            var several = new MenuItem { Header = Localization.GetLocalizedString("REPLAY_REISSUE_SEVERAL") };
            foreach (int count in new[] { 2, 5, 10, 25, 100 })
                several.Items.Add(Item(new ReissueRequest(captures, Repeat: count), "REPLAY_REISSUE_TIMES", count));

            var menu = new MenuItem { Header = Localization.GetLocalizedString("REPLAY_MENU") };
            menu.Items.Add(Item(new ReissueRequest(captures), "REPLAY_REISSUE"));
            menu.Items.Add(Item(new ReissueRequest(captures, Unconditional: true), "REPLAY_REISSUE_UNCONDITIONALLY"));
            menu.Items.Add(Item(new ReissueRequest(captures, Verify: true), "REPLAY_REISSUE_VERIFY"));
            menu.Items.Add(several);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = Localization.GetLocalizedString("REPLAY_CANCEL"),
                Command = WebServiceProxyCommands.CancelReissue,
                CommandTarget = this,
            });
            return menu;
        }

        // Where this view's replays and composer sends are recorded: by default the application's Replays tab.
        internal ReplayRecordingTarget ReplayTarget
        {
            get => _replayTarget ??= ApplicationReplayTarget(Dispatcher);
            set => _replayTarget = value;
        }

        // Asks the user to confirm a run (the message) and tells the user how a run went (the message and its image), with
        // message boxes unless tests replace them.
        internal Func<string, bool> ConfirmReplay { get; set; }

        internal Action<string, MessageBoxImage> ShowReplayMessage { get; set; }

        // A Replay menu run is in progress; one runs at a time. What protects its Replays tab is the capture-data operation
        // that its recording holds (ReplayRecordingTarget).
        internal static bool IsReissueRunning => _reissueCancellation != null;

        // The application's Replays tab, whose rows are added through dispatcher.
        internal static ReplayRecordingTarget ApplicationReplayTarget(Dispatcher dispatcher) =>
            new(CaptureAppModel.AppModel, UiDispatch(dispatcher));

        // Runs an action on dispatcher's thread and waits for it, or not at all once that dispatcher is shutting down, as
        // when XMAT exits: the recorder then counts the send as not recorded instead of waiting for a thread that has gone.
        internal static Action<Action> UiDispatch(Dispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            return action =>
            {
                if (dispatcher.HasShutdownStarted)
                {
                    return;
                }

                try
                {
                    dispatcher.Invoke(action);
                }
                catch (OperationCanceledException)
                {
                    // The dispatcher shut down before it ran the action, which then never runs.
                }
            };
        }

        // The name of this view's tab, for where its replays come from.
        private string SourceName => (DataContext as ICaptureDeviceContext)?.DeviceName ?? string.Empty;

        private void Reissue_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = !IsReissueRunning &&
                e.Parameter is ReissueRequest request &&
                request.Captures.Any(RequestReplayDraft.CanReplay) &&
                ReplayTarget.CanBegin;
            e.Handled = true;
        }

        private async void Reissue_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            if (e.Parameter is ReissueRequest request)
            {
                await ReissueFromMenuAsync(request);
            }
        }

        // Reissues the rows into the Replays tab, sequentially. The rows are copied first; methods that can change server
        // state need the user's confirmation, which states every send of the run; and the copies are what is sent. Returns
        // null when nothing was started.
        internal async Task<ReplayRunResult> ReissueFromMenuAsync(ReissueRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (IsReissueRunning)
            {
                return null;
            }

            ReplaySnapshot snapshot = ReplaySnapshot.Of(request, SourceName);
            if (snapshot.Captures.Count == 0)
            {
                return null;
            }

            Window owner = Window.GetWindow(this);
            if (snapshot.UnsafeSends > 0 && !Confirm(owner, ConfirmationMessage(snapshot)))
            {
                return null;
            }

            ReplayRunResult run = await ReissueAsync(snapshot, snapshot.UnsafeSends > 0, ReplayTarget);
            ShowReplayOutcome(owner, snapshot, run);
            return run;
        }

        // States every send of the run, repeats included, and how many of them can change server state.
        internal static string ConfirmationMessage(ReplaySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            return snapshot.Request.Repeat == 1
                ? Localization.GetLocalizedString("REPLAY_CONFIRM_UNSAFE", snapshot.Sends, snapshot.UnsafeSends)
                : Localization.GetLocalizedString(
                    "REPLAY_CONFIRM_UNSAFE_REPEATED",
                    snapshot.Sends,
                    snapshot.Captures.Count,
                    snapshot.Request.Repeat,
                    snapshot.UnsafeSends);
        }

        // Runs a Replay menu snapshot through the shared ReplayOperation, recorded through target; Busy, with nothing sent,
        // while another run, an analysis or another capture-data operation is running. IsReissueRunning holds while it
        // runs; a failure of the Replays tab ends it as failed.
        internal static async Task<ReplayRunResult> ReissueAsync(
            ReplaySnapshot snapshot,
            bool allowUnsafeMethods,
            ReplayRecordingTarget target,
            Func<ReplaySender> senderFactory = null)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(target);
            if (IsReissueRunning)
            {
                return ReplayRunResult.Busy;
            }

            using var cancellation = new CancellationTokenSource();
            using ReplayRecording recording = target.TryBegin(cancellation, snapshot.ProvenanceOf, show: true);
            if (recording == null)
            {
                return ReplayRunResult.Busy;
            }

            _reissueCancellation = cancellation;
            _reissueCancelledByUser = false;
            CommandManager.InvalidateRequerySuggested();
            OperationResult<ReplayResult> result;
            try
            {
                var operation = new ReplayOperation();
                if (senderFactory != null)
                {
                    operation.SenderFactory = senderFactory;
                }

                result = await operation.ExecuteAsync(
                    new ReplayRequest(
                        ReplaySource.FromCaptures(snapshot.Captures),
                        new ReplayOptions(
                            Repeat: snapshot.Request.Repeat,
                            Verify: snapshot.Request.Verify,
                            AllowUnsafeMethods: allowUnsafeMethods),
                        new ReplayEdits(Unconditional: snapshot.Request.Unconditional),
                        recording.Recorder),
                    OperationContext.None,
                    cancellation.Token);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Replay stopped: {exception.GetType().FullName}");
                result = OperationResult<ReplayResult>.Failure(
                    OperationOutcome.Failed,
                    new OperationDiagnostic(
                        "replay.gui.failed", OperationDiagnosticSeverity.Error, "The replay stopped before it completed."));
            }
            finally
            {
                _reissueCancellation = null;
                CommandManager.InvalidateRequerySuggested();
            }

            ReplayRecorder recorder = recording.Recorder;
            return new ReplayRunResult(
                result,
                Destination: recorder.Destination,
                BodyBudgetBytes: recorder.BodyBudgetBytes,
                Recorded: recorder.Recorded,
                OmittedBodies: recorder.OmittedBodies,
                NotRecorded: recorder.NotRecorded,
                CancelledByUser: _reissueCancelledByUser);
        }

        private bool Confirm(Window owner, string message) =>
            ConfirmReplay?.Invoke(message) ??
            ShowMessageBox(owner, message, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) ==
                MessageBoxResult.Yes;

        private void Tell(Window owner, string message, MessageBoxImage image)
        {
            if (ShowReplayMessage != null)
            {
                ShowReplayMessage(message, image);
            }
            else
            {
                ShowMessageBox(owner, message, MessageBoxButton.OK, image, MessageBoxResult.OK);
            }
        }

        private static MessageBoxResult ShowMessageBox(
            Window owner, string message, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
        {
            string title = Localization.GetLocalizedString("REPLAY_TITLE");
            return owner == null
                ? MessageBox.Show(message, title, buttons, image, defaultResult)
                : MessageBox.Show(owner, message, title, buttons, image, defaultResult);
        }

        // Tells the user how a run went: why it did not run or failed, that it was cancelled, the verify summary or the
        // sends without a complete response, then the bodies that the Replays tab did not keep and the sends it could not
        // record. A run that exiting XMAT cancelled says nothing.
        private void ShowReplayOutcome(Window owner, ReplaySnapshot snapshot, ReplayRunResult run)
        {
            if (ReplayTarget.IsShuttingDown)
            {
                return;
            }

            if (run.WasBusy)
            {
                Tell(owner, Localization.GetLocalizedString("REPLAY_BUSY"), MessageBoxImage.Information);
                return;
            }

            OperationResult<ReplayResult> result = run.Result;
            bool cancelled = result.Outcome == OperationOutcome.Cancelled;
            if (cancelled && !run.CancelledByUser && run.NotRecorded == 0)
            {
                return;
            }

            var lines = new List<string>();
            MessageBoxImage image = MessageBoxImage.Information;
            if (cancelled && run.CancelledByUser)
            {
                // Cancelling does not unsend: the request in flight may have reached the server, and it has no row.
                lines.Add(Localization.GetLocalizedString(
                    "REPLAY_CANCELLED", run.Recorded, snapshot.Sends, run.Destination));
                image = MessageBoxImage.Warning;
            }
            else if (!cancelled && !result.Succeeded)
            {
                lines.Add(Localization.GetLocalizedString(
                    "REPLAY_FAILED", result.Diagnostics.Count > 0 ? result.Diagnostics[0].Message : string.Empty));
                image = MessageBoxImage.Error;
            }
            else if (!cancelled && snapshot.Request.Verify)
            {
                lines.Add(VerifySummary(result.Value));
                if (result.Value.Mismatched + result.Value.Failed > 0)
                {
                    image = MessageBoxImage.Warning;
                }
            }
            else if (!cancelled && result.Value.Failed > 0)
            {
                lines.Add(Localization.GetLocalizedString(
                    "REPLAY_SOME_FAILED", result.Value.Failed, result.Value.Items.Count, run.Destination));
                image = MessageBoxImage.Warning;
            }

            if (run.OmittedBodies > 0)
            {
                lines.Add(Localization.GetLocalizedString(
                    "REPLAY_BODIES_NOT_KEPT", run.OmittedBodies, run.Destination, run.BodyBudgetBytes / (1024 * 1024)));
            }

            if (run.NotRecorded > 0)
            {
                lines.Add(Localization.GetLocalizedString("REPLAY_NOT_RECORDED", run.NotRecorded, run.Destination));
            }

            if ((run.OmittedBodies > 0 || run.NotRecorded > 0) && image == MessageBoxImage.Information)
            {
                image = MessageBoxImage.Warning;
            }

            if (lines.Count > 0)
            {
                Tell(owner, string.Join("\n\n", lines), image);
            }
        }

        // The outcome of Reissue and verify: the counts, then each request whose replay differed and how (at most 20).
        internal static string VerifySummary(ReplayResult result)
        {
            var summary = new StringBuilder(Localization.GetLocalizedString(
                "REPLAY_VERIFY_SUMMARY", result.Items.Count, result.Matched, result.Mismatched, result.Failed));
            foreach (ReplayItemResult item in result.Items.Where(item => item.Verification is { Matches: false }).Take(20))
            {
                string differences = string.Join(
                    ", ",
                    item.Verification.Differences.Select(difference => Localization.GetLocalizedString(
                        difference == ReplayVerifier.StatusDifference ? "REPLAY_DIFFERENCE_STATUS" : "REPLAY_DIFFERENCE_BODY")));
                summary.Append('\n').Append(Localization.GetLocalizedString(
                    "REPLAY_VERIFY_DIFFERENCE", item.RequestNumber, differences));
            }

            return summary.ToString();
        }

        private void CancelReissue_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _reissueCancellation is { IsCancellationRequested: false };
            e.Handled = true;
        }

        private void CancelReissue_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (_reissueCancellation is { IsCancellationRequested: false } cancellation)
            {
                _reissueCancelledByUser = true;
                cancellation.Cancel();
            }

            e.Handled = true;
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

        // Right-click and the menu key both open the context menu of the row that owns the
        // event source; the target is snapshotted here instead of using the selected row.
        private void ConnectionList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (HostListContextMenu.FindOwner(e.OriginalSource) is not DataGridRow row)
            {
                return;
            }
            if (!TryGetCaptureContext(DataContext, out _, out var controller))
            {
                e.Handled = true;
                return;
            }

            HostListContextMenu.Populate(
                row.ContextMenu,
                controller.HostListPolicy,
                (row.Item as ProxyConnectionModel)?.ActionableHost,
                "HOST_LIST_NO_HOST",
                row);
            row.ContextMenu.Items.Add(new Separator());
            row.ContextMenu.Items.Add(new MenuItem
            {
                Header = Localization.GetLocalizedString("COMPOSER_TITLE"),
                Command = WebServiceProxyCommands.EditAndResend,
                CommandTarget = this,
                CommandParameter = row.Item
            });
            row.ContextMenu.Items.Add(CreateReplayMenu(CapturesFor(row)));
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
