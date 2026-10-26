// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.XboxLiveCaptureAnalysis.Models;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;

namespace XMAT.XboxLiveCaptureAnalysis
{
    public class XboxLiveCaptureAnalyzer : ICaptureAnalyzer
    {
        private static string ApiMapFilePath => CombineWithRelativeSegmentsOnly(PublicUtilities.InstallDirectoryPath, "data", "XboxLiveTraceAnalyzer.APIMap.csv");
        private static string RulesFilePath => CombineWithRelativeSegmentsOnly(PublicUtilities.InstallDirectoryPath, "data", "XboxLiveTraceAnalyzer.Rules.json");

        private static string CombineWithRelativeSegmentsOnly(string firstSegment, params string[] remainingSegments)
        {
            if (remainingSegments.Where(Path.IsPathRooted).Any())
            {
                throw new ArgumentException("Path segments after the first must be relative.", nameof(remainingSegments));
            }

            var combinedPath = firstSegment;
            foreach (var segment in remainingSegments)
            {
                combinedPath = Path.Combine(combinedPath, segment);
            }

            return combinedPath;
        }

        private ICaptureAppModel _captureAppModel;
        private TraceAnalyzer _traceAnalyzer;

        public static ICaptureAnalyzer Analyzer { get { return AnalyzerInstance; } }

        internal static readonly XboxLiveCaptureAnalyzer AnalyzerInstance = new();

        public override string ToString()
        {
            return Description;
        }

        public ICaptureMethod SupportedCaptureMethod
        {
            get
            {
                return WebServiceCaptureMethod.Method;
            }
        }

        public string Description { get { return Localization.GetLocalizedString("WEBCAP_ANALYZER_TITLE"); } }

        public bool IsSelected { get; set; }

        // TODO: implement me!
        public ICaptureAnalyzerPreferences PreferencesModel { get { return null; } }

        public void Initialize(ICaptureAppModel appModel)
        {
            _captureAppModel = appModel;

            _traceAnalyzer = new TraceAnalyzer();
            _traceAnalyzer.LoadURIMap(ApiMapFilePath);
            _traceAnalyzer.LoadRules(RulesFilePath);
            // TODO: allow the user through the options UI for this analysis
            // to specify the reports they want
            _traceAnalyzer.AddReports(
                new Report[] {
                    new PerEndpointReport(),
                    new CallReport(),
                    new StatsReport()
                });
        }

