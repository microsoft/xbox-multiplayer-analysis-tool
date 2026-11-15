// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    // A region that fills the height its parent gives it but asks for no more than its MinHeight when its height is
    // unlimited, as in a scrolling page. A list or an editor in a FlexFill takes the room that the rest of the page
    // leaves and scrolls its own content, and the page scrolls only when even MinHeight doesn't fit.
    public sealed class FlexFill : Decorator
    {
        private bool _unlimited;
        private double _measuredHeight = double.NaN;
        private double _arrangedHeight;

        protected override Size MeasureOverride(Size constraint)
        {
            UIElement child = Child;
            _unlimited = double.IsInfinity(constraint.Height);
            if (child == null)
            {
                return new Size();
            }

            if (!_unlimited)
            {
                child.Measure(constraint);
                return child.DesiredSize;
            }

            // Lay the child out for the height this region was last given, but ask only for MinHeight, so the child's
            // content never makes the page longer.
            _measuredHeight = Math.Max(MinHeight, _arrangedHeight);
            child.Measure(new Size(constraint.Width, _measuredHeight));
            return new Size(child.DesiredSize.Width, 0);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            _arrangedHeight = arrangeSize.Height;
            if (_unlimited && Math.Abs(_measuredHeight - Math.Max(MinHeight, arrangeSize.Height)) > 0.5)
            {
                // Lay the child out again for the height it actually has; this element's own size doesn't change.
                InvalidateMeasure();
            }

            return base.ArrangeOverride(arrangeSize);
        }
    }
}
