// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows.Controls;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

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
            StreamingLabel.Text = Localization.GetLocalizedString("PROXY_PREFS_STREAMING");
            AutoStreamLabel.Text = Localization.GetLocalizedString("PROXY_PREFS_AUTO_STREAM");
            StreamAllLabel.Text = Localization.GetLocalizedString("PROXY_PREFS_STREAM_ALL");
            StreamAllCheckBox.ToolTip = Localization.GetLocalizedString("PROXY_PREFS_STREAM_ALL_TOOLTIP");
            StreamingNote.Text = Localization.GetLocalizedString("PROXY_PREFS_STREAM_NOTE");
            CaptureLimitLabel.Text = Localization.GetLocalizedString("PROXY_PREFS_CAPTURE_LIMIT");
            CaptureLimit.ToolTip = Localization.GetLocalizedString("PROXY_PREFS_CAPTURE_LIMIT_TOOLTIP");
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

        internal void CommitCaptureLimit()
        {
            if (!int.TryParse(CaptureLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int megabytes) ||
                megabytes < ResponseStreamingOptions.MinimumCaptureLimitMegabytes ||
                megabytes > ResponseStreamingOptions.MaximumCaptureLimitMegabytes)
            {
                throw new ArgumentException(Localization.GetLocalizedString("PROXY_PREFS_CAPTURE_LIMIT_INVALID"));
            }
            ((PreferencesModel)DataContext).CaptureLimitMegabytes = megabytes;
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
