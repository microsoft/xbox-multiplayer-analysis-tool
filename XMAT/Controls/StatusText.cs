// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;

namespace XMAT
{
    // An HTTP status in a table's cell, with its meaning (StatusMeanings). The new look (Styles/Status.xaml) shows a
    // problem's or a warning's code in its color after its icon, and other codes as the row's text. Without a look's
    // style, as in the original look, the code shows as a table's text columns show text. Screen readers hear the code,
    // and for a problem or a warning what it means, since they don't see the color or the icon.
    public sealed class StatusText : Control
    {
        public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
            nameof(Status), typeof(string), typeof(StatusText),
            new PropertyMetadata(null, (text, e) => ((StatusText)text).Meaning = StatusMeanings.OfHttpStatus((string)e.NewValue)));

        private static readonly DependencyPropertyKey MeaningPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(Meaning), typeof(StatusMeaning), typeof(StatusText), new PropertyMetadata(StatusMeaning.Routine));

        public static readonly DependencyProperty MeaningProperty = MeaningPropertyKey.DependencyProperty;

        static StatusText()
        {
            // The code as a text column sets its text, until a look's style gives the status its own template.
            var code = new FrameworkElementFactory(typeof(TextBlock));
            code.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(StatusProperty));
            code.SetValue(StyleProperty, DataGridTextColumn.DefaultElementStyle);
            var template = new ControlTemplate(typeof(StatusText)) { VisualTree = code };
            template.Seal();
            TemplateProperty.OverrideMetadata(typeof(StatusText), new FrameworkPropertyMetadata(template));
            FocusableProperty.OverrideMetadata(typeof(StatusText), new FrameworkPropertyMetadata(false));
            IsTabStopProperty.OverrideMetadata(typeof(StatusText), new FrameworkPropertyMetadata(false));
        }

        // The status as the capture records it, such as "429", or empty for a request without one.
        public string Status
        {
            get => (string)GetValue(StatusProperty);
            set => SetValue(StatusProperty, value);
        }

        public StatusMeaning Meaning
        {
            get => (StatusMeaning)GetValue(MeaningProperty);
            private set => SetValue(MeaningPropertyKey, value);
        }

        // What screen readers hear: the code, and what a problem or a warning means.
        internal string SpokenText => Meaning switch
        {
            StatusMeaning.Problem => Localization.GetLocalizedString("STATUS_TEXT_PROBLEM", Status),
            StatusMeaning.Attention => Localization.GetLocalizedString("STATUS_TEXT_ATTENTION", Status),
            _ => Status ?? string.Empty,
        };

        protected override AutomationPeer OnCreateAutomationPeer() => new StatusTextAutomationPeer(this);

        private sealed class StatusTextAutomationPeer(StatusText owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

            protected override string GetClassNameCore() => nameof(StatusText);

            protected override string GetNameCore() => ((StatusText)Owner).SpokenText;

            // The name says it all: the code and the icon inside aren't read again.
            protected override List<AutomationPeer> GetChildrenCore() => null;
        }
    }

    // An HTTP status's meaning (StatusMeanings), for what a status sets beyond its cell, such as its table row's wash
    // (STATUS-2).
    public sealed class HttpStatusMeaningConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            StatusMeanings.OfHttpStatus(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
