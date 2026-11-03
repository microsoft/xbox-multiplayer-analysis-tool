// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTraceCaptureAnalysis.Models;
using XMAT.SharedInterfaces;

namespace XMAT.NetworkTraceCaptureAnalysis
{
    public class NetworkTraceCaptureAnalyzer : ICaptureAnalyzer
    {
        public static ICaptureAnalyzer Analyzer => AnalyzerInstance;
        internal static readonly NetworkTraceCaptureAnalyzer AnalyzerInstance = new();
        internal static readonly NetworkTraceAnalysisOptionsModel PreferencesModelInstance = new();
        private CaptureAppModel _appModel;

        public ICaptureMethod SupportedCaptureMethod => NetworkTraceCaptureMethod.Method;
        public bool IsSelected { get; set; }
        public string Description => Localization.GetLocalizedString("NETCAP_ANALYSIS_TAB");
        public ICaptureAnalyzerPreferences PreferencesModel => PreferencesModelInstance;
        public override string ToString() => Description;
        public void Initialize(ICaptureAppModel appModel) => _appModel = appModel as CaptureAppModel;
        public void Shutdown() => _appModel = null;

        internal static NetworkTraceStatisticsOptions GetOptions() => new(
            PreferencesModelInstance.MaximumMTU,
            PreferencesModelInstance.PacketsPerSecondThreshold,
            PreferencesModelInstance.DuplicatePacketWindow);

        public async Task<ECaptureAnalyzerResult> RunAsync(ICaptureAnalysisRun analysisRun, IDeviceCaptureController captureController)
        {
            var controller = captureController as NetworkTraceCaptureController;
            if (controller?.FilteredItems == null)
                return ECaptureAnalyzerResult.NoSuitableData;
            var selected = new NetworkTracePacketDataModel[controller.FilteredItems.Count];
            controller.FilteredItems.CopyTo(selected, 0);
            var options = GetOptions();
            NetworkTraceStatisticsResult canonical;
            CaptureDataResult investigation = null;
            if (_appModel?.HasInvestigationTarget(controller) == true)
            {
                investigation = await ((CaptureInvestigation)_appModel.CaptureInvestigation).AnalyzeDesktopNetworkAsync(
                    analysisRun, controller, selected, options);
                canonical = ((CaptureDataDetails)investigation.Details).CanonicalNetworkAnalysis;
            }
            else
            {
                canonical = await Task.Run(() => NetworkTraceStatistics.Analyze(
                    selected, options, log: text => PublicUtilities.AppLog(LogLevel.INFO, text)));
            }
            PublicUtilities.SafeInvoke(() =>
            {
                var model = new NetworkTraceAnalyzerModel { CanonicalAnalysis = canonical, InvestigationData = investigation };
                analysisRun.AnalysisData = model;
                foreach (var id in new[]
                {
                    "packet.protocol", "packet.sent.count", "packet.received.count",
                    "packet.sent.bytes", "packet.received.bytes", "packet.source.average-bytes"
                })
                {
                    var group = canonical.Statistics.Where(statistic => statistic.Id == id).ToArray();
                    model.NumericStatsLists.Add(new NetworkTraceAnalyzerResultsModel
                    {
                        Topic = Topic(id, options),
                        Values = group.Select(statistic => new NetworkTraceAnalyzerResultsDataModel
                        {
                            Name = statistic.Id == "packet.protocol"
                                ? ProtocolName(int.Parse(statistic.Group, CultureInfo.InvariantCulture)) : statistic.Group,
                            Value = statistic.Value
                        }).ToArray()
                    });
                }
                foreach (var statistic in canonical.Statistics.Where(statistic => statistic.Group == null &&
                    statistic.Id is "packet.udp-over-mtu" or "packet.send-second-over-threshold" or "packet.matching-payload-pairs"))
                {
                    model.NumericStats.Add(new NetworkTraceAnalyzerResultsDataModel
                    {
                        Name = Topic(statistic.Id, options),
                        Value = statistic.Value
                    });
                }
                model.TotalPacketsScanned = canonical.TotalPackets;
            });
            return ECaptureAnalyzerResult.Success;
        }

        private static string ProtocolName(int number) => Enum.IsDefined(typeof(NetworkProtocol), number)
            ? $"{number} ({(NetworkProtocol)number})" : number.ToString(CultureInfo.InvariantCulture);

        private static string Topic(string id, NetworkTraceStatisticsOptions options) => id switch
        {
            "packet.protocol" => Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PCBP"),
            "packet.sent.count" => Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PSBS"),
            "packet.received.count" => Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PRBS"),
            "packet.sent.bytes" => Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_BSBS"),
            "packet.received.bytes" => Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_BRBS"),
            "packet.source.average-bytes" => Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PLBS"),
            "packet.udp-over-mtu" => Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_UPOM", options.MaximumMtu),
            "packet.send-second-over-threshold" => Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PSOT", options.PacketsPerSecondThreshold),
            "packet.matching-payload-pairs" => Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_DUPS", options.DuplicateWindowMilliseconds),
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
    }
}