        public async Task<ECaptureAnalyzerResult> RunAsync(ICaptureAnalysisRun analysisRun, IDeviceCaptureController captureController)
        {
            var analysisResultsModel = new AnalysisResultsModel();
            analysisRun.AnalysisData = analysisResultsModel;

            WebServiceDeviceCaptureController controller =
                captureController as WebServiceDeviceCaptureController;

            var sourceCaptureName = controller.DeviceName;

            // TODO: expose options for parameters
            var isInternal = false;
            var allEndpoints = false;
            Dictionary<string, IEnumerable<ReportDocument>> perConsoleReports = null;

            string analysisRunStorageDirectory = Path.Combine(
                PublicUtilities.StorageDirectoryPath,
                $"xlca_{analysisRun.Id}");

            Directory.CreateDirectory(analysisRunStorageDirectory);

            // TODO: support cancellation of the analysis processing
            await Task.Run(() =>
            {
                var rulesEngine = new RulesEngine(isInternal);
                var serviceCallData = new ServiceCallData(allEndpoints);

                // The application holds an analysis lease while converting these records,
                // so writable capture data remains quiescent without a second full copy.
                SetProcessStatus(analysisResultsModel, AnalysisResultsModel.ProcessStep.ConvertingProxyConnectionsToServiceCallItems);
                List<ServiceCallItem> serviceCallItems =
                    GetServiceCallItemsForProxyConnections(controller);

                SetProcessStatus(analysisResultsModel, AnalysisResultsModel.ProcessStep.CreatingServiceDataFromCallItems);
                serviceCallData.CreateFromServiceCallItems(serviceCallItems);

                SetProcessStatus(analysisResultsModel, AnalysisResultsModel.ProcessStep.RunningUrlConverterOnData);
                _traceAnalyzer.RunUriConverterOnData(serviceCallData);

                SetProcessStatus(analysisResultsModel, AnalysisResultsModel.ProcessStep.RunningValidationRules);
                perConsoleReports = _traceAnalyzer.Run(
                    rulesEngine,
                    serviceCallData,
                    analysisRunStorageDirectory);

                SetProcessStatus(analysisResultsModel, AnalysisResultsModel.ProcessStep.DumpingServiceCallData);
                rulesEngine.DumpServiceCallData(
                    Path.Combine(
                        analysisRunStorageDirectory,
                        $"xlca_{analysisRun.Id}_service_call_data.txt"),
                    serviceCallData);

                SetProcessStatus(analysisResultsModel, AnalysisResultsModel.ProcessStep.DumpingServiceCallItems);
                rulesEngine.DumpServiceCallItems(
                    Path.Combine(
                        analysisRunStorageDirectory,
                        $"xlca_{analysisRun.Id}_service_call_items.txt"),
                    serviceCallItems);

                SetProcessStatus(analysisResultsModel, AnalysisResultsModel.ProcessStep.DumpingRuleResults);
                rulesEngine.DumpResultsToFile(
                    Path.Combine(
                        analysisRunStorageDirectory,
                        $"xlca_{analysisRun.Id}_results.txt"));
            });

            // at this point we should show any report views
            // NOTE: for now we are just showing the reports for the first
            // console that we found in the data

            if (perConsoleReports.Count == 0)
            {
                return ECaptureAnalyzerResult.NoSuitableData;
            }

            var firstReports = perConsoleReports.First();
            foreach (var report in firstReports.Value)
            {
                ReportViewModel model = ReportViewModelFactory.CreateForDocument(
                    _captureAppModel,
                    sourceCaptureName,
                    analysisRunStorageDirectory,
                    report);
                if (model != null && model.Show)
                {
                    analysisResultsModel.Reports.Add(model);
                }
            }

            SetProcessStatus(analysisResultsModel, AnalysisResultsModel.ProcessStep.Complete);
            return ECaptureAnalyzerResult.Success;
        }

        public void Shutdown()
        {
            _traceAnalyzer = null;
        }

        private void SetProcessStatus(AnalysisResultsModel model, AnalysisResultsModel.ProcessStep processStep)
        {
            PublicUtilities.SafeInvoke(() => model.ActiveProcessStep = processStep);
        }

        private List<ServiceCallItem> GetServiceCallItemsForProxyConnections(
            WebServiceDeviceCaptureController controller)
        {
            List<ServiceCallItem> items = new();

            foreach (IDataRecord record in controller.GetCaptureRecords())
            {
                ServiceCallItem item = ProxyConnectionModelToServiceCallItem(record);
                if (item != null)
                {
                    items.Add(item);
                }
            }
            return items;
        }

        // TODO: move this to a more appropriate place
        private static WebHeaderCollection GetRequestHeadersForString(
            string requestLineAndHeaders)
        {
            var headers = new WebHeaderCollection();
            if (!string.IsNullOrEmpty(requestLineAndHeaders))
            {
                string[] lines = requestLineAndHeaders.Split("\r\n");
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (!string.IsNullOrEmpty(line) && line.Contains(':'))
                    {
                        headers.Add(line);
                    }
                }
            }
            return headers;
        }

        private static string[] AllowedNonAnalyzedHosts = { "playfab.com", "playfabapi.com" };

