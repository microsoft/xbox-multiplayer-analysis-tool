// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    // Lays out a search box's typed text and, after it, its summary or its problem. The summary, such as "12 of 340", is
    // short and keeps its width. A problem with what was typed can be long, so it takes only the room the typed text
    // leaves, but at least half, and trims to fit: neither hides the other. Its children, in order: a hidden copy of the
    // typed text, which tells how wide it is (a text box's own text host reports the width it was last given rather than
    // its text's); the text host; the summary; and the problem.
    public sealed class SearchTextPanel : Panel
    {
        private double _typed;
        private double _summary;
        private double _problem;

        protected override Size MeasureOverride(Size availableSize)
        {
            UIElement typed = Child(0);
            UIElement text = Child(1);
            UIElement summary = Child(2);
            UIElement problem = Child(3);
            var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
            typed?.Measure(unbounded);
            summary?.Measure(unbounded);
            problem?.Measure(unbounded);
            _typed = typed?.DesiredSize.Width ?? 0;
            _summary = summary?.DesiredSize.Width ?? 0;
            _problem = problem?.DesiredSize.Width ?? 0;

            double natural = _typed + _summary + _problem;
            double width = double.IsInfinity(availableSize.Width) ? natural : availableSize.Width;
            (double textWidth, double problemWidth) = Share(width);
            text?.Measure(new Size(textWidth, availableSize.Height));
            // A problem given its whole width is measured without a limit: layout rounding can leave its width a fraction
            // of a pixel narrower than its text, which would trim it.
            problem?.Measure(new Size(problemWidth < _problem ? problemWidth : double.PositiveInfinity, availableSize.Height));

            double height = 0;
            foreach (UIElement child in InternalChildren)
            {
                height = Math.Max(height, child.DesiredSize.Height);
            }

            return new Size(Math.Min(natural, width), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            (double textWidth, double problemWidth) = Share(finalSize.Width);
            double summaryWidth = Math.Min(_summary, finalSize.Width);
            var textSlot = new Rect(0, 0, textWidth, finalSize.Height);
            Child(0)?.Arrange(textSlot);
            Child(1)?.Arrange(textSlot);
            Child(2)?.Arrange(new Rect(textWidth, 0, summaryWidth, finalSize.Height));
            Child(3)?.Arrange(new Rect(textWidth + summaryWidth, 0, problemWidth, finalSize.Height));
            return finalSize;
        }

        // The typed text's and the problem's widths in WIDTH, beside the summary at its own width.
        private (double Text, double Problem) Share(double width)
        {
            double room = Math.Max(0, width - _summary);
            double problem = Math.Min(_problem, Math.Max(room - _typed, room / 2));
            return (room - problem, problem);
        }

        private UIElement Child(int index) => index < InternalChildren.Count ? InternalChildren[index] : null;
    }
}
