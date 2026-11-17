// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
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
        // Typing in the process filter editor applies it after this pause; Enter and Apply apply it at once.
        private static readonly TimeSpan ProcessEditDelay =
            TimeSpan.FromMilliseconds(250);
        // A capture tab shows one device; the shared selection counts its sessions under this ordinal id.
        private static readonly string ProcessSessionsDeviceId = CaptureDeviceIds.Ordinal(0);
        private bool _notAtBottom;
        private readonly Predicate<object> _connectionFilter;
        private readonly HashSet<ProxyConnectionModel> _observedConnections = new(ReferenceEqualityComparer.Instance);
        private readonly CaptureFilterIndex _statusValues = new();
        private readonly CaptureFilterIndex _hostValues = new();
        private readonly CaptureFilterIndex _methodValues = new();
        // For each observed row: whether the process filter shows it and whether it has a process, decided for the
        // process that the row had then.
        private readonly Dictionary<ProxyConnectionModel, ProcessRow> _processRows = new(ReferenceEqualityComparer.Instance);
        private readonly DispatcherTimer _processEditTimer;
        private WebServiceDeviceCaptureController _attachedController;
        private ProxyConnectionsCollection _attachedConnections;
        // The grid's own view of the attached rows, so its filter and live shaping never reach another view of them.
        private ListCollectionView _connectionsView;
        private INotifyCollectionChanged _attachedGridItems;
        private DispatcherOperation _pendingFilterRefresh;
        private ReplayRecordingTarget _replayTarget;

        // The row that the left button went down on, and where, until a drag starts or the button is released.
        private ProxyConnectionModel _dragRow;
        private Point _dragStart;

        // The Replay menu run in progress, for Cancel replay, and whether the user cancelled it. Used on the UI thread.
        private static CancellationTokenSource _reissueCancellation;
        private static bool _reissueCancelledByUser;
        private DispatcherOperation _pendingProcessIndicator;
        private ProcessFilterSpec _processSpec = ProcessFilterSpec.Empty;
        private ProcessFilter _processMatcher;
        private long _processIncluded;
        private long _processUnknown;
        private bool _showingProcessSpec;
        // While a deletion removes rows and restores the selection, neither Auto Scroll nor a change of the capture's selected
        // row scrolls the grid; the restored row is scrolled into view once.
        private bool _deleting;

        [Flags]
        private enum ProcessRowState : byte
        {
            None = 0,
            Included = 1,
            Unknown = 2,
        }

        private readonly record struct ProcessRow(ProcessRowState State, ClientProcessInfo Process);

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

        // The shared selection that picks and counts the rows a new process filter shows; tests substitute it.
        internal Func<FilterCaptureSessionsRequest, CancellationToken, OperationResult<FilterCaptureSessionsResult>>
            ProcessSelection { get; set; } = FilterCaptureSessionsOperation.Select;

        // The windows of a deletion; the main window's when null. Tests substitute them.
        internal ICaptureDeleteDialogService DeleteDialogs { get; set; }

        // The sessions that an export from this list covers: the visible ones follow every active filter (the Status, Host
        // and Method checkboxes as exact values, and the process filter); selectedOnly also keeps only the selected rows.
        internal CaptureSelectionSpec ExportSelection(bool selectedOnly) =>
            new(
                _attachedController?.ProcessFilter ?? ProcessFilterSpec.Empty,
                new HostSelection(
                    HostMatchMode.Exact, CheckedValues(HostFilters, _attachedController?.HostFilterList), ImmutableArray<string>.Empty),
                CheckedValues(StatusFilters, _attachedController?.StatusFilterList),
                CheckedValues(MethodFilters, _attachedController?.MethodFilterList),
                selectedOnly
                    ? ConnectionList.SelectedItems.OfType<ProxyConnectionModel>().Select(row => row.RowKey).ToImmutableHashSet()
                    : null,
                false);

        // The values that an enabled checkbox filter shows: like CaptureFilterIndex, the first item of a value decides.
        private static ImmutableArray<string>? CheckedValues(CheckBoxFilters filter, IEnumerable<CheckedListItem> items) =>
            filter.IsFilterEnabled
                ? (items ?? Enumerable.Empty<CheckedListItem>())
                    .GroupBy(item => item.Text ?? string.Empty, StringComparer.Ordinal)
                    .Where(values => values.First().IsChecked)
                    .Select(values => values.Key)
                    .ToImmutableArray()
                : null;

        public CaptureConnectionView()
        {
            _connectionFilter = IncludeConnection;
            InitializeComponent();
            _processEditTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = ProcessEditDelay
            };
            _processEditTimer.Tick += ProcessEditTimer_Tick;
            ProcessIncludeText.TextChanged += ProcessFilterText_TextChanged;
            ProcessExcludeText.TextChanged += ProcessFilterText_TextChanged;
            ProcessIncludeText.KeyDown += ProcessFilterText_KeyDown;
            ProcessExcludeText.KeyDown += ProcessFilterText_KeyDown;
            // The open popup closes on any click outside it, so the button that opens it ignores clicks meanwhile.
            ProcessFilterPopup.Opened += (_, _) => ProcessFilterButton.IsHitTestVisible = false;
            ProcessFilterPopup.Closed += (_, _) => ProcessFilterButton.IsHitTestVisible = true;
            TimelineView.OffersProcessFilterItems = true;

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
            ProcessFilterButton.Content = Localization.GetLocalizedString("WEBCAP_FILTER_PROCESS");
            ProcessIncludeLabel.Text = Localization.GetLocalizedString("PROCESS_FILTER_INCLUDE_LABEL");
            ProcessExcludeLabel.Text = Localization.GetLocalizedString("PROCESS_FILTER_EXCLUDE_LABEL");
            ProcessFilterHelp.Text = Localization.GetLocalizedString("PROCESS_FILTER_HELP");
            HideSvchostButton.Content = Localization.GetLocalizedString("PROCESS_FILTER_HIDE_SVCHOST");
            ApplyProcessFilterButton.Content = Localization.GetLocalizedString("PROCESS_FILTER_APPLY");
            ShowAllProcessesButton.Content = Localization.GetLocalizedString("PROCESS_FILTER_SHOW_ALL");

            ReqestResponseTab.Header = Localization.GetLocalizedString("WEBCAP_REQRES_TAB");
            ScriptEditorTab.Header = Localization.GetLocalizedString("WEBCAP_EDITOR_TAB");

            RequestViewControl.Title = Localization.GetLocalizedString("WEBCAP_REQRES_TITLE_REQUEST");
            ResponseViewControl.Title = Localization.GetLocalizedString("WEBCAP_REQRES_TITLE_RESPONSE");

            RequestIdLabel.Text = Localization.GetLocalizedString("WEBCAP_REQRES_LABEL_REQID");
            ReplayOfLabel.Text = Localization.GetLocalizedString("REPLAY_OF_LABEL");
            ViewVerifyDiffButton.Content = Localization.GetLocalizedString("VERIFY_DIFF_VIEW");
            TimeLabel.Text = Localization.GetLocalizedString("WEBCAP_REQRES_LABEL_TIME");
            DurationLabel.Text = Localization.GetLocalizedString("WEBCAP_REQRES_LABEL_DURATION");

            ConnectionList.Columns[0].Header = Localization.GetLocalizedString("WEBCAP_HEADER_NUMBER");
            ConnectionList.Columns[1].Header = Localization.GetLocalizedString("WEBCAP_HEADER_STATUS");
            ConnectionList.Columns[2].Header = Localization.GetLocalizedString("WEBCAP_HEADER_METHOD");
            ConnectionList.Columns[3].Header = Localization.GetLocalizedString("WEBCAP_HEADER_SCHEME");
            ConnectionList.Columns[4].Header = Localization.GetLocalizedString("WEBCAP_HEADER_HOST");
            ConnectionList.Columns[5].Header = Localization.GetLocalizedString("WEBCAP_HEADER_PROCESS");
            ConnectionList.Columns[6].Header = Localization.GetLocalizedString("WEBCAP_HEADER_PATH");
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
            if (RefusesStoredText(Window.GetWindow(this), new[] { (ProxyConnectionModel)e.Parameter }))
            {
                e.Handled = true;
                return;
            }

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

        // View verification differences applies to a row that Reissue and verify recorded from a send that it
        // verified, also when the tab did not keep the differences (the window then says so), while it is a row of this
        // view's tab.
        private void ViewVerifyDiff_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = VerifiedRowOfThisTab(e.Parameter, out _) != null;
            e.Handled = true;
        }

        // Opens the recorded verification of the very row that the command names. Nothing is sent or compared again.
        private void ViewVerifyDiff_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            ProxyConnectionModel row = VerifiedRowOfThisTab(e.Parameter, out WebServiceDeviceCaptureController controller);
            if (row == null)
            {
                return;
            }

            var window = new VerifyDiffWindow(row, controller) { Owner = Window.GetWindow(this) };
            (ShowVerifyDiff ?? (opened => opened.Show()))(window);
        }

        // The command's parameter when it is a row recorded from a verified send and, as this very object, a row of
        // this view's tab, with that tab's controller; otherwise null, as for a copy of such a row or a row of another
        // tab. A row is never found again by its number or its tab's name.
        private ProxyConnectionModel VerifiedRowOfThisTab(object parameter, out WebServiceDeviceCaptureController controller)
        {
            if (parameter is not ProxyConnectionModel { IsVerified: true } row ||
                !TryGetCaptureContext(DataContext, out _, out controller) ||
                !controller.ProxyConnections.Any(shown => ReferenceEquals(shown, row)))
            {
                controller = null;
                return null;
            }

            return row;
        }

        // Shows a verification differences window, by default without blocking XMAT; tests replace it.
        internal Action<VerifyDiffWindow> ShowVerifyDiff { get; set; }

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

        // Drag a session: pressing on a row and moving past the system's drag distance drags that request into a composer
        // (RequestComposerWindow.DropSession).
        private void ConnectionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragRow = e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(ConnectionList, source) is DataGridRow { Item: ProxyConnectionModel row }
                    ? row
                    : null;
            _dragStart = e.GetPosition(ConnectionList);
        }

        private void ConnectionList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragRow == null)
                return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragRow = null;
                return;
            }

            if (!IsPastDragThreshold(e.GetPosition(ConnectionList) - _dragStart))
                return;
            ProxyConnectionModel row = _dragRow;
            _dragRow = null;
            using ComposerSessionDragToken drag = BeginSessionDrag(row);
            if (drag != null)
            {
                DragDrop.DoDragDrop(ConnectionList, drag.Data, DragDropEffects.Copy);
            }
        }

        // Whether the mouse has moved far enough with its button down to start a drag.
        internal static bool IsPastDragThreshold(Vector moved) =>
            Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance;

        // Lends the pressed row to a composer for one drag: one replayable request, the only selected row; several selected
        // rows are not dragged, since a composer takes one request. Null when nothing is dragged. Dispose it when the drag
        // has ended.
        internal ComposerSessionDragToken BeginSessionDrag(ProxyConnectionModel row) =>
            row != null && ConnectionList.SelectedItems.Count == 1 && ReferenceEquals(ConnectionList.SelectedItem, row)
                ? ComposerSessionDrag.Begin(row, SourceName)
                : null;

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

        // Asks the user to confirm a run (the message) and tells the user how a run went, or why a request is not
        // replayed (the message and its image), with message boxes unless tests replace them.
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

        // Reissues the rows into the Replays tab, sequentially. The rows are copied first; copies with stored text that is
        // not known to be literal are refused (RefusesStoredText), then a selection with a destination that is malformed or
        // over its size limit, or that would send to an IPv6 address with a zone (RefusesCaptureDestinations), before
        // anything is asked; methods that can change server state, and rows of the Replays tab that would go to another
        // origin than the one that their request was captured or saved for, or first sent to
        // (ReplaySnapshot.CapturedOrigins), need the user's confirmation, one question for both, which states every send of
        // the run; each permission is given only when its own count asks for it; and the copies are what is sent. Returns
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
            if (RefusesStoredText(owner, snapshot.Captures))
            {
                return null;
            }

            if (RefusesCaptureDestinations(owner, snapshot.Captures))
            {
                return null;
            }

            if ((snapshot.UnsafeSends > 0 || snapshot.OriginChangeSends > 0) &&
                !Confirm(owner, ConfirmationMessage(snapshot)))
            {
                return null;
            }

            ReplayRunResult run = await ReissueAsync(
                snapshot,
                snapshot.UnsafeSends > 0,
                ReplayTarget,
                allowOriginChange: snapshot.OriginChangeSends > 0);
            ShowReplayOutcome(owner, snapshot, run);
            return run;
        }

        // States every send of the run, repeats included, how many of them can change server state, and how many go to
        // another origin than the one that their request was captured or saved for, or first sent to.
        internal static string ConfirmationMessage(ReplaySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            bool repeated = snapshot.Request.Repeat != 1;
            if (snapshot.OriginChangeSends == 0)
            {
                return repeated
                    ? Localization.GetLocalizedString(
                        "REPLAY_CONFIRM_UNSAFE_REPEATED",
                        snapshot.Sends,
                        snapshot.Captures.Count,
                        snapshot.Request.Repeat,
                        snapshot.UnsafeSends)
                    : Localization.GetLocalizedString("REPLAY_CONFIRM_UNSAFE", snapshot.Sends, snapshot.UnsafeSends);
            }

            if (snapshot.UnsafeSends == 0)
            {
                return repeated
                    ? Localization.GetLocalizedString(
                        "REPLAY_CONFIRM_ORIGIN_REPEATED",
                        snapshot.Sends,
                        snapshot.Captures.Count,
                        snapshot.Request.Repeat,
                        snapshot.OriginChangeSends)
                    : Localization.GetLocalizedString("REPLAY_CONFIRM_ORIGIN", snapshot.Sends, snapshot.OriginChangeSends);
            }

            return repeated
                ? Localization.GetLocalizedString(
                    "REPLAY_CONFIRM_UNSAFE_ORIGIN_REPEATED",
                    snapshot.Sends,
                    snapshot.Captures.Count,
                    snapshot.Request.Repeat,
                    snapshot.UnsafeSends,
                    snapshot.OriginChangeSends)
                : Localization.GetLocalizedString(
                    "REPLAY_CONFIRM_UNSAFE_ORIGIN", snapshot.Sends, snapshot.UnsafeSends, snapshot.OriginChangeSends);
        }

        // Runs a Replay menu snapshot through the shared ReplayOperation, recorded through target; Busy, with nothing sent,
        // while another run, an analysis or another capture-data operation is running. IsReissueRunning holds while it
        // runs; a failure of the Replays tab ends it as failed. The origins that the snapshot restores go with its rows
        // (ReplaySource.FromCaptures), so a row sent to another origin than its own is refused, with nothing sent, unless
        // allowOriginChange.
        internal static async Task<ReplayRunResult> ReissueAsync(
            ReplaySnapshot snapshot,
            bool allowUnsafeMethods,
            ReplayRecordingTarget target,
            Func<ReplaySender> senderFactory = null,
            bool allowOriginChange = false)
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
                        ReplaySource.FromCaptures(snapshot.Captures, snapshot.CapturedOrigins),
                        new ReplayOptions(
                            Repeat: snapshot.Request.Repeat,
                            Verify: snapshot.Request.Verify,
                            AllowUnsafeMethods: allowUnsafeMethods,
                            AllowOriginChange: allowOriginChange,
                            VerifyDiff: snapshot.Request.Verify),
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
                CancelledByUser: _reissueCancelledByUser,
                OmittedVerifyDiffs: recorder.OmittedVerifyDiffs,
                VerifyDiffBudgetBytes: recorder.VerifyDiffBudgetBytes);
        }

        private bool Confirm(Window owner, string message) =>
            ConfirmReplay?.Invoke(message) ??
            ShowMessageBox(owner, message, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) ==
                MessageBoxResult.Yes;

        // Until the GUI asks for a decision bound to a capture's own file, the requests of an XMAT
        // capture without the text-format marker are neither opened in a composer nor reissued: the user is told, in
        // words that name only a marked copy (replay.capture-text.ambiguous), and nothing is opened, asked or sent. A
        // capture whose source is unknown counts as unmarked. True when refused.
        private bool RefusesStoredText(Window owner, IEnumerable<ProxyConnectionModel> captures)
        {
            if (captures.All(capture => capture.CaptureTextSource?.Semantics == CaptureTextSemantics.Literal))
            {
                return false;
            }

            Tell(
                owner, RequestComposerWindow.StoredTextRefusalText(ReplayOperation.StoredTextAmbiguousCode),
                MessageBoxImage.Warning, Localization.GetLocalizedString("CAPTURE_TEXT_AMBIGUOUS_TITLE"));
            return true;
        }

        // XMAT sends nothing to a destination that is malformed or exceeds the size limit (replay.request.malformed), or
        // to an IPv6 address with a zone (replay.destination.scoped-ipv6): a selection in which any request that Reissue
        // would send has one is refused whole, as the operation would refuse it and with its precedence, but before
        // anything is asked, in the words for its code; the requests that Reissue skips are no destinations
        // (ReplayOperation.CheckCaptureDestinations). It reads only each copy's destination, as far as the shared decision
        // does and within its bound (ReplayDestination.ReadCaptureDestination, MaxCaptureDestinationChars), and does so
        // on the UI thread, so nothing can start another Reissue meanwhile. True when refused.
        private bool RefusesCaptureDestinations(Window owner, IEnumerable<ProxyConnectionModel> captures)
        {
            OperationDiagnostic refusal = ReplayOperation.CheckCaptureDestinations(captures);
            if (refusal == null)
            {
                return false;
            }

            string reason = refusal.Code switch
            {
                ReplayOperation.RequestMalformedCode => Localization.GetLocalizedString("REPLAY_DESTINATION_MALFORMED"),
                ReplayDestination.ScopedIpv6Code => Localization.GetLocalizedString("REPLAY_DESTINATION_SCOPED_IPV6"),
                _ => refusal.Message,
            };
            Tell(owner, Localization.GetLocalizedString("REPLAY_FAILED", reason), MessageBoxImage.Warning);
            return true;
        }

        private void Tell(Window owner, string message, MessageBoxImage image, string title = null)
        {
            if (ShowReplayMessage != null)
            {
                ShowReplayMessage(message, image);
            }
            else
            {
                ShowMessageBox(owner, message, MessageBoxButton.OK, image, MessageBoxResult.OK, title);
            }
        }

        private static MessageBoxResult ShowMessageBox(
            Window owner, string message, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult,
            string title = null)
        {
            title ??= Localization.GetLocalizedString("REPLAY_TITLE");
            return owner == null
                ? MessageBox.Show(message, title, buttons, image, defaultResult)
                : MessageBox.Show(owner, message, title, buttons, image, defaultResult);
        }

        // Tells the user how a run went: why it did not run or failed, that it was cancelled, the verify summary, with
        // where to see how replays differed, or the sends without a complete response, then the bodies and verification
        // differences that the Replays tab did not keep and the sends it could not record. A run that exiting XMAT
        // cancelled says nothing.
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
                if (result.Value.Mismatched > 0)
                {
                    lines.Add(Localization.GetLocalizedString("REPLAY_VERIFY_DIFF_HINT", run.Destination));
                }

                if (result.Value.Mismatched + result.Value.Failed + NotVerified(result.Value) > 0)
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

            if (run.OmittedVerifyDiffs > 0)
            {
                lines.Add(Localization.GetLocalizedString(
                    "REPLAY_DIFFS_NOT_KEPT",
                    run.OmittedVerifyDiffs,
                    run.Destination,
                    run.VerifyDiffBudgetBytes / (1024 * 1024)));
            }

            if (run.NotRecorded > 0)
            {
                lines.Add(Localization.GetLocalizedString("REPLAY_NOT_RECORDED", run.NotRecorded, run.Destination));
            }

            if ((run.OmittedBodies > 0 || run.OmittedVerifyDiffs > 0 || run.NotRecorded > 0) &&
                image == MessageBoxImage.Information)
            {
                image = MessageBoxImage.Warning;
            }

            if (lines.Count > 0)
            {
                Tell(owner, string.Join("\n\n", lines), image);
            }
        }

        // The outcome of Reissue and verify: the counts, including the requests not verified, then each request whose replay
        // differed and how (at most 20).
        internal static string VerifySummary(ReplayResult result)
        {
            int notVerified = NotVerified(result);
            var summary = new StringBuilder(notVerified == 0
                ? Localization.GetLocalizedString(
                    "REPLAY_VERIFY_SUMMARY", result.Items.Count, result.Matched, result.Mismatched, result.Failed)
                : Localization.GetLocalizedString(
                    "REPLAY_VERIFY_SUMMARY_NOT_VERIFIED",
                    result.Items.Count,
                    result.Matched,
                    result.Mismatched,
                    result.Failed,
                    notVerified));
            foreach (ReplayItemResult item in result.Items.Where(item => item.Verification is { Matches: false }).Take(20))
            {
                summary.Append('\n').Append(Localization.GetLocalizedString(
                    "REPLAY_VERIFY_DIFFERENCE", item.RequestNumber, VerifyDiffWording.Differences(item.Verification)));
            }

            return summary.ToString();
        }

        // The sends of a Reissue and verify run that got a response but were not compared with the captured one, because
        // no response was captured for them, or it was recorded without its body (ReplayOperation.VerifySkippedCode). They
        // count as neither matched nor differed.
        internal static int NotVerified(ReplayResult result) =>
            result.Items.Count(item => item.Outcome == ReplayOutcome.Completed && item.Verification == null);

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

        // An unloaded view keeps the grid's view of the rows, so the selection survives until it is shown again.
        private void UserControl_Unloaded(object sender, RoutedEventArgs e) => DetachContext(releaseView: false);

        private void UserControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsLoaded)
            {
                AttachContext();
            }
            else
            {
                DetachContext(releaseView: true);
            }
            CommandManager.InvalidateRequerySuggested();
        }

        private void AttachContext()
        {
            if (!TryGetCaptureContext(DataContext, out var context, out var controller))
            {
                DetachContext(releaseView: true);
                return;
            }
            CheckButton.Content = Localization.GetLocalizedString(
                context.DeviceType == DeviceType.GenericProxyDevice ? "PROXY_SETUP_DETAILS" : "WEBCAP_CHECK");
            if (ReferenceEquals(controller, _attachedController) &&
                ReferenceEquals(controller.ProxyConnections, _attachedConnections))
            {
                return;
            }
            DetachContext(releaseView: false);
            _attachedController = controller;
            controller.PropertyChanged += CaptureConnectionView_ControllerChanged;
            UpdateFilterSources();
            AttachConnections(controller.ProxyConnections);
            ShowConnections(controller.ProxyConnections, startOnSelection: true);
            _attachedGridItems = ConnectionList.Items;
            _attachedGridItems.CollectionChanged += CaptureConnectionView_CollectionChanged;
            UpdateProcessSpec(reselect: true);
            RequestFilterRefresh();
        }

        // Stops following the capture. releaseView also gives up the grid's view of the rows, when the view no longer
        // shows any capture.
        private void DetachContext(bool releaseView)
        {
            _pendingFilterRefresh?.Abort();
            _pendingFilterRefresh = null;
            _pendingProcessIndicator?.Abort();
            _pendingProcessIndicator = null;
            _processEditTimer?.Stop();
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
            if (ConnectionList != null)
            {
                if (ReferenceEquals(ConnectionList.Items.Filter, _connectionFilter))
                {
                    ConnectionList.Items.Filter = null;
                }
                SetLiveProcessFiltering(false);
                if (releaseView)
                {
                    ReleaseConnectionsView();
                }
            }
            UpdateFilterSources();
            if (ProcessFilterIndicator != null)
            {
                UpdateProcessSpec(reselect: true);
            }
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

        // The grid shows its own view of the rows for as long as it shows that collection, and follows that view's
        // current row. Like the bindings it replaces, a view shown on attach starts on the capture's selected row, or on
        // none, and the view of a replacing collection starts on its first row.
        private void ShowConnections(ProxyConnectionsCollection connections, bool startOnSelection)
        {
            if (_connectionsView != null && ReferenceEquals(_connectionsView.SourceCollection, connections))
            {
                return;
            }
            if (connections == null)
            {
                ReleaseConnectionsView();
                return;
            }
            var view = new ListCollectionView(connections);
            if (startOnSelection)
            {
                view.MoveCurrentTo(_attachedController?.SelectedConnectionModel);
            }
            SetLiveProcessFiltering(false);
            ListCollectionView previous = _connectionsView;
            _connectionsView = view;
            ConnectionList.ItemsSource = view;
            RetireConnectionsView(previous);
        }

        private void ReleaseConnectionsView()
        {
            ListCollectionView retired = _connectionsView;
            if (retired == null)
            {
                return;
            }
            SetLiveProcessFiltering(false);
            _connectionsView = null;
            if (ReferenceEquals(ConnectionList.ItemsSource, retired))
            {
                ConnectionList.ItemsSource = null;
            }
            RetireConnectionsView(retired);
        }

        // The grid no longer shows the retired view, but that view may be handling the rows' notification that retired
        // it, and detaching it from the rows during that notification breaks it. It stops observing the rows once the
        // notification has finished; the operation holds that view, never the one that replaced it.
        private void RetireConnectionsView(ListCollectionView retired)
        {
            if (retired != null)
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(retired.DetachFromSourceCollection));
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
            _processRows.Clear();
            _processIncluded = 0;
            _processUnknown = 0;
            RequestProcessIndicatorUpdate();
        }

        private void ObserveConnection(ProxyConnectionModel model)
        {
            if (_observedConnections.Add(model))
            {
                model.PropertyChanged += ProxyConnectionModel_PropertyChanged;
                UpdateFilters(model);
                if (_observedConnections.Contains(model))
                {
                    TrackProcess(model);
                }
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
                if (_processMatcher != null && _observedConnections.Count != 0)
                {
                    SelectProcesses();
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
                        UntrackProcess(model);
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
                _methodValues.IsChecked(model.Method)) &&
                (_processMatcher == null ||
                IsShownByProcessFilter(model));
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
                bool replaced = !ReferenceEquals(_attachedConnections, _attachedController.ProxyConnections);
                if (replaced)
                {
                    AttachConnections(_attachedController.ProxyConnections);
                    ShowConnections(_attachedController.ProxyConnections, startOnSelection: false);
                }
                UpdateProcessSpec(reselect: replaced);
                RequestFilterRefresh();
            }
            else if (args.PropertyName == nameof(WebServiceDeviceCaptureController.ProcessFilter) &&
                UpdateProcessSpec(reselect: false))
            {
                // A notification without new selectors changes nothing.
                RequestFilterRefresh();
            }
            if (!_deleting &&
                (string.IsNullOrEmpty(args.PropertyName) ||
                    args.PropertyName == nameof(WebServiceDeviceCaptureController.SelectedConnectionModel)))
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
            if (!_deleting && AutoScroll && ConnectionList.Items.Count > 0 && !_notAtBottom)
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
            // A late process resolution only updates the counts; live filtering moves that one row in or out of the grid.
            if ((string.IsNullOrEmpty(args.PropertyName) ||
                args.PropertyName is nameof(ProxyConnectionModel.ClientProcess) or
                    nameof(ProxyConnectionModel.IsProcessResolving)) &&
                _observedConnections.Contains(model))
            {
                TrackProcess(model);
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
            bool processFilterActive = _processMatcher != null;
            var filter = StatusFilters.IsFilterEnabled || HostFilters.IsFilterEnabled || MethodFilters.IsFilterEnabled ||
                processFilterActive
                ? _connectionFilter
                : null;
            // One refresh for the whole change, when the deferral ends: assigning the filter, even the one already set,
            // refreshes the view, and WPF refuses an explicit Refresh while the refresh is deferred. While a process
            // filter is set, live filtering re-evaluates a row whose process resolves later, so that row is added or
            // removed without refreshing the others.
            using (ConnectionList.Items.DeferRefresh())
            {
                SetLiveProcessFiltering(processFilterActive);
                if (filter != null || ConnectionList.Items.Filter != null)
                {
                    ConnectionList.Items.Filter = filter;
                }
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
            if (!TryGetCaptureContext(DataContext, out ICaptureDeviceContext tab, out var controller))
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
            if (row.Item is ProxyConnectionModel { IsReplay: true } replay)
            {
                row.ContextMenu.Items.Add(new MenuItem
                {
                    Header = Localization.GetLocalizedString("VERIFY_DIFF_VIEW"),
                    Command = WebServiceProxyCommands.ViewVerifyDiff,
                    CommandTarget = this,
                    CommandParameter = replay,
                });
            }
            CaptureProcessViewAdapter.AddMenuItems(
                row.ContextMenu,
                row.Item as ProxyConnectionModel,
                controller.ProcessFilter,
                row);
            row.ContextMenu.Items.Add(new Separator());
            row.ContextMenu.Items.Add(CommandItem("WEBCAP_EXPORT_SELECTED", WebServiceProxyCommands.ExportSelectedSessions, row));
            row.ContextMenu.Items.Add(CommandItem("WEBCAP_EXPORT_VISIBLE", WebServiceProxyCommands.ExportVisibleSessions, row));
            row.ContextMenu.Items.Add(new Separator());
            string readOnly = tab.IsReadOnly ? Localization.GetLocalizedString("CAPTURE_DELETE_READ_ONLY") : null;
            row.ContextMenu.Items.Add(
                DeleteItem("WEBCAP_DELETE_SELECTED", WebServiceProxyCommands.DeleteSelectedSessions, ModifierKeys.None, row, readOnly));
            row.ContextMenu.Items.Add(
                DeleteItem("WEBCAP_DELETE_UNSELECTED", WebServiceProxyCommands.DeleteUnselectedSessions, ModifierKeys.Shift, row, readOnly));
        }

        private static MenuItem CommandItem(string header, RoutedCommand command, IInputElement target) =>
            new() { Header = Localization.GetLocalizedString(header), CommandTarget = target, Command = command };

        private void ExportSessions_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _attachedController != null && CaptureAppModel.AppModel.CanModifyCaptureData &&
                (e.Command == WebServiceProxyCommands.ExportSelectedSessions
                    ? ConnectionList.SelectedItems.Count
                    : ConnectionList.Items.Count) != 0;
            e.Handled = true;
        }

        private async void ExportSessions_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!TryGetCaptureContext(DataContext, out ICaptureDeviceContext tab, out _) ||
                Window.GetWindow(this) is not ICaptureExportDialogService dialogs)
            {
                return;
            }

            // Selectors typed in the process filter editor and not applied yet are part of what the list will show.
            if (_processEditTimer.IsEnabled)
            {
                ApplyProcessEditor();
            }

            CaptureSelectionSpec selection = ExportSelection(e.Command == WebServiceProxyCommands.ExportSelectedSessions);
            await CaptureExportWorkflow.For(dialogs).RunAsync(CaptureAppModel.AppModel, tab, selection, sanitizedOnly: false);
        }

        // The row and timeline menus keep the rows they last opened on. Once a deletion is confirmed, whether it ran,
        // failed or found the tab unavailable, each is closed and let go; a failure is logged, never thrown over the result.
        private void ReleaseMenus()
        {
            foreach (Action release in new Action[]
            {
                () =>
                {
                    var rowMenu = (ContextMenu)Resources["HostListRowMenu"];
                    rowMenu.IsOpen = false;
                    rowMenu.Items.Clear();
                    rowMenu.ClearValue(ContextMenu.PlacementTargetProperty);
                },
                () => TimelineView.ReleaseMenuTargets(),
            })
            {
                try
                {
                    release();
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Releasing a menu after deleting sessions failed: {exception}");
                }
            }
        }

        // Selectors typed in the process filter editor, and filter changes not applied yet, are part of what the list shows.
        private void ApplyPendingFilters()
        {
            if (_processEditTimer.IsEnabled)
            {
                ApplyProcessEditor();
            }

            if (_pendingFilterRefresh?.Status == DispatcherOperationStatus.Pending)
            {
                _pendingFilterRefresh.Abort();
                ApplyConnectionFilter();
            }
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
