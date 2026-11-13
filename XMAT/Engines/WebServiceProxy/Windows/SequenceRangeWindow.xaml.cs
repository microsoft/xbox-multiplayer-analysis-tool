// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using XMAT.Operations.Replay;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Asks which numbers a URL with '#' is sent with, as Fiddler's composer does: Start At and End At, both 1 at first.
    /// The range is kept as typed, so a Start At with a leading zero pads every number to its width.
    /// </summary>
    public partial class SequenceRangeWindow : Window
    {
        internal SequenceRangeWindow()
        {
            InitializeComponent();
            Title = Localization.GetLocalizedString("SEQUENCE_TITLE");
            PromptText.Text = Localization.GetLocalizedString("SEQUENCE_PROMPT");
            StartLabel.Content = Localization.GetLocalizedString("SEQUENCE_START");
            EndLabel.Content = Localization.GetLocalizedString("SEQUENCE_END");
            // Each number has its own name for automation, as its label reads without the access key: a TextBox takes no
            // name from a Label's Target, and the name that LabeledBy gives exists only once something has made the label's
            // automation peer, which a screen reader that starts at the focused field has not.
            AutomationProperties.SetName(StartBox, Localization.GetLocalizedString("SEQUENCE_START_FIELD"));
            AutomationProperties.SetName(EndBox, Localization.GetLocalizedString("SEQUENCE_END_FIELD"));
            OkButton.Content = Localization.GetLocalizedString("SEQUENCE_OK");
            CancelButton.Content = Localization.GetLocalizedString("SEQUENCE_CANCEL");
            StartBox.Text = "1";
            EndBox.Text = "1";
            Validate();
            Loaded += (_, _) =>
            {
                StartBox.Focus();
                StartBox.SelectAll();
            };
        }

        // START-END as typed, without surrounding spaces, as ReplaySequence.TryParse reads it.
        internal string Range => StartBox.Text.Trim() + "-" + EndBox.Text.Trim();

        // The range that the user entered, or null when the user cancelled.
        internal static string Ask(Window owner)
        {
            var dialog = new SequenceRangeWindow { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.Range : null;
        }

        // Whether the range is one that a sequence can have; says how many requests it sends, or what is wrong.
        internal bool Validate()
        {
            bool valid = ReplaySequence.TryParse(Range, out ReplaySequence sequence);
            OkButton.IsEnabled = valid;
            CountText.Text = valid
                ? Localization.GetLocalizedString("SEQUENCE_COUNT", sequence.Count)
                : Localization.GetLocalizedString("SEQUENCE_INVALID");
            return valid;
        }

        private void Range_TextChanged(object sender, TextChangedEventArgs e) => Validate();

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (Validate())
                DialogResult = true;
        }
    }
}
