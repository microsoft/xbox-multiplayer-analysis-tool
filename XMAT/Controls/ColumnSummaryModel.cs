// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace XMAT
{
    // What a table tells ColumnSummary about its columns.
    public interface IColumnSummarySource
    {
        // How COLUMN is summarized, or null when it has no summary.
        ColumnSummaryColumn Describe(DataGridColumn column);
    }

    // The color of a value's bar: the neutral bar, or the timeline's color for a response status.
    public enum ColumnSummaryBar
    {
        Neutral,
        NoStatus,
        Success,
        Redirect,
        ClientError,
        ServerError,
    }

    public static class ColumnSummaryBars
    {
        // The timeline's status colors: none or not a number, 2xx, 3xx, 4xx, and 5xx or above.
        public static ColumnSummaryBar ForHttpStatus(string status) =>
            int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
                ? (code / 100) switch
                {
                    2 => ColumnSummaryBar.Success,
                    3 => ColumnSummaryBar.Redirect,
                    4 => ColumnSummaryBar.ClientError,
                    >= 5 => ColumnSummaryBar.ServerError,
                    _ => ColumnSummaryBar.NoStatus,
                }
                : ColumnSummaryBar.NoStatus;
    }

    // One of a column's values, how many of the table's rows have it, and the first of those rows.
    public sealed record ColumnSummaryValue(string Value, int Count, object Row);

    // How a summary reads one column and what choosing one of its values does. ValueOf gives a row's value as text,
    // null or empty when the row has none. Texts left unset read "No value", "N rows" and "N more values"; without
    // Select the column has no filter.
    public sealed record ColumnSummaryColumn(Func<object, string> ValueOf)
    {
        // What rows without a value are called, such as "No status".
        public string EmptyValueText { get; init; }

        // The number of rows, such as "1,284 requests".
        public Func<int, string> RowCountText { get; init; }

        // The number of different values, such as "14 hosts", or null to leave it out.
        public Func<int, string> ValueCountText { get; init; }

        // The row for the less common values, such as "8 more hosts".
        public Func<int, string> MoreValuesText { get; init; }

        // The bar color of each value; the bars are neutral without it.
        public Func<string, ColumnSummaryBar> BarOf { get; init; }

        // Filters the table to a value, the same as choosing it in the column's filter. CanSelect says which values the
        // filter has; all of them when it is unset.
        public Action<ColumnSummaryValue> Select { get; init; }

        public Func<ColumnSummaryValue, bool> CanSelect { get; init; }

        // The footer: how to filter by the column, or that it has no filter.
        public string Hint { get; init; }

        // A column summarized by the text of its sort member, such as Host or ClientProcess.Name, with no filter. Null
        // when the column has no sort member.
        internal static ColumnSummaryColumn FromSortMember(DataGridColumn column)
        {
            string path = column?.SortMemberPath;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string[] names = path.Split('.');
            var properties = new Dictionary<(Type, string), PropertyInfo>();
            return new ColumnSummaryColumn(row =>
            {
                object value = row;
                foreach (string name in names)
                {
                    if (value == null)
                    {
                        return null;
                    }

                    Type type = value.GetType();
                    if (!properties.TryGetValue((type, name), out PropertyInfo property))
                    {
                        property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                        properties[(type, name)] = property;
                    }

                    value = property?.GetValue(value);
                }

                return Convert.ToString(value, CultureInfo.CurrentCulture);
            });
        }
    }

    // How a column's values are spread over the rows: every value when there are at most seven, or else the six most
    // common and the rest together. Values are compared exactly, as the filters compare them; the largest come first.
    internal sealed class ColumnSummaryCounts
    {
        internal const int MaxRows = 7;

        private readonly HashSet<string> _values;

        private ColumnSummaryCounts(
            int total, HashSet<string> values, IReadOnlyList<ColumnSummaryValue> shown, int moreValues, int moreCount)
        {
            Total = total;
            _values = values;
            Shown = shown;
            MoreValues = moreValues;
            MoreCount = moreCount;
        }

        internal int Total { get; }

        // The number of different values; rows without a value aren't a value of their own.
        internal int ValueCount => _values.Count - (_values.Contains(string.Empty) ? 1 : 0);

        internal IReadOnlyList<ColumnSummaryValue> Shown { get; }

        // The values in the rest, none when every value is shown, and their rows. Rows without a value can be in the
        // rest, but aren't counted as one of its values.
        internal int MoreValues { get; }

        internal int MoreCount { get; }

        internal bool Has(string value) => value != null && _values.Contains(value);

        internal static ColumnSummaryCounts Count(IEnumerable rows, Func<object, string> valueOf)
        {
            var counts = new Dictionary<string, (int Count, object Row)>(StringComparer.Ordinal);
            int total = 0;
            foreach (object row in rows ?? Array.Empty<object>())
            {
                if (row == null || ReferenceEquals(row, CollectionView.NewItemPlaceholder))
                {
                    continue;
                }

                string value = valueOf(row) ?? string.Empty;
                counts[value] = counts.TryGetValue(value, out var entry) ? (entry.Count + 1, entry.Row) : (1, row);
                total++;
            }

            List<ColumnSummaryValue> values = counts
                .Select(pair => new ColumnSummaryValue(pair.Key, pair.Value.Count, pair.Value.Row))
                .OrderByDescending(value => value.Count)
                .ThenBy(value => value.Value.Length == 0)
                .ThenBy(value => value.Value, StringComparer.Ordinal)
                .ToList();
            var keys = new HashSet<string>(counts.Keys, StringComparer.Ordinal);
            if (values.Count <= MaxRows)
            {
                return new ColumnSummaryCounts(total, keys, values, 0, 0);
            }

            List<ColumnSummaryValue> shown = values.Take(MaxRows - 1).ToList();
            List<ColumnSummaryValue> rest = values.Skip(shown.Count).ToList();
            return new ColumnSummaryCounts(
                total, keys, shown, rest.Count(value => value.Value.Length > 0), rest.Sum(value => value.Count));
        }

        // COUNT as a whole percentage of TOTAL. Less than 1% is UnderOne, and a count short of the total stays below 100.
        internal static (int Percent, bool UnderOne) Percent(int count, int total)
        {
            if (total <= 0 || count <= 0)
            {
                return (0, false);
            }

            double exact = 100.0 * count / total;
            if (exact < 1)
            {
                return (0, true);
            }

            int rounded = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
            return (count < total ? Math.Min(rounded, 99) : 100, false);
        }
    }

    // A column's summary as shown: its name and totals, one row per shown value and one for the rest, and the footer.
    // Each refresh makes a new one.
    public sealed class ColumnSummaryModel : INotifyPropertyChanged
    {
        private ColumnSummaryModel(
            string title, string totals, string accessibleName, string hint, IReadOnlyList<ColumnSummaryRow> rows)
        {
            Title = title;
            Totals = totals;
            AccessibleName = accessibleName;
            Hint = hint;
            Rows = rows;
        }

        // Never raised: a summary does not change. Implemented so bindings don't watch it through property descriptors.
        public event PropertyChangedEventHandler PropertyChanged
        {
            add { }
            remove { }
        }

        public string Title { get; }

        // "1,284 requests", and for some columns the number of values: "1,284 requests · 14 hosts".
        public string Totals { get; }

        public string AccessibleName { get; }

        public string Hint { get; }

        public IReadOnlyList<ColumnSummaryRow> Rows { get; }

        // The summary of COLUMN, titled TITLE, over ROWS, the rows that the table shows. SELECTEDROW's value is marked.
        internal static ColumnSummaryModel Create(string title, ColumnSummaryColumn column, IEnumerable rows, object selectedRow)
        {
            ColumnSummaryCounts counts = ColumnSummaryCounts.Count(rows, column.ValueOf);
            Func<int, string> rowCount = column.RowCountText ?? (count => Counted(count, "COLUMN_SUMMARY_ROW", "COLUMN_SUMMARY_ROWS"));
            string current = selectedRow == null ? null : column.ValueOf(selectedRow) ?? string.Empty;
            int largest = Math.Max(counts.MoreCount, counts.Shown.Select(value => value.Count).DefaultIfEmpty(0).Max());

            var shown = new List<ColumnSummaryRow>();
            foreach (ColumnSummaryValue value in counts.Shown)
            {
                string label = value.Value.Length != 0
                    ? value.Value
                    : column.EmptyValueText ?? Localization.GetLocalizedString("COLUMN_SUMMARY_NO_VALUE");
                shown.Add(new ColumnSummaryRow(
                    label,
                    value,
                    counts.Total,
                    largest,
                    rowCount,
                    column.BarOf?.Invoke(value.Value) ?? ColumnSummaryBar.Neutral,
                    isCurrent: value.Value == current,
                    isMore: false,
                    canSelect: column.Select != null && (column.CanSelect?.Invoke(value) ?? true)));
            }

            if (counts.MoreValues > 0)
            {
                Func<int, string> more = column.MoreValuesText ??
                    (count => Counted(count, "COLUMN_SUMMARY_MORE_VALUE", "COLUMN_SUMMARY_MORE_VALUES"));
                shown.Add(new ColumnSummaryRow(
                    more(counts.MoreValues),
                    new ColumnSummaryValue(null, counts.MoreCount, null),
                    counts.Total,
                    largest,
                    rowCount,
                    ColumnSummaryBar.Neutral,
                    isCurrent: counts.Has(current) && counts.Shown.All(value => value.Value != current),
                    isMore: true,
                    canSelect: false));
            }

            var totals = new List<string> { rowCount(counts.Total) };
            if (column.ValueCountText != null)
            {
                totals.Add(column.ValueCountText(counts.ValueCount));
            }

            return new ColumnSummaryModel(
                title,
                totals.Count == 1 ? totals[0] : Localization.GetLocalizedString("COLUMN_SUMMARY_TOTALS", totals[0], totals[1]),
                Localization.GetLocalizedString("COLUMN_SUMMARY_NAME", title, string.Join(", ", totals)),
                column.Hint ?? Localization.GetLocalizedString(
                    column.Select == null ? "COLUMN_SUMMARY_HINT_NO_FILTER" : "COLUMN_SUMMARY_HINT_VALUE"),
                shown);
        }

        // COUNT with its noun, singular or plural: "1 request", "1,284 requests".
        internal static string Counted(int count, string singularKey, string pluralKey) =>
            Localization.GetLocalizedString(count == 1 ? singularKey : pluralKey, Number(count));

        internal static string Number(int count) => count.ToString("N0", CultureInfo.CurrentCulture);

        // Whether OTHER shows the same thing, so a refresh can keep this one.
        internal bool HasSameContent(ColumnSummaryModel other) =>
            other != null && Title == other.Title && Totals == other.Totals && Hint == other.Hint &&
            Rows.Count == other.Rows.Count && Rows.Zip(other.Rows).All(pair => pair.First.HasSameContent(pair.Second));
    }

    // One row of a summary: a value, or the rest of the values, with its count, its share of the rows and its bar.
    public sealed class ColumnSummaryRow : INotifyPropertyChanged
    {
        internal ColumnSummaryRow(
            string label,
            ColumnSummaryValue value,
            int total,
            int largest,
            Func<int, string> rowCount,
            ColumnSummaryBar bar,
            bool isCurrent,
            bool isMore,
            bool canSelect)
        {
            Label = label;
            Entry = value;
            Bar = bar;
            IsCurrent = isCurrent;
            IsMore = isMore;
            CanSelect = canSelect;
            CountText = ColumnSummaryModel.Number(value.Count);
            (int percent, bool underOne) = ColumnSummaryCounts.Percent(value.Count, total);
            PercentText = underOne
                ? Localization.GetLocalizedString("COLUMN_SUMMARY_UNDER_ONE_PERCENT")
                : Localization.GetLocalizedString("COLUMN_SUMMARY_PERCENT", percent);
            AccessibleName = Localization.GetLocalizedString(
                "COLUMN_SUMMARY_VALUE_NAME",
                label,
                rowCount(value.Count),
                underOne
                    ? Localization.GetLocalizedString("COLUMN_SUMMARY_UNDER_ONE_PERCENT_NAME")
                    : Localization.GetLocalizedString("COLUMN_SUMMARY_PERCENT_NAME", percent));
            ItemStatus = isCurrent ? Localization.GetLocalizedString("COLUMN_SUMMARY_SELECTED_ROW") : null;
            Fraction = largest > 0 ? (double)value.Count / largest : 0;
        }

        // Never raised: a row does not change. Implemented so bindings don't watch it through property descriptors.
        public event PropertyChangedEventHandler PropertyChanged
        {
            add { }
            remove { }
        }

        public string Label { get; }

        // The value and its rows; its Value is null for the rest of the values.
        public ColumnSummaryValue Entry { get; }

        public string CountText { get; }

        public string PercentText { get; }

        // What screen readers read: "429, 41 requests, 3 percent".
        public string AccessibleName { get; }

        // Says that this row has the table's selected row.
        public string ItemStatus { get; }

        public ColumnSummaryBar Bar { get; }

        // The bar's length as a share of the longest bar, and the rest of its lane.
        public double Fraction { get; }

        public GridLength BarWidth => new(Fraction, GridUnitType.Star);

        public GridLength BarRemainder => new(1 - Fraction, GridUnitType.Star);

        // The value of the table's selected row is in this row.
        public bool IsCurrent { get; }

        public bool IsMore { get; }

        // Choosing this row filters the table to its value.
        public bool CanSelect { get; }

        internal bool HasSameContent(ColumnSummaryRow other) =>
            Label == other.Label && Entry.Value == other.Entry.Value && Entry.Count == other.Entry.Count &&
            PercentText == other.PercentText && Bar == other.Bar && IsCurrent == other.IsCurrent &&
            CanSelect == other.CanSelect && Fraction.Equals(other.Fraction);
    }
}
