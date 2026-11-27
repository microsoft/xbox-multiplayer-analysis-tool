// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace XMAT
{
    // One value of a histogram and its amount, such as a source and the packets it sent.
    public sealed record HistogramValue(string Label, long Amount);

    // How a histogram names its amounts and its values. AmountText gives an amount with its unit, such as "1,280 packets";
    // ValuesText a number of values, such as "39 sources".
    public sealed record HistogramUnits(Func<long, string> AmountText, Func<int, string> ValuesText)
    {
        // Counts add up, so a histogram of them shows their total, each value's share of it and, collapsed, the rest of
        // the values in one row. Averages don't: give them a caption to show in place of the total, such as "Bytes per
        // packet".
        public string Caption { get; init; }

        // The row of the rest of the values, such as "33 more sources". Left unset, it reads "33 more values".
        public Func<int, string> MoreValuesText { get; init; }

        internal bool AddUp => Caption == null;
    }

    // A histogram as shown (HistogramView): its title and totals, its rows while collapsed, and a row for every value.
    // Values come largest first, and values of the same amount in the order of their labels. Collapsed, a histogram of
    // more than seven values shows the six largest and, for amounts that add up, the rest together, as a column summary
    // does; Show all lists every value.
    public sealed class HistogramModel : INotifyPropertyChanged
    {
        internal const int MaxRows = ColumnSummaryCounts.MaxRows;

        private HistogramModel(
            string title, string totals, string accessibleName, IReadOnlyList<HistogramRow> rows,
            IReadOnlyList<HistogramRow> allRows, string showAllText)
        {
            Title = title;
            Totals = totals;
            AccessibleName = accessibleName;
            Rows = rows;
            AllRows = allRows;
            ShowAllText = showAllText;
        }

        // Never raised: a histogram does not change. Implemented so bindings don't watch it through property descriptors.
        public event PropertyChangedEventHandler PropertyChanged
        {
            add { }
            remove { }
        }

        public string Title { get; }

        // "672 packets · 39 sources", or for averages their caption: "Bytes per packet · 41 sources".
        public string Totals { get; }

        // What screen readers call the list of rows: "Packets Received by Source: 672 packets, 39 sources".
        public string AccessibleName { get; }

        // The rows while collapsed.
        public IReadOnlyList<HistogramRow> Rows { get; }

        public IReadOnlyList<HistogramRow> AllRows { get; }

        // Some values are left out while collapsed, so Show all has more to show.
        public bool CanShowAll => AllRows.Count > Rows.Count(row => !row.IsMore);

        // "Show all 39 sources".
        public string ShowAllText { get; }

        public string ShowFewerText => Localization.GetLocalizedString("HISTOGRAM_SHOW_FEWER");

        public bool IsEmpty => AllRows.Count == 0;

        public string EmptyText => Localization.GetLocalizedString("HISTOGRAM_NO_VALUES");

        public static HistogramModel Create(string title, IEnumerable<HistogramValue> values, HistogramUnits units)
        {
            List<HistogramValue> sorted = (values ?? Enumerable.Empty<HistogramValue>())
                .OrderByDescending(value => value.Amount)
                .ThenBy(value => value.Label, StringComparer.Ordinal)
                .ToList();
            long total = sorted.Sum(value => value.Amount);
            long largest = sorted.Count > 0 ? sorted[0].Amount : 0;
            List<HistogramRow> all = RowsOf(sorted, total, largest, units);

            IReadOnlyList<HistogramRow> rows = all;
            if (sorted.Count > MaxRows)
            {
                List<HistogramValue> shown = sorted.Take(MaxRows - 1).ToList();
                if (units.AddUp)
                {
                    int more = sorted.Count - shown.Count;
                    long rest = total - shown.Sum(value => value.Amount);
                    // The rest's bar is measured against the same scale as the values', and can be the longest.
                    long scale = Math.Max(largest, rest);
                    string label = units.MoreValuesText?.Invoke(more) ??
                        ColumnSummaryModel.Counted(more, "COLUMN_SUMMARY_MORE_VALUE", "COLUMN_SUMMARY_MORE_VALUES");
                    rows = RowsOf(shown, total, scale, units)
                        .Append(new HistogramRow(label, rest, total, scale, units, isMore: true))
                        .ToList();
                }
                else
                {
                    rows = all.Take(shown.Count).ToList();
                }
            }

            string valueCount = units.ValuesText(sorted.Count);
            string measure = units.AddUp ? units.AmountText(total) : units.Caption;
            return new HistogramModel(
                title,
                Localization.GetLocalizedString("COLUMN_SUMMARY_TOTALS", measure, valueCount),
                Localization.GetLocalizedString("HISTOGRAM_NAME", title, string.Join(", ", measure, valueCount)),
                rows,
                all,
                Localization.GetLocalizedString("HISTOGRAM_SHOW_ALL", valueCount));
        }

        private static List<HistogramRow> RowsOf(List<HistogramValue> values, long total, long scale, HistogramUnits units) =>
            values.Select(value => new HistogramRow(value.Label, value.Amount, total, scale, units, isMore: false)).ToList();
    }

    // One row of a histogram: a value, or the rest of the values, with its amount, its share of the total when amounts
    // add up, and its bar.
    public sealed class HistogramRow : INotifyPropertyChanged
    {
        internal HistogramRow(string label, long amount, long total, long scale, HistogramUnits units, bool isMore)
        {
            Label = label;
            Amount = amount;
            IsMore = isMore;
            AmountText = ColumnSummaryModel.Number(amount);
            string amountName = units.AmountText(amount);
            if (units.AddUp)
            {
                (int percent, bool underOne) = ColumnSummaryCounts.Percent(amount, total);
                ShareText = underOne
                    ? Localization.GetLocalizedString("COLUMN_SUMMARY_UNDER_ONE_PERCENT")
                    : Localization.GetLocalizedString("COLUMN_SUMMARY_PERCENT", percent);
                AccessibleName = Localization.GetLocalizedString(
                    "COLUMN_SUMMARY_VALUE_NAME",
                    label,
                    amountName,
                    underOne
                        ? Localization.GetLocalizedString("COLUMN_SUMMARY_UNDER_ONE_PERCENT_NAME")
                        : Localization.GetLocalizedString("COLUMN_SUMMARY_PERCENT_NAME", percent));
            }
            else
            {
                AccessibleName = Localization.GetLocalizedString("HISTOGRAM_VALUE_NAME", label, amountName);
            }

            Fraction = scale > 0 ? (double)amount / scale : 0;
        }

        // Never raised: a row does not change. Implemented so bindings don't watch it through property descriptors.
        public event PropertyChangedEventHandler PropertyChanged
        {
            add { }
            remove { }
        }

        public string Label { get; }

        public long Amount { get; }

        public string AmountText { get; }

        // "38%", or null when amounts don't add up.
        public string ShareText { get; }

        // What screen readers read: "35.186.224.31, 254 packets, 38 percent".
        public string AccessibleName { get; }

        // The bar's length as a share of the longest bar, and the rest of its lane.
        public double Fraction { get; }

        public GridLength BarWidth => new(Fraction, GridUnitType.Star);

        public GridLength BarRemainder => new(1 - Fraction, GridUnitType.Star);

        // The row of the rest of the values.
        public bool IsMore { get; }
    }
}
