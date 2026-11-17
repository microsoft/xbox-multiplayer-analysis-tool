// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            CaptureLimit.ToolTip = Localization.GetLocalizedString(
                "PROXY_PREFS_CAPTURE_LIMIT_TOOLTIP",
                ResponseStreamingOptions.MinimumCaptureLimitMegabytes,
                ResponseStreamingOptions.MaximumCaptureLimitMegabytes);
        }

        // Save: every field is checked before any is committed, so an invalid one leaves the preferences unchanged. The
        // streaming check boxes are drafts until then, like the text boxes.
        internal void CommitPreferences()
        {
            (ushort first, ushort last) = ReadPortRange();
            int megabytes = ReadCaptureLimit();
            var preferences = (PreferencesModel)DataContext;
            preferences.FirstPort = first;
            preferences.LastPort = last;
            preferences.CaptureLimitMegabytes = megabytes;
            AutoStreamCheckBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
            StreamAllCheckBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        }

        private (ushort First, ushort Last) ReadPortRange()
        {
            if (!ProxyPortChangeWindow.TryParsePort(StartingPort.Text, out ushort first) ||
                !ProxyPortChangeWindow.TryParsePort(EndingPort.Text, out ushort last) || last < first)
            {
                throw new ArgumentException(Localization.GetLocalizedString("PROXY_PORT_RANGE_INVALID"));
            }
            return (first, last);
        }

        private int ReadCaptureLimit()
        {
            if (!int.TryParse(CaptureLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int megabytes) ||
                megabytes < ResponseStreamingOptions.MinimumCaptureLimitMegabytes ||
                megabytes > ResponseStreamingOptions.MaximumCaptureLimitMegabytes)
            {
                throw new ArgumentException(Localization.GetLocalizedString(
                    "PROXY_PREFS_CAPTURE_LIMIT_INVALID",
                    ResponseStreamingOptions.MinimumCaptureLimitMegabytes,
                    ResponseStreamingOptions.MaximumCaptureLimitMegabytes));
            }
            return megabytes;
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
