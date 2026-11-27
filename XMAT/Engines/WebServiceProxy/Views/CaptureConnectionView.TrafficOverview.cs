// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace XMAT.WebServiceCapture
{
    public partial class CaptureConnectionView
    {
        // Selecting a status class or a host in the traffic overview sets the Status or Host filter to show only it: every
        // status of that class, or only that host. Selecting it again, while the filter still shows only that part, clears
        // the filter.
        private void TrafficOverview_FilterRequested(object sender, TrafficFilterRequestedEventArgs e)
        {
            if (_attachedController == null)
            {
                return;
            }

            if (e.StatusClass is { } statusClass)
            {
                ShowOnlyOverviewPart(
                    StatusFilters, _attachedController.StatusFilterList,
                    status => TrafficSummary.ClassOf(status) == statusClass);
            }
            else if (e.Host != null)
            {
                ShowOnlyOverviewPart(
                    HostFilters, _attachedController.HostFilterList,
                    host => string.Equals(host, e.Host, StringComparison.Ordinal));
            }
        }

        private void ShowOnlyOverviewPart(CheckBoxFilters filter, IEnumerable<CheckedListItem> values, Func<string, bool> isPart)
        {
            List<CheckedListItem> items = (values ?? Enumerable.Empty<CheckedListItem>()).ToList();
            if (!items.Any(item => isPart(item.Text ?? string.Empty)))
            {
                return;
            }

            bool showsOnlyPart = filter.IsFilterEnabled && items.Any(item => item.IsChecked) &&
                items.All(item => !item.IsChecked || isPart(item.Text ?? string.Empty));
            filter.CheckOnly(value => showsOnlyPart || isPart(value));
            filter.IsFilterEnabled = !showsOnlyPart;
            UpdateActiveFilterCount();
            RequestFilterRefresh();
        }
    }
}
