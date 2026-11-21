// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using XMAT.SharedInterfaces;

namespace XMAT.WebServiceCapture.Models
{
    internal sealed class HostListCollection(object gate) : ObservableCollection<string>
    {
        internal Func<NotifyCollectionChangedEventArgs, Guid?, HostPolicyChange> Committing { get; set; }

        internal HostPolicyChange AddOwned(string entry, Guid? actor)
        {
            lock (gate)
            {
                if (this.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase)))
                    return null;
                return Insert(Count, entry, actor);
            }
        }

        internal HostPolicyChange RemoveOwned(int index, Guid? actor)
        {
            lock (gate)
            {
                CheckReentrancy();
                var previous = Items[index];
                Items.RemoveAt(index);
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, previous, index);
                var committed = Committing?.Invoke(args, actor);
                Notify(args, countChanged: true);
                return committed;
            }
        }

        protected override void InsertItem(int index, string item)
        {
            lock (gate)
            {
                if (!HostListEntry.TryNormalize(item, out var entry, out _) ||
                    this.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase)))
                    return;
                Insert(index, entry, null);
            }
        }

        private HostPolicyChange Insert(int index, string entry, Guid? actor)
        {
            CheckReentrancy();
            Items.Insert(index, entry);
            var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, entry, index);
            // The committed identity/epoch must exist before even a Count notification can run UI code.
            var committed = Committing?.Invoke(args, actor);
            Notify(args, countChanged: true);
            return committed;
        }

        protected override void RemoveItem(int index) => RemoveOwned(index, null);

        protected override void SetItem(int index, string item)
        {
            lock (gate)
            {
                CheckReentrancy();
                if (!HostListEntry.TryNormalize(item, out var entry, out _) ||
                    this.Where((_, position) => position != index)
                        .Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase)))
                    return;
                var previous = Items[index];
                Items[index] = entry;
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, entry, previous, index);
                Committing?.Invoke(args, null);
                Notify(args, countChanged: false);
            }
        }

        protected override void ClearItems()
        {
            ClearOwned(null);
        }

        internal HostPolicyChange ClearOwned(Guid? actor)
        {
            lock (gate)
            {
                CheckReentrancy();
                if (Count == 0)
                    return null;
                Items.Clear();
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
                var committed = Committing?.Invoke(args, actor);
                Notify(args, countChanged: true);
                return committed;
            }
        }

        protected override void MoveItem(int oldIndex, int newIndex)
        {
            lock (gate)
            {
                CheckReentrancy();
                if (oldIndex == newIndex)
                    return;
                var item = Items[oldIndex];
                Items.RemoveAt(oldIndex);
                Items.Insert(newIndex, item);
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, item, newIndex, oldIndex);
                Committing?.Invoke(args, null);
                Notify(args, countChanged: false);
            }
        }

        private void Notify(NotifyCollectionChangedEventArgs args, bool countChanged)
        {
            if (countChanged)
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(args);
        }
    }
}
