// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Controls;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls
{
    /// <summary>
    /// Interaction logic for RuleViolationDetail.xaml
    /// </summary>
    public partial class RuleViolationDetail : UserControl
    {
        public static string ViolationIconName => Localization.GetLocalizedString("LTA_VIOLATION_ICON_NAME");

        // Names a request-number button, which shows the number alone: a binding's StringFormat, so {0} is kept.
        public static string GoToRequestFormat => Localization.GetLocalizedString("LTA_GO_TO_REQUEST", "{0}");

        public RuleViolationDetail()
        {
            InitializeComponent();
        }
    }
}
