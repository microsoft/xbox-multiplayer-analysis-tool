// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using System.Windows;
using System.Windows.Controls;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Interaction logic for CaptureOptionsFull.xaml
    /// </summary>
    public partial class CaptureOptionsFull : UserControl, ICaptureOptions
    {
        public CaptureOptionsFull()
        {
            InitializeComponent();

            PortRangeLabel.Text = Localization.GetLocalizedString("PROXY_PREFS_RANGE");
            ProxyPoolLabel.Text = Localization.GetLocalizedString("PROXY_PREFS_POOL");
            DisablePromptLabel.Content = Localization.GetLocalizedString("PROXY_PREFS_PROMPT");
            GeneralLabel.Text = Localization.GetLocalizedString("PROXY_PREFS_GENERAL");
        }

        private void CaptureOptionsFull_Loaded(object sender, RoutedEventArgs e)
        {
            var preferencesModel = DataContext as PreferencesModel;
            ProxyPortPool.Initialize(preferencesModel.FirstPort, preferencesModel.LastPort);
        }

        // TODO: Do we need these and/or the ICaptureOptions interface?
        public void Initialize(ICaptureMethod captureMethod)
        {
        }

        public void EnableOptions()
        {
        }

        public void DisableOptions()
        {
        }
    }
}