        internal static ServiceCallItem ProxyConnectionModelToServiceCallItem(
            IDataRecord dataRecord)
        {
            var requestNumber = dataRecord.Int(WebServiceCaptureMethod.FieldKey_RequestNumber);
            var requestTimestamp = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestTimestamp);
            var responseTimestamp = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ResponseTimestamp);
            var requestScheme = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestScheme);
            var requestHost = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestHost);
            var requestPort = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestPort);
            var requestMethod = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestMethod);
            var requestPath = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestPath);
            var requestStatus = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestStatus);
            var requestLineAndHeaders = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders);
            var requestBodyBase64 = dataRecord.Str(WebServiceCaptureMethod.FieldKey_RequestBody);
            var responseLineAndHeaders = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders);
            var responseBodyBase64 = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ResponseBody);
            var clientIP = dataRecord.Str(WebServiceCaptureMethod.FieldKey_ClientIP);

            ServiceCallItem item = new ServiceCallItem((UInt32)requestNumber);

            // we should ignore all CONNECT(s)
            if (requestMethod == @"CONNECT")
            {
                return null;
            }

            // we should ignore empty timestamps
            if (string.IsNullOrEmpty(requestTimestamp) || string.IsNullOrEmpty(responseTimestamp))
            {
                return null;
            }

            // TODO: we should ignore invalid protocols/calls
            // eg. Fiddler test frames start with "http:///" and should be ignored

            // finally ignore models based on rules
            // TODO: LTA supports a "custom user agent" for filtering and we should expose
            // that option as well
            var requestHeaders = GetRequestHeadersForString(requestLineAndHeaders);
            if (!Utils.IsAnalyzedService(requestHeaders, null))
            {
                if (!AllowedNonAnalyzedHosts.Any(host => requestHost.Contains(host)))
                {
                    return null;
                }
            }

            var requestTime = DateTime.Parse(requestTimestamp);
            var duration = DateTime.Parse(responseTimestamp) - requestTime;

            // note: obtained from "Host" request header if possible
            if (requestHeaders[HttpRequestHeader.Host] != null)
            {
                item.Host = requestHeaders[HttpRequestHeader.Host];
            }
            else
            {
                item.Host = requestHost.Split(':')[0];
            }

            // The logical Host label above is unchanged; host-list actions use provenance: a
            // structured capture host that is a host identity wins, otherwise the captured Host
            // header parsed as an authority is used. Text that is not a host identity is kept
            // but never actionable.
            item.EndpointKind = EndpointKind.Captured;
            item.PhysicalHost = GetPhysicalHost(requestHost, requestHeaders[HttpRequestHeader.Host]);

            // note: obtained from the .Scheme & .Call
            item.Uri = UriUtils.GetAbsoluteUri(
                requestScheme,
                requestHost,
                requestPort,
                requestPath);

            item.XboxUserId = Utils.GetXboxUserID(requestPath);
            item.ReqHeader = requestLineAndHeaders;
            item.ReqBody = Encoding.ASCII.GetString(
                Convert.FromBase64String(requestBodyBase64));

            item.RspHeader = responseLineAndHeaders;
            item.RspBody = Encoding.ASCII.GetString(
                Convert.FromBase64String(responseBodyBase64));

            item.ConsoleIP = clientIP;
            item.HttpStatusCode = String.IsNullOrEmpty(requestStatus) ? 0 : Convert.ToUInt32(requestStatus);
            item.ReqBodyHash = (UInt64)item.ReqBody.GetHashCode();
            item.ElapsedCallTimeMs = (ulong)duration.TotalMilliseconds;
            item.ReqTimeUTC = (ulong)requestTime.ToFileTimeUtc();
            item.Method = requestMethod;

            return item;
        }

        private static string GetPhysicalHost(string structuredHost, string hostHeader)
        {
            if (HostAuthority.TryNormalizeHost(structuredHost, out string host))
            {
                return host;
            }
            return HostAuthority.TryParse(hostHeader, out host, out _) ? host : null;
        }
    }
}
