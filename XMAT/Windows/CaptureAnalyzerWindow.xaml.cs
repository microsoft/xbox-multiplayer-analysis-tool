// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for CaptureAnalyzerSelector.xaml
    /// </summary>
    public partial class CaptureAnalyzerWindow : Window
    {
        public CaptureAnalyzerWindow()
        {
            InitializeComponent();

            CaptureAnalyzerWindowText.Title = Localization.GetLocalizedString("ANALYSIS_SELECT_TITLE");
            SelectLabel.Content = Localization.GetLocalizedString("ANALYSIS_SELECT_LABEL");
            CancelButton.Content = Localization.GetLocalizedString("ANALYSIS_SELECT_CANCEL");
            OkButton.Content = Localization.GetLocalizedString("ANALYSIS_SELECT_START");
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
