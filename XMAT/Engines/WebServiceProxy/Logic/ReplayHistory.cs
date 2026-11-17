// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    // The composer's history: the rows that this session's Replays tab recorded, newest first, as the recorder committed
    // them (ReplayRecorder), at most MaxEntries. It is a way back to those rows, not another store of what they hold: an
    // entry names its row and tab by weak reference only, so the history keeps neither alive, copies none of their content
    // and is never saved, logged or exported. An entry is live while its tab is one of the application's tabs and its row is
    // one of that tab's rows, by reference; it is checked each time the history is read (Snapshot, TryResolve), so closing
    // the Replays tab, or a new Replays tab with the same name and numbers, leaves no entry that resolves. Clear and eviction
    // revoke entries even though their rows stay in Replays. Used on the UI thread only, like the rows themselves.
    internal sealed class ReplayHistory
    {
        internal const int MaxEntries = 200;

        // The most characters of each preview, as shown (UntrustedText.ShowParts), escapes and ellipsis included.
        internal const int MaxUrlPreviewChars = 512;
        internal const int MaxMethodPreviewChars = 64;
        internal const int MaxTabNamePreviewChars = 64;
        internal const int MaxStatusPreviewChars = 16;

        // The longest host and port that a preview gives Uri to build an authority (RequestReplayDraft.CaptureAuthority); a
        // longer one, which no URL that was sent has, is shown as stored, cut to the preview's bound.
        private const int MaxAuthorityHostChars = 255;
        private const int MaxAuthorityPortChars = 5;

        private readonly CaptureAppModel _model;

        // Oldest first.
        private readonly List<ReplayHistoryEntry> _entries = new();
        private readonly List<Observation> _observations = new();

        internal ReplayHistory(CaptureAppModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        // The entries held, live or not yet found dead.
        internal int Count => _entries.Count;

        // A row that the recorder has committed to tab (ReplayRecorder's recorded hook); the oldest entry goes once there are
        // more than MaxEntries.
        internal void Add(ICaptureDeviceContext tab, ProxyConnectionModel row)
        {
            ArgumentNullException.ThrowIfNull(tab);
            ArgumentNullException.ThrowIfNull(row);
            _entries.Add(new ReplayHistoryEntry(tab, row));
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
            }

            NotifyObservers();
        }

        // The live entries, newest first, each described from its row as it is now, with bounded text. The list is the
        // caller's, and the history keeps nothing of it.
        internal IReadOnlyList<ReplayHistoryPreview> Snapshot()
        {
            var previews = new List<ReplayHistoryPreview>();
            for (int index = _entries.Count - 1; index >= 0; index--)
            {
                ReplayHistoryEntry entry = _entries[index];
                if (TryGetLive(entry, out ICaptureDeviceContext tab, out ProxyConnectionModel row))
                {
                    previews.Add(Preview(entry, tab, row));
                }
                else
                {
                    _entries.RemoveAt(index);
                }
            }

            return previews;
        }

        // The tab and row that entry names, now: only while the entry is still one of this history's (not cleared or
        // evicted), XMAT is not shutting down, and the entry is live.
        internal bool TryResolve(ReplayHistoryEntry entry, out ICaptureDeviceContext tab, out ProxyConnectionModel row)
        {
            tab = null;
            row = null;
            int index = entry == null ? -1 : _entries.IndexOf(entry);
            if (index < 0 || _model.IsShuttingDown)
            {
                return false;
            }

            if (!TryGetLive(entry, out tab, out row))
            {
                _entries.RemoveAt(index);
                tab = null;
                row = null;
                return false;
            }

            return true;
        }

        // Forgets every entry; the Replays tab and its rows stay as they are.
        internal void Clear()
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
            NotifyObservers();
        }

        // For one open view: changed is called, on the UI thread, whenever what Snapshot returns may have changed: an entry
        // added, evicted or cleared, the application's tabs changed, rows removed from a tab whose rows live entries name, or
        // that tab's rows replaced by another collection. changed should only schedule work. Dispose the subscription when
        // the view closes; it then holds nothing of the view, and nothing that it watched holds it.
        internal IDisposable Observe(Action changed)
        {
            ArgumentNullException.ThrowIfNull(changed);
            var observation = new Observation(this, changed);
            _observations.Add(observation);
            observation.Watch();
            return observation;
        }

        private void NotifyObservers()
        {
            foreach (Observation observation in _observations.ToArray())
            {
                observation.Notify();
            }
        }

        private bool TryGetLive(ReplayHistoryEntry entry, out ICaptureDeviceContext tab, out ProxyConnectionModel row)
        {
            if (entry.TryGet(out tab, out row) &&
                IsListed(tab) &&
                tab.CaptureController is WebServiceDeviceCaptureController controller &&
                Contains(controller.ProxyConnections, row))
            {
                return true;
            }

            tab = null;
            row = null;
            return false;
        }

        private bool IsListed(ICaptureDeviceContext tab)
        {
            foreach (ICaptureDeviceContext listed in _model.CaptureDeviceContexts)
            {
                if (ReferenceEquals(listed, tab))
                {
                    return true;
                }
            }

            return false;
        }

        // By reference, from the end: entries name the rows that were recorded last.
        private static bool Contains(ProxyConnectionsCollection rows, ProxyConnectionModel row)
        {
            if (rows == null)
            {
                return false;
            }

            for (int index = rows.Count - 1; index >= 0; index--)
            {
                if (ReferenceEquals(rows[index], row))
                {
                    return true;
                }
            }

            return false;
        }

        // The controllers of the tabs whose rows live entries name (TryGetLive), which an observation watches: a tab whose
        // entries have all expired is watched no longer, so its later changes, which change nothing that Snapshot shows,
        // call no view.
        private List<WebServiceDeviceCaptureController> SourceControllers()
        {
            var controllers = new List<WebServiceDeviceCaptureController>();
            foreach (ReplayHistoryEntry entry in _entries)
            {
                if (TryGetLive(entry, out ICaptureDeviceContext tab, out _) &&
                    tab.CaptureController is WebServiceDeviceCaptureController controller &&
                    !controllers.Exists(known => ReferenceEquals(known, controller)))
                {
                    controllers.Add(controller);
                }
            }

            return controllers;
        }

        private static ReplayHistoryPreview Preview(ReplayHistoryEntry entry, ICaptureDeviceContext tab, ProxyConnectionModel row) =>
            new(
                entry,
                UntrustedText.ShowParts(MaxTabNamePreviewChars, tab.DeviceName),
                row.RequestNumber,
                row.RequestTime,
                UntrustedText.ShowParts(MaxMethodPreviewChars, row.Method),
                UrlPreview(row),
                UntrustedText.ShowParts(MaxStatusPreviewChars, row.Status),
                RequestReplayDraft.CanReplay(row));

        // Where Load sends the row's request (RequestReplayDraft.FromCapture): its authority as Uri gives it, then its path,
        // read only as far as the bound needs. A row whose path is an absolute URL, which the recorder never writes, is shown
        // as stored. A host or port too long for any URL that was sent, or that Uri refuses, is shown as stored, the host
        // of an IPv6 address in brackets, and so is a row whose scheme is unknown, without a scheme.
        private static string UrlPreview(ProxyConnectionModel row)
        {
            string path = row.Path ?? string.Empty;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return UntrustedText.ShowParts(MaxUrlPreviewChars, path);
            }

            string separator = path.StartsWith('/') ? string.Empty : "/";
            string scheme = row.Scheme ?? string.Empty;
            string host = row.Host ?? string.Empty;
            string port = row.Port ?? string.Empty;
            if (scheme is "http" or "https" && host.Length <= MaxAuthorityHostChars && port.Length <= MaxAuthorityPortChars)
            {
                try
                {
                    return UntrustedText.ShowParts(MaxUrlPreviewChars, RequestReplayDraft.CaptureAuthority(row), separator, path);
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
                {
                    // Load fails for this row too, and says why; the preview shows the row as stored.
                }
            }

            bool bracketed = host.Length <= MaxAuthorityHostChars && host.Contains(':');
            if (scheme.Length == 0)
            {
                // An unknown scheme is shown as none rather than as a URL without one: the host and port before the path,
                // or the path alone without a host.
                return host.Length == 0
                    ? UntrustedText.ShowParts(MaxUrlPreviewChars, path)
                    : UntrustedText.ShowParts(
                        MaxUrlPreviewChars,
                        bracketed ? "[" : string.Empty,
                        host,
                        bracketed ? "]" : string.Empty,
                        port.Length == 0 ? string.Empty : ":",
                        port,
                        separator,
                        path);
            }

            return UntrustedText.ShowParts(
                MaxUrlPreviewChars,
                scheme,
                "://",
                bracketed ? "[" : string.Empty,
                host,
                bracketed ? "]" : string.Empty,
                port.Length == 0 ? string.Empty : ":",
                port,
                separator,
                path);
        }

        // One open view's subscriptions: the application's tab list, and the controller and current rows of each tab whose
        // rows live entries name, which it works out again after every change, so it never keeps a replaced collection or a
        // tab whose entries have expired. Disposing it detaches them all and lets go of the view's callback.
        private sealed class Observation : IDisposable
        {
            private readonly List<(WebServiceDeviceCaptureController Controller, ProxyConnectionsCollection Rows)> _sources = new();

            // The thread that the view observes from, and its context, which a change reported on another thread is posted to.
            private readonly int _observerThread = Environment.CurrentManagedThreadId;
            private readonly SynchronizationContext _observerContext = SynchronizationContext.Current;
            private ReplayHistory _history;
            private Action _changed;
            private bool _watchingTabs;

            internal Observation(ReplayHistory history, Action changed)
            {
                _history = history;
                _changed = changed;
            }

            internal void Notify()
            {
                if (_history == null)
                {
                    return;
                }

                Watch();
                _changed();
            }

            internal void Watch()
            {
                ReplayHistory history = _history;
                if (history == null)
                {
                    return;
                }

                if (!_watchingTabs)
                {
                    history._model.CaptureDeviceContexts.CollectionChanged += Tabs_CollectionChanged;
                    _watchingTabs = true;
                }

                List<WebServiceDeviceCaptureController> wanted = history.SourceControllers();
                for (int index = _sources.Count - 1; index >= 0; index--)
                {
                    (WebServiceDeviceCaptureController controller, ProxyConnectionsCollection rows) = _sources[index];
                    if (!wanted.Exists(source => ReferenceEquals(source, controller)) ||
                        !ReferenceEquals(controller.ProxyConnections, rows))
                    {
                        Detach(controller, rows);
                        _sources.RemoveAt(index);
                    }
                }

                foreach (WebServiceDeviceCaptureController controller in wanted)
                {
                    if (!_sources.Exists(source => ReferenceEquals(source.Controller, controller)))
                    {
                        ProxyConnectionsCollection rows = controller.ProxyConnections;
                        controller.PropertyChanged += Controller_PropertyChanged;
                        if (rows != null)
                        {
                            rows.CollectionChanged += Rows_CollectionChanged;
                        }

                        _sources.Add((controller, rows));
                    }
                }
            }

            public void Dispose()
            {
                ReplayHistory history = _history;
                if (history == null)
                {
                    return;
                }

                _history = null;
                _changed = null;
                if (_watchingTabs)
                {
                    history._model.CaptureDeviceContexts.CollectionChanged -= Tabs_CollectionChanged;
                    _watchingTabs = false;
                }

                foreach ((WebServiceDeviceCaptureController controller, ProxyConnectionsCollection rows) in _sources)
                {
                    Detach(controller, rows);
                }

                _sources.Clear();
                history._observations.Remove(this);
            }

            private void Detach(WebServiceDeviceCaptureController controller, ProxyConnectionsCollection rows)
            {
                controller.PropertyChanged -= Controller_PropertyChanged;
                if (rows != null)
                {
                    rows.CollectionChanged -= Rows_CollectionChanged;
                }
            }

            private void Tabs_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) => OnSourceChanged();

            // Rows added change nothing that the history shows: the recorder adds each recorded row to the history itself.
            private void Rows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action != NotifyCollectionChangedAction.Add)
                {
                    OnSourceChanged();
                }
            }

            private void Controller_PropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                if (string.IsNullOrEmpty(e.PropertyName) ||
                    e.PropertyName == nameof(WebServiceDeviceCaptureController.ProxyConnections))
                {
                    OnSourceChanged();
                }
            }

            // A change that another thread reports is handled on the thread that the view observes from, the UI thread, where
            // the history and its views live; one that comes once the subscription is disposed is ignored there too (Notify).
            private void OnSourceChanged()
            {
                if (_history == null)
                {
                    return;
                }

                if (Environment.CurrentManagedThreadId == _observerThread)
                {
                    Notify();
                }
                else
                {
                    _observerContext?.Post(_ => Notify(), null);
                }
            }
        }
    }

    // One row that the Replays tab recorded, and its tab, by weak reference only: an entry holds no content and keeps
    // neither alive.
    internal sealed class ReplayHistoryEntry
    {
        private readonly WeakReference<ICaptureDeviceContext> _tab;
        private readonly WeakReference<ProxyConnectionModel> _row;

        internal ReplayHistoryEntry(ICaptureDeviceContext tab, ProxyConnectionModel row)
        {
            _tab = new WeakReference<ICaptureDeviceContext>(tab);
            _row = new WeakReference<ProxyConnectionModel>(row);
        }

        internal bool TryGet(out ICaptureDeviceContext tab, out ProxyConnectionModel row)
        {
            row = null;
            return _tab.TryGetTarget(out tab) && _row.TryGetTarget(out row);
        }
    }

    // What a history view shows of one live entry, made from its row when the view is built (ReplayHistory.Snapshot). Each
    // text is escaped and bounded for display only: Load and Show in Replays act on the row that TryResolve gives, never on
    // these. CanLoad says whether the row can be sent again (RequestReplayDraft.CanReplay), which only Load needs.
    internal sealed record ReplayHistoryPreview(
        ReplayHistoryEntry Entry,
        string TabNamePreview,
        long RequestNumber,
        DateTime RequestTime,
        string MethodPreview,
        string UrlPreview,
        string StatusPreview,
        bool CanLoad);
}
