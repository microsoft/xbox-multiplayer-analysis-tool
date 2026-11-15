// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CaptureAnalysisEngine;
using XMAT.XboxLiveCaptureAnalysis.Models;

namespace XMAT.XboxLiveCaptureAnalysis
{
    // Colors a TextBlock or a control for an analysis issue level with the theme's brushes for it, which follow theme
    // changes and Windows high contrast. Count also shows an issue count as the element's text: the new look names the
    // level, such as "3 errors", so that it is not told by color alone, and the original look shows the number.
    internal static class IssueBadge
    {
        public static readonly DependencyProperty LevelProperty = DependencyProperty.RegisterAttached(
            "Level", typeof(ViolationLevel?), typeof(IssueBadge), new PropertyMetadata(null, OnLevelChanged));

        public static readonly DependencyProperty CountProperty = DependencyProperty.RegisterAttached(
            "Count", typeof(ValidationIssueCount), typeof(IssueBadge), new PropertyMetadata(null, OnCountChanged));

        // Bound to LookState, so the text follows the look without the state holding on to the element.
        private static readonly DependencyProperty IsNewLookProperty = DependencyProperty.RegisterAttached(
            "IsNewLook", typeof(bool), typeof(IssueBadge), new PropertyMetadata(true, OnIsNewLookChanged));

        public static ViolationLevel? GetLevel(DependencyObject element) =>
            (ViolationLevel?)element.GetValue(LevelProperty);

        public static void SetLevel(DependencyObject element, ViolationLevel? value) =>
            element.SetValue(LevelProperty, value);

        public static ValidationIssueCount GetCount(DependencyObject element) =>
            (ValidationIssueCount)element.GetValue(CountProperty);

        public static void SetCount(DependencyObject element, ValidationIssueCount value) =>
            element.SetValue(CountProperty, value);

        private static void OnLevelChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            DependencyProperty background = element is TextBlock ? TextBlock.BackgroundProperty : Control.BackgroundProperty;
            DependencyProperty foreground = element is TextBlock ? TextBlock.ForegroundProperty : Control.ForegroundProperty;
            if (e.NewValue is not ViolationLevel level || element is not FrameworkElement target)
            {
                element.ClearValue(background);
                element.ClearValue(foreground);
                return;
            }

            (string backgroundKey, string foregroundKey) = level switch
            {
                ViolationLevel.Error => ("IssueErrorBackgroundBrush", "IssueErrorForegroundBrush"),
                ViolationLevel.Warning => ("IssueWarningBackgroundBrush", "IssueWarningForegroundBrush"),
                _ => ("IssueInfoBackgroundBrush", "IssueInfoForegroundBrush")
            };
            target.SetResourceReference(background, backgroundKey);
            target.SetResourceReference(foreground, foregroundKey);
        }

        private static void OnCountChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (!BindingOperations.IsDataBound(element, IsNewLookProperty))
            {
                BindingOperations.SetBinding(element, IsNewLookProperty,
                    new Binding(nameof(LookState.IsNewLook)) { Source = LookState.Current, Mode = BindingMode.OneWay });
            }
            SetLevel(element, (e.NewValue as ValidationIssueCount)?.IssueLevel);
            ShowCount(element);
        }

        private static void OnIsNewLookChanged(DependencyObject element, DependencyPropertyChangedEventArgs e) =>
            ShowCount(element);

        private static void ShowCount(DependencyObject element)
        {
            ValidationIssueCount count = GetCount(element);
            string text = count == null ? null
                : (bool)element.GetValue(IsNewLookProperty) && count.IssueLevel != ViolationLevel.Info ? count.Description
                : count.ErrorsAndWarnings.ToString(CultureInfo.CurrentCulture);
            switch (element)
            {
                case TextBlock textBlock:
                    textBlock.Text = text;
                    break;
                case ContentControl control:
                    control.Content = text;
                    break;
            }
        }
    }
}
