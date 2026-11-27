// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace XMAT
{
    // Text in a context menu that is not an action: a group's header or a note. A menu shows a separator as it is and
    // its arrow keys pass over it, so a label is a separator with text. Styles/Menus.xaml draws them.
    public abstract class MenuLabel : Separator
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(MenuLabel), new PropertyMetadata(null));

        protected MenuLabel()
        {
            // A menu gives each separator without a style of its own the style of its separator lines.
            SetResourceReference(StyleProperty, GetType());
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        // What assistive technology reads for the label.
        protected virtual string AccessibleText => Text;

        protected override AutomationPeer OnCreateAutomationPeer() => new MenuLabelAutomationPeer(this);

        // Read as text rather than as a separator.
        private sealed class MenuLabelAutomationPeer : FrameworkElementAutomationPeer
        {
            internal MenuLabelAutomationPeer(MenuLabel owner) : base(owner)
            {
            }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

            protected override string GetClassNameCore() => Owner.GetType().Name;

            protected override string GetNameCore()
            {
                string name = base.GetNameCore();
                return string.IsNullOrEmpty(name) ? ((MenuLabel)Owner).AccessibleText : name;
            }
        }
    }

    // A group's header: the group's name and, when its items act on one, their subject, such as "Host  example.com".
    // The items can then leave the subject out of their labels.
    public sealed class MenuGroupHeader : MenuLabel
    {
        public static readonly DependencyProperty SubjectProperty = DependencyProperty.Register(
            nameof(Subject), typeof(string), typeof(MenuGroupHeader), new PropertyMetadata(null));

        public string Subject
        {
            get => (string)GetValue(SubjectProperty);
            set => SetValue(SubjectProperty, value);
        }

        protected override string AccessibleText => string.IsNullOrEmpty(Subject) ? Text : Text + " " + Subject;
    }

    // Information that can't be chosen, such as why an action is missing or a rule that also applies.
    public sealed class MenuNote : MenuLabel
    {
    }
}
