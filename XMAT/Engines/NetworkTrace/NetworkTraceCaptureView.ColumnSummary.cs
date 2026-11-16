// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using XMAT;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.SharedInterfaces;

namespace NetworkTraceCaptureControls
{
    // The packet table's column summaries (see ColumnSummary), over the packets that every filter shows. Choosing a
    // protocol, address, PID or TID sets that filter to the value alone, as in a web proxy capture's traffic table; MAC
    // addresses and the media type have no filter. The timestamp, the flags and the length have no summary, since
    // their values are nearly all different or only yes and no.
    public partial class NetworkTraceCaptureView : IColumnSummarySource
    {
        ColumnSummaryColumn IColumnSummarySource.Describe(DataGridColumn column) =>
            column?.SortMemberPath switch
            {
                nameof(NetworkTracePacketDataModel.Protocol) =>
                    Filtered(packet => ProtocolName(packet.Protocol), ProtocolFilters, controller => controller.ProtocolFilterList,
                        packet => GetProtocolFilterName(packet.Protocol)),
                nameof(NetworkTracePacketDataModel.SourceIpv4Address) =>
                    Filtered(packet => packet.SourceIpv4Address, SourceIpFilters, controller => controller.SourceIpFilterList),
                nameof(NetworkTracePacketDataModel.DestinationIpv4Address) =>
                    Filtered(packet => packet.DestinationIpv4Address, DestIpFilters, controller => controller.DestIpFilterList),
                nameof(NetworkTracePacketDataModel.ProcessId) =>
                    Filtered(packet => Number(packet.ProcessId), PidFilters, controller => controller.PidFilterList),
                nameof(NetworkTracePacketDataModel.ThreadId) =>
                    Filtered(packet => Number(packet.ThreadId), TidFilters, controller => controller.TidFilterList),
                nameof(NetworkTracePacketDataModel.SourceMacAddress) => Unfiltered(packet => packet.SourceMacAddress),
                nameof(NetworkTracePacketDataModel.DestinationMacAddress) => Unfiltered(packet => packet.DestinationMacAddress),
                nameof(NetworkTracePacketDataModel.MediaType) => Unfiltered(packet => packet.MediaType.ToString()),
                _ => null,
            };

        private NetworkTraceCaptureController Controller =>
            (DataContext as ICaptureDeviceContext)?.CaptureController as NetworkTraceCaptureController;

        // A protocol as its column shows it: its name when it has one, such as TCP, or else its number.
        private static string ProtocolName(int protocol) =>
            Enum.IsDefined(typeof(NetworkProtocol), protocol)
                ? ((NetworkProtocol)protocol).ToString()
                : protocol.ToString(CultureInfo.InvariantCulture);

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string PacketCount(int count) =>
            ColumnSummaryModel.Counted(count, "COLUMN_SUMMARY_PACKET", "COLUMN_SUMMARY_PACKETS");

        private static ColumnSummaryColumn Unfiltered(Func<NetworkTracePacketDataModel, string> value) =>
            new(row => row is NetworkTracePacketDataModel packet ? value(packet) : null) { RowCountText = PacketCount };

        // A column with a checkbox filter, whose values are the filter's: FILTER TEXT gives a packet's value as the
        // filter names it, when that differs from how the column shows it.
        private ColumnSummaryColumn Filtered(
            Func<NetworkTracePacketDataModel, string> value,
            CheckBoxFilters filter,
            Func<NetworkTraceCaptureController, IEnumerable<CheckedListItem>> items,
            Func<NetworkTracePacketDataModel, string> filterText = null)
        {
            filterText ??= value;
            string FilterValue(ColumnSummaryValue entry) =>
                entry.Row is NetworkTracePacketDataModel packet ? filterText(packet) : null;
            return Unfiltered(value) with
            {
                CanSelect = entry => FilterValue(entry) is { } text && Controller is { } controller &&
                    items(controller)?.Any(item => string.Equals(item.Text, text, StringComparison.Ordinal)) == true,
                Select = entry =>
                {
                    if (FilterValue(entry) is { } text)
                    {
                        ShowOnly(filter, text);
                    }
                },
                Hint = Localization.GetLocalizedString("COLUMN_SUMMARY_HINT_VALUE"),
            };
        }

        // Sets FILTER to VALUE alone. Turning the filter on and checking only the value each refresh the table.
        private static void ShowOnly(CheckBoxFilters filter, string value)
        {
            filter.IsFilterEnabled = true;
            filter.CheckOnly(text => string.Equals(text, value, StringComparison.Ordinal));
        }
    }
}
