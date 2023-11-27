// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;
using XMAT.NetworkTrace;
using System.Diagnostics;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTraceCaptureAnalysis.Models;

namespace XMAT.NetworkTraceCaptureAnalysis
{
    public class NetworkTraceCaptureAnalyzer : ICaptureAnalyzer
    {
        public static ICaptureAnalyzer Analyzer { get { return AnalyzerInstance; } }

        internal static readonly NetworkTraceCaptureAnalyzer AnalyzerInstance = new();

        public ICaptureMethod SupportedCaptureMethod
        {
            get
            {
                return NetworkTraceCaptureMethod.Method;
            }
        }

        public bool IsSelected { get; set; }

        public string Description { get { return Localization.GetLocalizedString("NETCAP_ANALYSIS_TAB"); } }

        public override string ToString()
        {
            return Description;
        }

        internal static readonly NetworkTraceAnalysisOptionsModel PreferencesModelInstance = new();

        public ICaptureAnalyzerPreferences PreferencesModel { get { return PreferencesModelInstance; } }

        public void Initialize(ICaptureAppModel appModel)
        {
        }

        private string PrettyProtocolName(int protocolNum)
        {
            if (Enum.IsDefined(typeof(NetworkProtocol), protocolNum))
            {
                return $"{protocolNum} ({((NetworkProtocol)protocolNum).ToString()})";
            }

            return protocolNum.ToString();
        }

