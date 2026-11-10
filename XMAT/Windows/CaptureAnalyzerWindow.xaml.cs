// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Windows;
using XMAT.Operations.Processes;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for CaptureAnalyzerSelector.xaml
    /// </summary>
    public partial class CaptureAnalyzerWindow : Window
    {
        private readonly ProcessFilterSpec _processFilter = ProcessFilterSpec.Empty;

        public CaptureAnalyzerWindow()
        {
            InitializeComponent();

            CaptureAnalyzerWindowText.Title = Localization.GetLocalizedString("ANALYSIS_SELECT_TITLE");
            SelectLabel.Content = Localization.GetLocalizedString("ANALYSIS_SELECT_LABEL");
            CancelButton.Content = Localization.GetLocalizedString("ANALYSIS_SELECT_CANCEL");
            OkButton.Content = Localization.GetLocalizedString("ANALYSIS_SELECT_START");
            AnalyzeProcessFilteredText.Text = Localization.GetLocalizedString("ANALYSIS_PROCESS_FILTER_OPTION");
            ProcessFilterSummary.Text = Localization.GetLocalizedString("ANALYSIS_PROCESS_FILTER_UNAVAILABLE");
        }

        // The capture tab's process filter, offered as an opt-in; unchecked, the analysis covers the full capture.
        internal CaptureAnalyzerWindow(ProcessFilterSpec currentProcessFilter)
            : this()
        {
            if (ProcessFilter.IsEmpty(currentProcessFilter))
            {
                return;
            }

            _processFilter = currentProcessFilter;
            AnalyzeProcessFilteredSessions.IsEnabled = true;
            ProcessFilterSummary.Text = Localization.GetLocalizedString(
                "ANALYSIS_PROCESS_FILTER_SUMMARY",
                Describe(currentProcessFilter.Include, "ANALYSIS_PROCESS_FILTER_ALL"),
                Describe(currentProcessFilter.Exclude, "ANALYSIS_PROCESS_FILTER_NONE"));
        }

        // The specification the analysis run snapshots: the tab's filter only when the option is checked.
        internal ProcessFilterSpec ProcessFilterForRun =>
            AnalyzeProcessFilteredSessions.IsEnabled && AnalyzeProcessFilteredSessions.IsChecked == true
                ? _processFilter
                : ProcessFilterSpec.Empty;

        private static string Describe(ImmutableArray<string> selectors, string emptyKey) =>
            selectors.IsDefaultOrEmpty
                ? Localization.GetLocalizedString(emptyKey)
                : string.Join(", ", selectors);

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
