// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.Models;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for CaptureErrorsExpanded.xaml
    /// </summary>
    public partial class CaptureAnalysisControl : UserControl
    {
        public CaptureAnalysisControl()
        {
            InitializeComponent();

            CaptureAnalysisTitle.Text = Localization.GetLocalizedString("CAPTURE_ANALYSIS_TITLE");
        }

        public void AddedAnalysis(AnalysisRunModel analysisRun)
        {
            CaptureAnalysisResultsTabs.SelectedIndex = CaptureAnalysisResultsTabs.Items.Count - 1;
            RaiseAnalysisTabAddedEvent(analysisRun);
        }

        private void TabClose_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            RaiseAnalysisTabClosedEvent(e.Parameter as AnalysisRunModel);
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
