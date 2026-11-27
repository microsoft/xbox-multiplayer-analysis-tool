// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;

namespace XMAT
{
    // The new look's confirmation window: a ConfirmPanel, sized to it and centered on its owner. ShowDialog returns true
    // only when the action's button was chosen; Cancel, Esc, Enter and closing the window all answer no. Focus starts on
    // Cancel.
    internal sealed class ConfirmWindow : Window
    {
        internal ConfirmWindow(ConfirmPanel panel, string title)
        {
            Panel = panel;
            Title = title;
            Content = panel;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            UseLayoutRounding = true;
            SetResourceReference(BackgroundProperty, "SurfacePanelBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            SetResourceReference(FontSizeProperty, "BaseFontSize");
            panel.Answered += (_, confirmed) => DialogResult = confirmed;
            Loaded += (_, _) => panel.CancelButton.Focus();
        }

        internal ConfirmPanel Panel { get; }
    }
}
