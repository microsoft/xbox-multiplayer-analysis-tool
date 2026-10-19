// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    internal sealed class CaptureTimelineLayout
    {
        internal const double RowHeight = 16;
        internal const double HostLabelHeight = 18;
        internal const double AxisHeight = 20;
        internal const double MinimumBarWidth = 3;
        private const double HostGap = 4;
        private const double Overscan = RowHeight * 2;
        private static readonly double[] TickIntervals =
            { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 30000, 60000 };
        private static readonly IComparer<Entry> EntryOrder = Comparer<Entry>.Create(
            (left, right) => Compare(left.Start, left.Sequence, right.Start, right.Sequence));
        private static readonly IComparer<HostGroup> GroupOrder = Comparer<HostGroup>.Create(
            (left, right) => Compare(left.Rows[0].Start, left.Sequence, right.Rows[0].Start, right.Sequence));
        private readonly Dictionary<ProxyConnectionModel, Entry> _entries = new(ReferenceEqualityComparer.Instance);
        private readonly List<Entry> _rows = new();
        private readonly SortedSet<(DateTime End, long Sequence)> _ends = new();
        private readonly Dictionary<HostKey, HostGroup> _hosts = new();
        private readonly List<HostGroup> _groups = new();
        private readonly PrefixHeights _groupHeights = new();
        private long _nextSequence;
        private long _nextGroupSequence;

        internal int Count => _rows.Count;
        internal DateTime Origin => Count == 0 ? DateTime.MinValue : _rows[0].Start;
        internal double SpanMilliseconds => Count == 0 ? 1 : Math.Max((_ends.Max.End - Origin).TotalMilliseconds, 1);
        internal double Width => Math.Max(SpanMilliseconds * 0.5, 200);
        internal double Scale => Width / SpanMilliseconds;

        internal readonly record struct Row(
            ProxyConnectionModel Model, string Host, double Top,
            DateTime Start, TimeSpan Duration, long Version);

        internal readonly record struct Tick(double Milliseconds, double Left);

        internal void Reset(IEnumerable<ProxyConnectionModel> models)
        {
            _entries.Clear();
            _rows.Clear();
            _ends.Clear();
            _hosts.Clear();
            _groups.Clear();
            _groupHeights.Clear();
            _nextSequence = 0;
            _nextGroupSequence = 0;
            foreach (var model in models)
            {
                Add(model);
            }
        }

        internal void Add(ProxyConnectionModel model)
        {
            ArgumentNullException.ThrowIfNull(model);
            var entry = new Entry(model, _nextSequence++);
            _entries.Add(model, entry);
            if (entry.IsValid)
            {
                AddRow(entry);
            }
        }

        internal void Remove(ProxyConnectionModel model)
        {
            if (_entries.Remove(model, out var entry) && entry.IsValid)
            {
                RemoveRow(entry);
            }
        }

        internal bool Update(ProxyConnectionModel model)
        {
            if (!_entries.TryGetValue(model, out var entry) || entry.Matches(model))
            {
                return false;
            }

            var reorder = entry.Start != model.RequestTime || entry.Host != model.Host;
            if (entry.IsValid)
            {
                if (reorder)
                {
                    RemoveRow(entry);
                }
                else
                {
                    _ends.Remove((entry.End, entry.Sequence));
                }
            }

            entry.Refresh();
            if (entry.IsValid)
            {
                if (reorder)
                {
                    AddRow(entry);
                }
                else
                {
                    _ends.Add((entry.End, entry.Sequence));
                }
            }
            return true;
        }

        internal double GetHeight(bool waterfall) =>
            waterfall ? Count * RowHeight : _groupHeights.Sum(_groups.Count);

        internal void GetVisibleRows(bool waterfall, double offset, double height, List<Row> result)
        {
            result.Clear();
            if (height <= 0 || Count == 0)
            {
                return;
            }

            var top = Math.Max(0, offset - Overscan);
            var bottom = Math.Max(0, offset + height + Overscan);
            if (waterfall)
            {
                AddVisibleRows(_rows, 0, top, bottom, result);
                return;
            }

            var groupIndex = _groupHeights.Find(top);
            var groupTop = _groupHeights.Sum(groupIndex);
            for (; groupIndex < _groups.Count && groupTop < bottom; groupIndex++)
            {
                var group = _groups[groupIndex];
                if (groupTop + HostLabelHeight >= top)
                {
                    result.Add(new Row(null, group.Host, groupTop, default, default, 0));
                }
                AddVisibleRows(group.Rows, groupTop + HostLabelHeight, top, bottom, result);
                groupTop += group.Height;
            }
        }

        internal void GetVisibleTicks(double offset, double width, List<Tick> result)
        {
            result.Clear();
            if (Count == 0 || width <= 0)
            {
                return;
            }

            var rawInterval = SpanMilliseconds / (Width / 80);
            var interval = rawInterval;
            foreach (var candidate in TickIntervals)
            {
                if (candidate >= rawInterval)
                {
                    interval = candidate;
                    break;
                }
            }

            var spacing = interval * Scale;
            var first = Math.Max(0, (long)Math.Floor(offset / spacing) - 1);
            var last = Math.Min(
                (long)Math.Floor(SpanMilliseconds / interval),
                (long)Math.Ceiling((offset + width) / spacing) + 1);
            for (var index = first; index <= last; index++)
            {
                result.Add(new Tick(index * interval, index * spacing));
            }
        }

        internal (double Left, double Width) GetBarBounds(Row row) =>
            ((row.Start - Origin).TotalMilliseconds * Scale,
                Math.Max(row.Duration.TotalMilliseconds * Scale, MinimumBarWidth));

        private static void AddVisibleRows(List<Entry> rows, double origin, double top, double bottom, List<Row> result)
        {
            var first = (int)Math.Clamp(Math.Floor((top - origin) / RowHeight), 0, rows.Count);
            var last = (int)Math.Clamp(Math.Ceiling((bottom - origin) / RowHeight), 0, rows.Count);
            for (var index = first; index < last; index++)
            {
                var entry = rows[index];
                result.Add(new Row(entry.Model, entry.Host, origin + (index * RowHeight),
                    entry.Start, entry.Duration, entry.Version));
            }
        }

        private void AddRow(Entry entry)
        {
            Insert(_rows, entry, EntryOrder);
            _ends.Add((entry.End, entry.Sequence));
            var key = new HostKey(entry.Host);
            if (!_hosts.TryGetValue(key, out var group))
            {
                group = new HostGroup(entry.Host, _nextGroupSequence++);
                group.Rows.Add(entry);
                _hosts.Add(key, group);
                var index = Insert(_groups, group, GroupOrder);
                if (index == _groups.Count - 1)
                {
                    group.Index = index;
                    _groupHeights.Append(group.Height);
                }
                else
                {
                    RebuildGroups();
                }
            }
            else
            {
                var first = group.Rows[0].Start;
                Insert(group.Rows, entry, EntryOrder);
                if (group.Rows[0].Start != first)
                {
                    ReorderGroup(group);
                }
                else
                {
                    _groupHeights.Add(group.Index, RowHeight);
                }
            }
        }

        private void RemoveRow(Entry entry)
        {
            _rows.RemoveAt(_rows.BinarySearch(entry, EntryOrder));
            _ends.Remove((entry.End, entry.Sequence));
            var key = new HostKey(entry.Host);
            var group = _hosts[key];
            var first = group.Rows[0].Start;
            group.Rows.RemoveAt(group.Rows.BinarySearch(entry, EntryOrder));
            if (group.Rows.Count == 0)
            {
                _hosts.Remove(key);
                _groups.RemoveAt(group.Index);
                RebuildGroups();
            }
            else if (group.Rows[0].Start != first)
            {
                ReorderGroup(group);
            }
            else
            {
                _groupHeights.Add(group.Index, -RowHeight);
            }
        }

        private void ReorderGroup(HostGroup group)
        {
            _groups.RemoveAt(group.Index);
            Insert(_groups, group, GroupOrder);
            RebuildGroups();
        }

        private void RebuildGroups()
        {
            _groupHeights.Clear();
            for (var index = 0; index < _groups.Count; index++)
            {
                _groups[index].Index = index;
                _groupHeights.Append(_groups[index].Height);
            }
        }

        private static int Insert<T>(List<T> items, T item, IComparer<T> order)
        {
            var index = items.Count == 0 || order.Compare(items[^1], item) < 0
                ? items.Count
                : ~items.BinarySearch(item, order);
            items.Insert(index, item);
            return index;
        }

        private static int Compare(DateTime left, long leftSequence, DateTime right, long rightSequence)
        {
            var comparison = left.CompareTo(right);
            return comparison == 0 ? leftSequence.CompareTo(rightSequence) : comparison;
        }

        private readonly record struct HostKey(string Host);

        private sealed class Entry
        {
            internal ProxyConnectionModel Model { get; }
            internal long Sequence { get; }
            internal long Version { get; private set; }
            internal DateTime Start { get; private set; }
            internal TimeSpan Duration { get; private set; }
            internal string Host { get; private set; }
            private string Status { get; set; }
            private string Method { get; set; }
            private string Path { get; set; }
            internal bool IsValid => Start != DateTime.MinValue;
            internal DateTime End => Start + Duration;

            internal Entry(ProxyConnectionModel model, long sequence)
            {
                Model = model;
                Sequence = sequence;
                Refresh();
            }

            internal bool Matches(ProxyConnectionModel model) =>
                Start == model.RequestTime && Duration == model.Duration && Host == model.Host &&
                Status == model.Status && Method == model.Method && Path == model.Path;

            internal void Refresh()
            {
                Start = Model.RequestTime;
                Duration = Model.Duration;
                Host = Model.Host;
                Status = Model.Status;
                Method = Model.Method;
                Path = Model.Path;
                Version++;
            }
        }

        private sealed class HostGroup
        {
            internal string Host { get; }
            internal long Sequence { get; }
            internal int Index { get; set; }
            internal List<Entry> Rows { get; } = new();
            internal double Height => HostLabelHeight + (Rows.Count * RowHeight) + HostGap;

            internal HostGroup(string host, long sequence)
            {
                Host = host;
                Sequence = sequence;
            }
        }

        // Prefix sums locate visible host groups without walking all earlier groups.
        private sealed class PrefixHeights
        {
            private readonly List<double> _tree = new() { 0 };

            internal void Clear()
            {
                _tree.Clear();
                _tree.Add(0);
            }

            internal void Append(double height)
            {
                var index = _tree.Count;
                _tree.Add(height + Sum(index - 1) - Sum(index - (index & -index)));
            }

            internal void Add(int index, double delta)
            {
                for (var position = index + 1; position < _tree.Count; position += position & -position)
                {
                    _tree[position] += delta;
                }
            }

            internal double Sum(int count)
            {
                double result = 0;
                for (var position = count; position > 0; position -= position & -position)
                {
                    result += _tree[position];
                }
                return result;
            }

            internal int Find(double offset)
            {
                var index = 0;
                double sum = 0;
                var bit = 1;
                while (bit <= (_tree.Count - 1) / 2)
                {
                    bit <<= 1;
                }
                for (; bit != 0; bit >>= 1)
                {
                    var next = index + bit;
                    if (next < _tree.Count && sum + _tree[next] <= offset)
                    {
                        sum += _tree[next];
                        index = next;
                    }
                }
                return index;
            }
        }
    }
}
