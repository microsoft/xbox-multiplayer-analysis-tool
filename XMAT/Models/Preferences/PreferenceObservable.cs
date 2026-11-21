// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

namespace XMAT.Models
{
    internal abstract class PreferenceObservable : INotifyPropertyChanged
    {
        private long _revision = 1;
        private int _batchDepth;
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

        internal long Revision => Interlocked.Read(ref _revision);
        internal event EventHandler Changed;
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetValue<T>(ref T field, T value, [CallerMemberName] string property = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            ValueChanged(property);
            return true;
        }

        protected void ValueChanged([CallerMemberName] string property = null)
        {
            Interlocked.Increment(ref _revision);
            Changed?.Invoke(this, EventArgs.Empty);
            if (_batchDepth != 0)
            {
                _pending.Add(property ?? string.Empty);
                return;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }

        internal IDisposable BatchChanges()
        {
            _batchDepth++;
            return new Batch(this);
        }

        private void EndBatch()
        {
            if (--_batchDepth != 0 || _pending.Count == 0)
                return;
            _pending.Clear();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        private sealed class Batch(PreferenceObservable owner) : IDisposable
        {
            private PreferenceObservable _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndBatch();
        }
    }
}
