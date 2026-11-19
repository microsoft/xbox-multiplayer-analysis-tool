// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Linq;
using System.Windows;
using XMAT.Operations.Processes;

namespace XMAT.WebServiceCapture
{
    // The new look's Clear filters, in the filter row between the dropdowns and Process. It is available while anything
    // filters the table: the Status, Host and Method filters, the process filter or the search, and shows every request
    // again.
    public partial class CaptureConnectionView
    {
        // What the Status filter calls a request without a status.
        public static string NoStatusText => Localization.GetLocalizedString("TRAFFIC_OVERVIEW_NO_STATUS");

        private bool IsAnythingFiltered =>
            new[] { StatusFilters, HostFilters, MethodFilters }.Any(filter => filter is { IsFilterEnabled: true }) ||
            (_attachedController != null && !ProcessFilter.IsEmpty(_processSpec)) ||
            _search.IsActive;

        private void UpdateClearFilters()
        {
            if (ClearFiltersButton != null)
            {
                ClearFiltersButton.IsEnabled = LookState.Current.IsNewLook && IsAnythingFiltered;
            }
        }

        private void ClearFilters_Click(object sender, RoutedEventArgs e) => ClearFilters();

        // Turns every filter off and empties the search; the table refreshes once.
        internal void ClearFilters()
        {
            foreach (CheckBoxFilters filter in new[] { StatusFilters, HostFilters, MethodFilters })
            {
                filter.ShowAll();
            }

            SetProcessFilter(ProcessFilterSpec.Empty);
            SearchBox.Clear();
            ApplySearch();
            UpdateActiveFilterCount();
        }

        private void LookState_Changed(object sender, PropertyChangedEventArgs e) => UpdateClearFilters();
    }
}
