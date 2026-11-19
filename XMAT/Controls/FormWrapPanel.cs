// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    // Options with fields, such as the composer's connection options. In the original look, a WrapPanel. In the new
    // look, a form: each child that isn't a field (IsField) starts a row at the left edge, and the field after it sits in
    // one column for every row, ColumnGap after the widest label that has a field, filling the rest of the row up to the
    // field's MaxWidth. A label without a field has its row to itself. Rows are RowGap apart, each as tall as its tallest
    // child, which takes its own vertical alignment there.
    public sealed class FormWrapPanel : WrapPanel
    {
        public static readonly DependencyProperty IsFieldProperty = DependencyProperty.RegisterAttached(
            "IsField", typeof(bool), typeof(FormWrapPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
            nameof(ColumnGap), typeof(double), typeof(FormWrapPanel),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsValidLength);

        public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
            nameof(RowGap), typeof(double), typeof(FormWrapPanel),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsValidLength);

        public FormWrapPanel()
        {
            // Lays the children out again, the look's way, when the look changes.
            PropertyChangedEventManager.AddHandler(LookState.Current, LookState_Changed, nameof(LookState.IsNewLook));
        }

        public double ColumnGap
        {
            get => (double)GetValue(ColumnGapProperty);
            set => SetValue(ColumnGapProperty, value);
        }

        public double RowGap
        {
            get => (double)GetValue(RowGapProperty);
            set => SetValue(RowGapProperty, value);
        }

        public static bool GetIsField(UIElement element) => (bool)element.GetValue(IsFieldProperty);

        public static void SetIsField(UIElement element, bool value) => element.SetValue(IsFieldProperty, value);

        protected override Size MeasureOverride(Size constraint)
        {
            if (!LookState.Current.IsNewLook)
            {
                return base.MeasureOverride(constraint);
            }

            List<Row> rows = Rows();
            double width = 0;
            foreach (Row row in rows)
            {
                // A label beside a field takes the width it needs; one on its own may wrap to the panel's width.
                row.Label.Measure(row.Field == null
                    ? new Size(constraint.Width, double.PositiveInfinity)
                    : new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (row.Field == null)
                {
                    width = Math.Max(width, row.Label.DesiredSize.Width);
                }
            }

            double fieldX = FieldX(rows);
            double height = 0;
            for (int index = 0; index < rows.Count; index++)
            {
                Row row = rows[index];
                if (row.Field != null)
                {
                    row.Field.Measure(new Size(Math.Max(0, constraint.Width - fieldX), double.PositiveInfinity));
                    width = Math.Max(width, fieldX + row.Field.DesiredSize.Width);
                }

                height += row.Height + (index > 0 ? RowGap : 0);
            }

            return new Size(Math.Min(width, constraint.Width), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (!LookState.Current.IsNewLook)
            {
                return base.ArrangeOverride(finalSize);
            }

            List<Row> rows = Rows();
            double fieldX = FieldX(rows);
            double y = 0;
            foreach (Row row in rows)
            {
                double height = row.Height;
                row.Label.Arrange(new Rect(0, y, Math.Min(row.Label.DesiredSize.Width, finalSize.Width), height));
                if (row.Field != null)
                {
                    double fieldWidth = Math.Max(0, finalSize.Width - fieldX);
                    if (row.Field is FrameworkElement field)
                    {
                        fieldWidth = Math.Min(fieldWidth, field.MaxWidth);
                    }

                    row.Field.Arrange(new Rect(fieldX, y, fieldWidth, height));
                }

                y += height + RowGap;
            }

            return finalSize;
        }

        private static bool IsValidLength(object value) => value is double length && length >= 0 && double.IsFinite(length);

        // Where the fields start: ColumnGap after the widest label that has a field.
        private double FieldX(List<Row> rows)
        {
            double labelWidth = 0;
            foreach (Row row in rows)
            {
                if (row.Field != null)
                {
                    labelWidth = Math.Max(labelWidth, row.Label.DesiredSize.Width);
                }
            }

            return labelWidth + ColumnGap;
        }

        // The visible children in rows: a label, and the field after it if there is one.
        private List<Row> Rows()
        {
            var rows = new List<Row>();
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed)
                {
                    continue;
                }

                if (GetIsField(child) && rows.Count > 0 && rows[^1].Field == null && !GetIsField(rows[^1].Label))
                {
                    rows[^1] = rows[^1] with { Field = child };
                }
                else
                {
                    rows.Add(new Row(child, null));
                }
            }

            return rows;
        }

        private void LookState_Changed(object sender, PropertyChangedEventArgs e) => InvalidateMeasure();

        private readonly record struct Row(UIElement Label, UIElement Field)
        {
            internal double Height => Math.Max(Label.DesiredSize.Height, Field?.DesiredSize.Height ?? 0);
        }
    }
}
