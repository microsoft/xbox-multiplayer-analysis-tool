// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows.Controls;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

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

        internal void CommitPortRange()
        {
            if (!ProxyPortChangeWindow.TryParsePort(StartingPort.Text, out var first) ||
                !ProxyPortChangeWindow.TryParsePort(EndingPort.Text, out var last) || last < first)
            {
                throw new ArgumentException(Localization.GetLocalizedString("PROXY_PORT_RANGE_INVALID"));
            }
            var preferences = (PreferencesModel)DataContext;
            preferences.FirstPort = first;
            preferences.LastPort = last;
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