        public async Task<ECaptureAnalyzerResult> RunAsync(ICaptureAnalysisRun analysisRun, IDeviceCaptureController captureController)
        {
            NetworkTraceCaptureController controller = captureController as NetworkTraceCaptureController;

            if (controller.FilteredItems == null)
            {
                return ECaptureAnalyzerResult.NoSuitableData;
            }

            var selectedItems = new NetworkTracePacketDataModel[controller.FilteredItems.Count];

            controller.FilteredItems.CopyTo(selectedItems, 0);

            await Task.Run(() =>
            {
                NetworkTraceAnalyzerModel analyzerModel = new NetworkTraceAnalyzerModel();

                analysisRun.AnalysisData = analyzerModel;

                PublicUtilities.SafeInvoke(() => analyzerModel.NumericStatsLists.Add(
                    new NetworkTraceAnalyzerResultsModel
                    {
                        Topic = Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PCBP"),
                        Values =
                            from packet in selectedItems
                            orderby packet.Protocol
                            group packet by packet.Protocol into protocolGroup
                            select new NetworkTraceAnalyzerResultsDataModel
                            {
                                Name = PrettyProtocolName(protocolGroup.Key),
                                Value = protocolGroup.Count(),
                            }
                    }));

                PublicUtilities.SafeInvoke(() => analyzerModel.NumericStatsLists.Add(
                    new NetworkTraceAnalyzerResultsModel
                    {
                        Topic = Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PSBS"),
                        Values =
                            from packet in selectedItems
                            orderby packet.SourceIpv4Address
                            where (packet.Flags & NetworkTrace.Models.NetworkPacketFlags.Send) != 0
                            group packet by packet.SourceIpv4Address into sourceIpGroup
                            select new NetworkTraceAnalyzerResultsDataModel
                            {
                                Name = sourceIpGroup.Key,
                                Value = sourceIpGroup.Count(),
                            }
                    }));

                PublicUtilities.SafeInvoke(() => analyzerModel.NumericStatsLists.Add(
                    new NetworkTraceAnalyzerResultsModel
                    {
                        Topic = Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PRBS"),
                        Values =
                            from packet in selectedItems
                            orderby packet.SourceIpv4Address
                            where (packet.Flags & NetworkTrace.Models.NetworkPacketFlags.Receive) != 0
                            group packet by packet.SourceIpv4Address into sourceIpGroup
                            select new NetworkTraceAnalyzerResultsDataModel
                            {
                                Name = sourceIpGroup.Key,
                                Value = sourceIpGroup.Count(),
                            }
                    }));

                PublicUtilities.SafeInvoke(() => analyzerModel.NumericStatsLists.Add(
                    new NetworkTraceAnalyzerResultsModel
                    {
                        Topic = Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_BSBS"),
                        Values =
                            from packet in selectedItems
                            orderby packet.SourceIpv4Address
                            where (packet.Flags & NetworkTrace.Models.NetworkPacketFlags.Send) != 0
                            group packet by packet.SourceIpv4Address into sourceIpGroup
                            select new NetworkTraceAnalyzerResultsDataModel
                            {
                                Name = sourceIpGroup.Key,
                                Value = sourceIpGroup.Sum(p => p.Payload.Length),
                            }
                    }));

                PublicUtilities.SafeInvoke(() => analyzerModel.NumericStatsLists.Add(
                    new NetworkTraceAnalyzerResultsModel
                    {
                        Topic = Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_BRBS"),
                        Values =
                            from packet in selectedItems
                            orderby packet.SourceIpv4Address
                            where (packet.Flags & NetworkTrace.Models.NetworkPacketFlags.Receive) != 0
                            group packet by packet.SourceIpv4Address into sourceIpGroup
                            select new NetworkTraceAnalyzerResultsDataModel
                            {
                                Name = sourceIpGroup.Key,
                                Value = sourceIpGroup.Sum(p => p.Payload.Length),
                            }
                    }));

                PublicUtilities.SafeInvoke(() => analyzerModel.NumericStatsLists.Add(
                    new NetworkTraceAnalyzerResultsModel
                    {
                        Topic = Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PLBS"),
                        Values =
                             from packet in selectedItems
                             orderby packet.SourceIpv4Address
                             group packet by packet.SourceIpv4Address into sourceIpGroup
                             select new NetworkTraceAnalyzerResultsDataModel
                             {
                                 Name = sourceIpGroup.Key,
                                 Value = (int)sourceIpGroup.Average(p => p.Payload.Length),
                             }
                    }));

                PublicUtilities.SafeInvoke(() => analyzerModel.NumericStats.Add(
                    new NetworkTraceAnalyzerResultsDataModel
                    {
                        Name = Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_UPOM", PreferencesModelInstance.MaximumMTU),
                        Value =
                            (from packet in selectedItems
                             where packet.Protocol == (int)NetworkTrace.Models.NetworkProtocol.UDP && packet.Payload.Length > PreferencesModelInstance.MaximumMTU
                             select packet).Count()
                    }));

                var packetsPerMinuteDataQuery =
                    from packet in selectedItems
                    orderby packet.Timestamp
                    where (packet.Flags & NetworkTrace.Models.NetworkPacketFlags.Send) != 0
                    group packet by packet.Timestamp.ToLongTimeString() into timeBucket
                    select new
                    {
                        Time = timeBucket.Key,
                        Sources =
                            from packet in timeBucket
                            orderby packet.SourceIpv4Address
                            group packet by packet.SourceIpv4Address into packetBucket
                            select new
                            {
                                SourceIpv4Address = packetBucket.Key,
                                Packets = packetBucket.ToList(),
                            },
                    };

                int packetCount = 0;

                // Check for packets sent per second over threshold
                foreach (var bucket in packetsPerMinuteDataQuery)
                {
                    foreach (var source in bucket.Sources)
                    {
                        if (source.Packets.Count() > PreferencesModelInstance.PacketsPerSecondThreshold)
                        {
                            packetCount++;
                            PublicUtilities.AppLog(LogLevel.INFO, $"{source.SourceIpv4Address} at {bucket.Time} sent {source.Packets.Count()} packets; should be less than {PreferencesModelInstance.PacketsPerSecondThreshold}");
                        }
                    }
                }

                PublicUtilities.SafeInvoke(() => analyzerModel.NumericStats.Add(
                    new NetworkTraceAnalyzerResultsDataModel
                    {
                        Name = Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PSOT", PreferencesModelInstance.PacketsPerSecondThreshold),
                        Value = packetCount
                    }));

                // Check for duplicate packets
                packetCount = 0;

                for(int i = 0; i < selectedItems.Length; i++)
                {
                    // Get the starting model
                    var startModel = selectedItems[i];
                    var startTime = startModel.Timestamp.Ticks / TimeSpan.TicksPerMillisecond;

                    // Get the index of the next model
                    int sub = i + 1;

                    // Loop while the subsequent models are within the timeframe
                    while (sub < selectedItems.Length)
                    {
                        // Next model to check
                        var nextModel = selectedItems[sub];
                        var nextTime = nextModel.Timestamp.Ticks / TimeSpan.TicksPerMillisecond;

                        if (nextTime - startTime > PreferencesModelInstance.DuplicatePacketWindow)
                        {
                            // We're outside the window and can exit the loop
                            break;
                        }

                        if (nextModel.Payload == startModel.Payload)
                        {
                            packetCount++;
                            PublicUtilities.AppLog(LogLevel.INFO, $"Duplicate packets from {nextModel.SourceIpv4Address} detected at {nextModel.Timestamp.ToLongTimeString()}");
                        }

                        sub++;
                    }
                }

                PublicUtilities.SafeInvoke(() => analyzerModel.NumericStats.Add(
                    new NetworkTraceAnalyzerResultsDataModel
                    {
                        Name = Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_DUPS", PreferencesModelInstance.DuplicatePacketWindow),
                        Value = packetCount
                    }));

                PublicUtilities.SafeInvoke(() => analyzerModel.TotalPacketsScanned = selectedItems.Length);
            });

            return ECaptureAnalyzerResult.Success;
        }

        public void Shutdown()
        {
        }
    }
}
