// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using XMAT.SharedInterfaces;
using XMAT.SqlQueries;

namespace XMAT.Models
{
    internal abstract class SqlQueryObservable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            RaisePropertyChanged(name);
            return true;
        }
    }

    internal sealed class SqlQuerySourceModel(
        QuerySourceDescriptor descriptor, string path, string displayName = null) : SqlQueryObservable
    {
        private bool _isAvailable = true;
        public Guid Id => descriptor.Id;
        public QuerySourceKind Kind => descriptor.Kind;
        public string Name => displayName ?? (descriptor.Kind == QuerySourceKind.Active
            ? Localization.GetLocalizedString("SQL_SOURCE_ACTIVE")
            : Localization.GetLocalizedString("SQL_SOURCE_LOADED", descriptor.DisplayName));
        public string DisplayName => IsAvailable ? Name : Localization.GetLocalizedString("SQL_SOURCE_STALE", Name);
        public string ToolTip => string.IsNullOrEmpty(path) ? Name : path;
        public bool IsAvailable
        {
            get => _isAvailable;
            internal set
            {
                if (SetProperty(ref _isAvailable, value))
                    RaisePropertyChanged(nameof(DisplayName));
            }
        }
    }

    internal sealed record SqlQueryHistoryEntry(
        Guid SourceId, string SourceName, string Sql, string Status, DateTimeOffset CompletedAt, TimeSpan Elapsed)
    {
        public string DisplayName => $"{CompletedAt:t} — {SourceName} — {Status} — " +
            Sql.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(80, Sql.Length)];
        public override string ToString() => DisplayName;
    }

    internal sealed record SqlQueryRun(
        Guid ResultId, Guid SnapshotId, Guid SourceId, string SourceName, string Sql, int EditorOffset,
        DateTimeOffset CapturedAt, TimeSpan Elapsed, long RowCount);

    internal sealed class SqlQuerySchemaItem
    {
        public QueryTable Table { get; }
        public QuerySchemaColumn Column { get; }
        public string Name => Column?.Name ?? Table.Name;
        public string Insertion => SqlQueryText.QuoteIdentifier(Name);
        public string DisplayName => Column == null ? Table.Name + " (" + Table.Kind + ")" :
            Column.Name + "  " + Column.DeclaredType +
            (Column.PrimaryKey ? "  " + Localization.GetLocalizedString("SQL_PRIMARY_KEY") : string.Empty) +
            "  " + Localization.GetLocalizedString(Column.Nullable ? "SQL_NULLABLE" : "SQL_NOT_NULL");
        public string ToolTip => Column == null ? Table.Name : SqlQueryText.ColumnDescription(Column) +
            SqlQueryExamples.FieldHelp(Table.Name, Column.Name);
        public IReadOnlyList<SqlQuerySchemaItem> Children { get; }

        internal SqlQuerySchemaItem(QueryTable table, QuerySchemaColumn column, string search)
        {
            Table = table;
            Column = column;
            Children = column == null
                ? table.Columns.Where(item => table.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (item.DeclaredType ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Select(item => new SqlQuerySchemaItem(table, item, search)).ToArray()
                : [];
        }
    }

    internal sealed class SqlQueryCellModel
    {
        public int Ordinal { get; }
        public int DisplayOrdinal => Ordinal + 1;
        public string Name { get; }
        public QueryValueKind Kind { get; }
        public long ByteLength { get; }
        public string Preview { get; }
        public bool IsTruncated { get; }
        public string Type => Kind.ToString().ToUpperInvariant();
        public string Description => Localization.GetLocalizedString("SQL_CELL_DESCRIPTION",
            Ordinal + 1, Name, Type, ByteLength);

        internal SqlQueryCellModel(int ordinal, string name, QueryCell cell)
        {
            Ordinal = ordinal;
            Name = name;
            Kind = cell.Kind;
            ByteLength = cell.ByteLength;
            var value = cell.Kind switch
            {
                QueryValueKind.Null => "NULL",
                QueryValueKind.Integer => ((long)cell.Value).ToString(CultureInfo.InvariantCulture),
                QueryValueKind.Real => ((double)cell.Value).ToString("R", CultureInfo.InvariantCulture),
                QueryValueKind.Text when cell.ByteLength == 0 =>
                    Localization.GetLocalizedString("SQL_EMPTY_TEXT"),
                QueryValueKind.Text => ((string)cell.Value).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"),
                QueryValueKind.Blob => "0x" + Convert.ToHexString(((byte[])cell.Value).AsSpan(0, Math.Min(64, ((byte[])cell.Value).Length))),
                _ => string.Empty
            };
            IsTruncated = cell.IsTruncated || value.Length > 256 ||
                cell.Kind == QueryValueKind.Blob && cell.ByteLength > 64;
            var length = Math.Min(256, value.Length);
            if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]) &&
                char.IsLowSurrogate(value[length]))
                length--;
            Preview = value[..length] + (IsTruncated ? "…" : string.Empty);
        }
    }

    internal sealed record SqlQueryResultRow(long Ordinal, IReadOnlyList<SqlQueryCellModel> Cells)
    {
        public long DisplayOrdinal => Ordinal + 1;
    }

    internal static class SqlQueryResultPresentation
    {
        internal static IReadOnlyList<string> ColumnLabels(IReadOnlyList<QueryColumn> columns)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var labels = new List<string>();
            foreach (var column in columns)
            {
                var original = string.IsNullOrEmpty(column.Name)
                    ? Localization.GetLocalizedString("SQL_UNNAMED_COLUMN") : column.Name;
                var label = original;
                var suffix = 2;
                while (!used.Add(label))
                    label = $"{original} [{suffix++}]";
                labels.Add(label);
            }
            return labels.AsReadOnly();
        }

        internal static SqlQueryResultRow Row(QueryRow row, IReadOnlyList<QueryColumn> columns) =>
            new(row.Ordinal, row.Cells.Select((cell, index) =>
                new SqlQueryCellModel(index, columns[index].Name, cell)).ToArray());
    }
}
