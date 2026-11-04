// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace XMAT.WebServiceCapture
{
    internal sealed class CaptureFilterIndex
    {
        private readonly Dictionary<ValueKey, CheckedListItem> _values = new();
        private readonly HashSet<CheckedListItem> _observedItems = new(ReferenceEqualityComparer.Instance);
        private ObservableCollection<CheckedListItem> _source;

        internal void SetSource(ObservableCollection<CheckedListItem> source)
        {
            if (ReferenceEquals(source, _source))
            {
                return;
            }
            if (_source != null)
            {
                _source.CollectionChanged -= SourceChanged;
            }
            DetachItems();
            _source = source;
            if (source != null)
            {
                source.CollectionChanged += SourceChanged;
                Rebuild();
            }
        }

        internal void EnsureValue(string text)
        {
            if (_source != null && !string.IsNullOrEmpty(text) && !_values.ContainsKey(new ValueKey(text)))
            {
                _source.Add(new CheckedListItem(text));
            }
        }

        internal bool IsChecked(string text) =>
            _values.TryGetValue(new ValueKey(text), out var item) && item.IsChecked;

        private void SourceChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _source))
            {
                return;
            }
            if (e.Action == NotifyCollectionChangedAction.Add &&
                e.NewStartingIndex == _source.Count - e.NewItems.Count)
            {
                foreach (CheckedListItem item in e.NewItems)
                {
                    Observe(item);
                }
            }
            else
            {
                Rebuild();
            }
        }

        private void ItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is CheckedListItem item && _observedItems.Contains(item) &&
                (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(CheckedListItem.Text)))
            {
                Rebuild();
            }
        }

        private void Observe(CheckedListItem item)
        {
            if (item == null)
            {
                return;
            }
            if (_observedItems.Add(item))
            {
                item.PropertyChanged += ItemChanged;
            }
            _values.TryAdd(new ValueKey(item.Text), item);
        }

        private void Rebuild()
        {
            DetachItems();
            foreach (var item in _source)
            {
                Observe(item);
            }
        }

        private void DetachItems()
        {
            foreach (var item in _observedItems)
            {
                item.PropertyChanged -= ItemChanged;
            }
            _observedItems.Clear();
            _values.Clear();
        }

        private readonly record struct ValueKey(string Text);
    }
}
