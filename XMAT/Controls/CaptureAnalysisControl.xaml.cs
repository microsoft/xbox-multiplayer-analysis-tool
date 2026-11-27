// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using XMAT.Models;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for CaptureErrorsExpanded.xaml
    /// </summary>
    public partial class CaptureAnalysisControl : UserControl
    {
        // The skeleton appears only once an analysis has been processing this long, so quick ones never flash it, and
        // then stays up at least SkeletonMinimumTime.
        private static readonly TimeSpan SkeletonDelay = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan SkeletonMinimumTime = TimeSpan.FromMilliseconds(400);

        private static readonly DependencyPropertyKey IsHoldingResultsPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsHoldingResults), typeof(bool), typeof(CaptureAnalysisControl), new PropertyMetadata(false));

        public static readonly DependencyProperty IsHoldingResultsProperty = IsHoldingResultsPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey ShowsSkeletonPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(ShowsSkeleton), typeof(bool), typeof(CaptureAnalysisControl), new PropertyMetadata(false));

        public static readonly DependencyProperty ShowsSkeletonProperty = ShowsSkeletonPropertyKey.DependencyProperty;

        // What the loading state follows, bound so that neither the runs nor LookState hold on to the control.
        private static readonly DependencyProperty SelectedRunProperty = DependencyProperty.Register(
            "SelectedRun", typeof(object), typeof(CaptureAnalysisControl), new PropertyMetadata(null, OnLoadingInputChanged));

        private static readonly DependencyProperty IsSelectedRunProcessingProperty = DependencyProperty.Register(
            "IsSelectedRunProcessing", typeof(bool), typeof(CaptureAnalysisControl),
            new PropertyMetadata(false, OnLoadingInputChanged));

        private static readonly DependencyProperty IsNewLookProperty = DependencyProperty.Register(
            "IsNewLook", typeof(bool), typeof(CaptureAnalysisControl), new PropertyMetadata(true, OnLoadingInputChanged));

        private readonly DispatcherTimer _skeletonTimer = new(DispatcherPriority.Normal);
        private AnalysisRunModel _heldRun;
        private long _skeletonShownAt;

        public CaptureAnalysisControl()
        {
            InitializeComponent();
            AllCaptureDevicesTabControl.AddCloseTabShortcuts(this, CaptureAnalysisResultsTabs);

            CaptureAnalysisTitle.Text = Localization.GetLocalizedString("CAPTURE_ANALYSIS_TITLE");
            AnalyzeEmptyCaptureButton.Content = Localization.GetLocalizedString("WEBCAP_ANALYZE");
            AnalyzeEmptyCaptureButton.ToolTip = Localization.GetLocalizedString("ANALYZE_CAPTURE_REQUIRES_STOP_TOOLTIP");

            _skeletonTimer.Tick += SkeletonTimer_Tick;
            SetBinding(SelectedRunProperty, new Binding(nameof(TabControl.SelectedItem)) { Source = CaptureAnalysisResultsTabs });
            SetBinding(IsSelectedRunProcessingProperty, new Binding("SelectedItem.IsProcessing")
            {
                Source = CaptureAnalysisResultsTabs,
                FallbackValue = false
            });
            SetBinding(IsNewLookProperty, new Binding(nameof(LookState.IsNewLook)) { Source = LookState.Current });
            Loaded += (_, _) => UpdateLoading();
            Unloaded += (_, _) => ReleaseResults(reveal: false);
        }

        public static string AnalyzingText => Localization.GetLocalizedString("ANALYSIS_LOADING");

        // Whether the selected run's results are hidden while it processes, in the new look.
        public bool IsHoldingResults => (bool)GetValue(IsHoldingResultsProperty);

        // Whether a skeleton of the report stands in for the held results.
        public bool ShowsSkeleton => (bool)GetValue(ShowsSkeletonProperty);

        public void AddedAnalysis(AnalysisRunModel analysisRun)
        {
            CaptureAnalysisResultsTabs.SelectedIndex = CaptureAnalysisResultsTabs.Items.Count - 1;
            RaiseAnalysisTabAddedEvent(analysisRun);
        }

        private static void OnLoadingInputChanged(DependencyObject view, DependencyPropertyChangedEventArgs e) =>
            ((CaptureAnalysisControl)view).UpdateLoading();

        // In the new look, the selected run's results are held back while it processes; when processing ends they fade
        // in. The original look shows the run's own progress, as it always has.
        private void UpdateLoading()
        {
            var run = CaptureAnalysisResultsTabs.SelectedItem as AnalysisRunModel;
            bool newLook = LookState.Current.IsNewLook;
            if (newLook && run is { IsProcessing: true })
            {
                if (run != _heldRun)
                {
                    // A run started, or the selection moved to one that is still processing.
                    SetValue(ShowsSkeletonPropertyKey, false);
                    SetValue(IsHoldingResultsPropertyKey, true);
                    _heldRun = run;
                    RestartSkeletonTimer(SkeletonDelay);
                }

                return;
            }

            if (_heldRun == null)
            {
                return;
            }

            // The run on screen finished: a skeleton that has only just appeared stays a little longer, so it never
            // flashes. Selecting another run, or the original look, shows its results at once.
            bool finished = newLook && run == _heldRun;
            if (finished && ShowsSkeleton)
            {
                TimeSpan remaining = SkeletonMinimumTime -
                    TimeSpan.FromMilliseconds(Environment.TickCount64 - _skeletonShownAt);
                if (remaining > TimeSpan.Zero)
                {
                    RestartSkeletonTimer(remaining);
                    return;
                }
            }

            ReleaseResults(reveal: finished);
        }

        // Shows the skeleton once the delay has passed, or releases the results once it has been up long enough.
        private void SkeletonTimer_Tick(object sender, EventArgs e)
        {
            _skeletonTimer.Stop();
            if (_heldRun == null)
            {
                return;
            }

            if (_heldRun.IsProcessing)
            {
                _skeletonShownAt = Environment.TickCount64;
                SetValue(ShowsSkeletonPropertyKey, true);
            }
            else
            {
                ReleaseResults(reveal: true);
            }
        }

        private void ReleaseResults(bool reveal)
        {
            _skeletonTimer.Stop();
            _heldRun = null;
            SetValue(ShowsSkeletonPropertyKey, false);
            SetValue(IsHoldingResultsPropertyKey, false);
            if (reveal && MotionSettings.Current.AnimationsEnabled && FindAnalysisResults() is FrameworkElement results &&
                results.TryFindResource("FadeInStoryboard") is Storyboard fadeIn)
            {
                fadeIn.Begin(results);
            }
        }

        private void RestartSkeletonTimer(TimeSpan interval)
        {
            _skeletonTimer.Stop();
            _skeletonTimer.Interval = interval;
            _skeletonTimer.Start();
        }

        // The presenter of the selected run's results, in the run template under the tab control's content host.
        private FrameworkElement FindAnalysisResults() =>
            CaptureAnalysisResultsTabs.Template?.FindName("PART_SelectedContentHost", CaptureAnalysisResultsTabs)
                is ContentPresenter host
                ? host.ContentTemplate?.FindName("AnalysisResults", host) as FrameworkElement
                : null;

        private void TabClose_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            RaiseAnalysisTabClosedEvent(e.Parameter as AnalysisRunModel);
        }

        // An analysis can be closed once it has finished, which is when its close button shows.
        private void TabClose_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = e.Parameter is AnalysisRunModel { CloseVisibility: Visibility.Visible };
        }

        //
        // AnalysisTabAdded
        //
        public static readonly RoutedEvent AnalysisTabAddedEvent = EventManager.RegisterRoutedEvent(
            "AnalysisTabAdded", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(AllCaptureDevicesTabControl));

        public event RoutedEventHandler AnalysisTabAdded
        {
            add { AddHandler(AnalysisTabAddedEvent, value); }
            remove { RemoveHandler(AnalysisTabAddedEvent, value); }
        }
        void RaiseAnalysisTabAddedEvent(AnalysisRunModel analysisRun)
        {
            RaiseEvent(new AnalysisTabAddedRoutedEventArgs(AnalysisTabAddedEvent, analysisRun));
        }

        //
        // AnalysisTabClosed
        //
        public static readonly RoutedEvent AnalysisTabClosedEvent = EventManager.RegisterRoutedEvent(
            "AnalysisTabClosed",
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(CaptureAnalysisControl));

        public event RoutedEventHandler AnalysisTabClosed
        {
            add { AddHandler(AnalysisTabClosedEvent, value); }
            remove { RemoveHandler(AnalysisTabClosedEvent, value); }
        }
        void RaiseAnalysisTabClosedEvent(AnalysisRunModel analysisRun)
        {
            RaiseEvent(new AnalysisTabClosedRoutedEventArgs(AnalysisTabClosedEvent, analysisRun));
        }
    }

    public class AnalysisTabAddedRoutedEventArgs : RoutedEventArgs
    {
        public AnalysisRunModel AnalysisRun { get; set; }
        public AnalysisTabAddedRoutedEventArgs(RoutedEvent re, AnalysisRunModel analysisRun) : base(re)
        {
            AnalysisRun = analysisRun;
        }
    }

    public class AnalysisTabClosedRoutedEventArgs : RoutedEventArgs
    {
        public AnalysisRunModel AnalysisRun { get; set; }
        public AnalysisTabClosedRoutedEventArgs(RoutedEvent re, AnalysisRunModel analysisRun) : base(re)
        {
            AnalysisRun = analysisRun;
        }
    }
}
