// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace XMAT.WebServiceCapture
{
    // The search box at the start of the filter row (CaptureSearch). It filters the table with the Status, Host and
    // Method filters and the process filter, and while anything filters the table, it says how many of the capture's
    // requests the table shows. Ctrl+F in the view moves to it.
    public partial class CaptureConnectionView
    {
        // Typing applies once it pauses, so each key doesn't filter a large capture again; emptying the box applies at once.
        private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

        private CaptureSearch _search = CaptureSearch.Empty;
        private DispatcherTimer _searchTimer;
        private DispatcherOperation _pendingSearchCount;

        public static string SearchName => Localization.GetLocalizedString("CAPTURE_SEARCH_NAME");

        public static string SearchHint => Localization.GetLocalizedString("CAPTURE_SEARCH_HINT");

        public static string SearchHelp => Localization.GetLocalizedString("CAPTURE_SEARCH_HELP");

        internal CaptureSearch Search => _search;

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (!e.Handled && e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && SearchBox.IsVisible)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                ApplySearch();
                return;
            }

            if (_searchTimer == null)
            {
                _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SearchDelay };
                _searchTimer.Tick += (_, _) => ApplySearch();
            }

            _searchTimer.Stop();
            _searchTimer.Start();
        }

        // Filters the table by what the search box says now.
        internal void ApplySearch()
        {
            _searchTimer?.Stop();
            _search = CaptureSearch.Parse(SearchBox.Text);
            SearchBox.Problem = _search.Problems.Count > 0 ? _search.Problems[0] : null;
            RequestFilterRefresh();
            RequestSearchCount();
        }

        private void RequestSearchCount()
        {
            if (_pendingSearchCount?.Status != DispatcherOperationStatus.Pending)
            {
                _pendingSearchCount = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateSearchCount));
            }
        }

        // "12 of 340" while anything filters the table; nothing while it shows every request.
        private void UpdateSearchCount()
        {
            _pendingSearchCount = null;
            int total = _attachedController?.ProxyConnections?.Count ?? 0;
            SearchBox.Summary = ConnectionList.Items.Filter == null || _attachedController == null
                ? null
                : Localization.GetLocalizedString(
                    "CAPTURE_SEARCH_COUNT",
                    ConnectionList.Items.Count.ToString("N0", CultureInfo.CurrentCulture),
                    total.ToString("N0", CultureInfo.CurrentCulture));
        }
    }
}
