// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XMAT
{
    // One option of a choice, shown as a card: a picture, a title, a sentence about the option and an optional note,
    // such as what the option needs or why it isn't available. Cards with the same GroupName make one choice, as radio
    // buttons do: Tab reaches the chosen card, and the arrow keys choose the next or previous card that is available.
    // Content, such as a field the option needs, shows under the text while the card is chosen. Styles/ChoiceCards.xaml
    // draws the card.
    public class ChoiceCard : RadioButton
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
            nameof(Icon), typeof(Geometry), typeof(ChoiceCard));

        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(ChoiceCard));

        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(ChoiceCard));

        public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
            nameof(Note), typeof(string), typeof(ChoiceCard));

        public static readonly DependencyProperty NoteIconProperty = DependencyProperty.Register(
            nameof(NoteIcon), typeof(Geometry), typeof(ChoiceCard));

        public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
            nameof(Orientation), typeof(Orientation), typeof(ChoiceCard), new PropertyMetadata(Orientation.Vertical));

        public ChoiceCard()
        {
            Loaded += (_, _) => UpdateTabStops();
            IsEnabledChanged += (_, _) => UpdateTabStops();
        }

        // The picture: strokes on a 40px square, such as those in Styles/CaptureIcons.xaml.
        public Geometry Icon
        {
            get => (Geometry)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        // A line under the description: what the option needs, or why it isn't available.
        public string Note
        {
            get => (string)GetValue(NoteProperty);
            set => SetValue(NoteProperty, value);
        }

        // The note's icon: strokes on a 14px square.
        public Geometry NoteIcon
        {
            get => (Geometry)GetValue(NoteIconProperty);
            set => SetValue(NoteIconProperty, value);
        }

        // Vertical puts the picture above the text; Horizontal puts it beside the text.
        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        // The cards of this card's choice, in the order they are laid out: those with its GroupName in its window, or
        // without a GroupName, the cards beside it in its panel.
        internal IReadOnlyList<ChoiceCard> Group()
        {
            var cards = new List<ChoiceCard>();
            if (string.IsNullOrEmpty(GroupName))
            {
                DependencyObject parent = VisualTreeHelper.GetParent(this);
                int count = parent == null ? 0 : VisualTreeHelper.GetChildrenCount(parent);
                for (int index = 0; index < count; index++)
                {
                    if (VisualTreeHelper.GetChild(parent, index) is ChoiceCard { GroupName: null or "" } card)
                    {
                        cards.Add(card);
                    }
                }
            }
            else
            {
                DependencyObject root = this;
                for (DependencyObject parent = VisualTreeHelper.GetParent(root); parent != null;
                     parent = VisualTreeHelper.GetParent(root))
                {
                    root = parent;
                }

                Collect(root, GroupName, cards);
            }

            if (!cards.Contains(this))
            {
                cards.Insert(0, this);
            }

            return cards;
        }

        protected override void OnChecked(RoutedEventArgs e)
        {
            base.OnChecked(e);
            UpdateTabStops();
        }

        protected override void OnUnchecked(RoutedEventArgs e)
        {
            base.OnUnchecked(e);
            UpdateTabStops();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = e.OriginalSource == this && Keyboard.Modifiers == ModifierKeys.None ? Step(e.Key) : 0;
            if (step == 0)
            {
                base.OnKeyDown(e);
                return;
            }

            if (Next(step) is ChoiceCard next)
            {
                next.IsChecked = true;
                next.Focus();
            }

            e.Handled = true;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ChoiceCardAutomationPeer(this);

        private static void Collect(DependencyObject parent, string group, List<ChoiceCard> cards)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is ChoiceCard card && card.GroupName == group)
                {
                    cards.Add(card);
                }

                Collect(child, group, cards);
            }
        }

        // -1 for the previous card, 1 for the next, in reading order.
        private int Step(Key key)
        {
            int forward = FlowDirection == FlowDirection.RightToLeft ? -1 : 1;
            return key switch
            {
                Key.Up => -1,
                Key.Down => 1,
                Key.Left => -forward,
                Key.Right => forward,
                _ => 0,
            };
        }

        // The next available card STEP away, wrapping around the group, or null when no other card is available.
        private ChoiceCard Next(int step)
        {
            IReadOnlyList<ChoiceCard> group = Group();
            int index = IndexOf(group, this);
            for (int offset = 1; offset < group.Count; offset++)
            {
                ChoiceCard candidate = group[(((index + step * offset) % group.Count) + group.Count) % group.Count];
                if (candidate.IsEnabled && candidate.IsVisible)
                {
                    return candidate;
                }
            }

            return null;
        }

        // Only one card of a choice is a Tab stop: the chosen card, or while none is chosen, the first available one.
        private void UpdateTabStops()
        {
            IReadOnlyList<ChoiceCard> group = Group();
            ChoiceCard stop = group.FirstOrDefault(card => card.IsChecked == true && card.IsEnabled) ??
                group.FirstOrDefault(card => card.IsEnabled);
            foreach (ChoiceCard card in group)
            {
                card.IsTabStop = card == stop;
            }
        }

        private static int IndexOf(IReadOnlyList<ChoiceCard> group, ChoiceCard card)
        {
            for (int index = 0; index < group.Count; index++)
            {
                if (group[index] == card)
                {
                    return index;
                }
            }

            return -1;
        }

        // A radio button named by the card's title, with its description and note as help text, and its place among
        // the cards of its choice.
        private sealed class ChoiceCardAutomationPeer : RadioButtonAutomationPeer
        {
            // AutomationProperties.PositionInSet and SizeOfSet when they aren't set.
            private const int NotSet = -1;

            internal ChoiceCardAutomationPeer(ChoiceCard owner)
                : base(owner)
            {
            }

            private ChoiceCard Card => (ChoiceCard)Owner;

            protected override string GetNameCore()
            {
                string name = base.GetNameCore();
                return string.IsNullOrEmpty(name) ? Card.Title ?? string.Empty : name;
            }

            protected override string GetHelpTextCore()
            {
                string help = base.GetHelpTextCore();
                return string.IsNullOrEmpty(help)
                    ? string.Join(" ", new[] { Card.Description, Card.Note }.Where(text => !string.IsNullOrEmpty(text)))
                    : help;
            }

            protected override int GetPositionInSetCore()
            {
                int position = base.GetPositionInSetCore();
                return position != NotSet
                    ? position
                    : IndexOf(Card.Group(), Card) + 1;
            }

            protected override int GetSizeOfSetCore()
            {
                int size = base.GetSizeOfSetCore();
                return size != NotSet ? size : Card.Group().Count;
            }
        }
    }
}
