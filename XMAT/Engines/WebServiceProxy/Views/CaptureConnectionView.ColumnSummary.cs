// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using XMAT.Operations;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    // The traffic table's column summaries (see ColumnSummary), over the rows that every filter shows. Choosing a
    // Status, Host or Method value sets that filter to the value alone, and choosing a process shows only that process,
    // as Show only does; Scheme and Path have no filter. The # column has no summary, since every value is different.
    public partial class CaptureConnectionView : IColumnSummarySource
    {
        ColumnSummaryColumn IColumnSummarySource.Describe(DataGridColumn column) =>
            column?.SortMemberPath switch
            {
                nameof(ProxyConnectionModel.Status) =>
                    Filtered(model => model.Status, StatusFilters, () => _attachedController?.StatusFilterList, "COLUMN_SUMMARY_HINT_VALUE") with
                    {
                        EmptyValueText = Localization.GetLocalizedString("COLUMN_SUMMARY_NO_STATUS"),
                        BarOf = ColumnSummaryBars.ForHttpStatus,
                    },
                nameof(ProxyConnectionModel.Method) =>
                    Filtered(model => model.Method, MethodFilters, () => _attachedController?.MethodFilterList, "COLUMN_SUMMARY_HINT_VALUE"),
                nameof(ProxyConnectionModel.Host) =>
                    Filtered(model => model.Host, HostFilters, () => _attachedController?.HostFilterList, "COLUMN_SUMMARY_HINT_HOST") with
                    {
                        ValueCountText = count => ColumnSummaryModel.Counted(count, "COLUMN_SUMMARY_HOST", "COLUMN_SUMMARY_HOSTS"),
                        MoreValuesText = count => ColumnSummaryModel.Counted(count, "COLUMN_SUMMARY_MORE_HOST", "COLUMN_SUMMARY_MORE_HOSTS"),
                    },
                nameof(ProxyConnectionModel.Scheme) => Unfiltered(model => model.Scheme),
                nameof(ProxyConnectionModel.Path) => Unfiltered(model => model.Path),
                nameof(ProxyConnectionModel.ClientProcess) + "." + nameof(ClientProcessInfo.Name) => ProcessSummary(),
                _ => null,
            };

        private static string RequestCount(int count) =>
            ColumnSummaryModel.Counted(count, "COLUMN_SUMMARY_REQUEST", "COLUMN_SUMMARY_REQUESTS");

        private static ColumnSummaryColumn Unfiltered(Func<ProxyConnectionModel, string> value) =>
            new(row => row is ProxyConnectionModel model ? value(model) : null) { RowCountText = RequestCount };

        // A column with a checkbox filter: its values are the filter's.
        private ColumnSummaryColumn Filtered(
            Func<ProxyConnectionModel, string> value,
            CheckBoxFilters filter,
            Func<IEnumerable<CheckedListItem>> items,
            string hint) =>
            Unfiltered(value) with
            {
                CanSelect = entry => items()?.Any(item => string.Equals(item.Text, entry.Value, StringComparison.Ordinal)) == true,
                Select = entry => ShowOnly(filter, items(), entry.Value),
                Hint = Localization.GetLocalizedString(hint),
            };

        // Sets FILTER to VALUE alone, as the traffic overview sets a filter to one part.
        private void ShowOnly(CheckBoxFilters filter, IEnumerable<CheckedListItem> items, string value)
        {
            if (items == null)
            {
                return;
            }

            filter.CheckOnly(text => string.Equals(text, value, StringComparison.Ordinal));
            filter.IsFilterEnabled = true;
            UpdateActiveFilterCount();
            RequestFilterRefresh();
        }

        // Processes by name, or by id when the name is unknown, as Show only names them; Show only applies to processes
        // that are known.
        private ColumnSummaryColumn ProcessSummary() =>
            Unfiltered(model => ProcessFilter.IsUnknown(model.ClientProcess)
                ? CaptureProcessViewAdapter.DisplayText(model.ClientProcess, model.IsProcessResolving)
                : CaptureProcessViewAdapter.LabelOf(model.ClientProcess)) with
            {
                CanSelect = entry => ShowOnlySpec(entry) != null,
                Select = entry =>
                {
                    if (ShowOnlySpec(entry) is { } spec)
                    {
                        WebServiceProxyCommands.ShowOnlyProcess.Execute(spec, ConnectionList);
                    }
                },
                Hint = Localization.GetLocalizedString("COLUMN_SUMMARY_HINT_PROCESS"),
            };

        // The process filter that shows only ENTRY's process, or null when it has none.
        private ProcessFilterSpec ShowOnlySpec(ColumnSummaryValue entry)
        {
            string selector = CaptureProcessViewAdapter.SelectorOf((entry.Row as ProxyConnectionModel)?.ClientProcess);
            OperationResult<ProcessFilterSpec> only = selector == null
                ? null
                : CaptureProcessViewAdapter.WithOnly(_attachedController?.ProcessFilter ?? ProcessFilterSpec.Empty, selector);
            return only is { Succeeded: true } ? only.Value : null;
        }
    }
}
