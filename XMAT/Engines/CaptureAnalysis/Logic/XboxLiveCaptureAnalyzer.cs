// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CaptureAnalysisEngine;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.XboxLiveCaptureAnalysis.Models;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;

namespace XMAT.XboxLiveCaptureAnalysis
{
    public class XboxLiveCaptureAnalyzer : ICaptureAnalyzer
    {
        private readonly string _installDirectory;
        private readonly Func<string> _outputRootProvider;
        private readonly Action<Type, string> _exceptionObserver;
        private readonly Func<AnalysisWorkBudget> _workBudgetFactory;
        private ICaptureAppModel _captureAppModel;
        private AnalyzeCaptureOperation _operation;

        public XboxLiveCaptureAnalyzer()
            : this(
                AppContext.BaseDirectory,
                () => PublicUtilities.StorageDirectoryPath,
                null,
                null)
        {
        }

        internal XboxLiveCaptureAnalyzer(
            string installDirectory,
            Func<string> outputRootProvider,
            Action<Type, string> exceptionObserver = null,
            Func<AnalysisWorkBudget> workBudgetFactory = null)
        {
            _installDirectory = installDirectory;
            _outputRootProvider = outputRootProvider ??
                throw new ArgumentNullException(nameof(outputRootProvider));
            _exceptionObserver = exceptionObserver;
            _workBudgetFactory = workBudgetFactory;
        }

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
            _operation = null;
            var assets = AnalysisAssets.Load(
                _installDirectory,
                AnalysisAssetLoadPolicy.GuiCompatible);
            _operation = new AnalyzeCaptureOperation(assets)
            {
                ExceptionObserver = ObserveOperationException,
            };
            if (_workBudgetFactory != null)
            {
                _operation.WorkBudgetFactory =
                    _workBudgetFactory;
            }
        }

        public async Task<ECaptureAnalyzerResult> RunAsync(ICaptureAnalysisRun analysisRun, IDeviceCaptureController captureController)
        {
            var analysisResultsModel = new AnalysisResultsModel();
            analysisRun.AnalysisData = analysisResultsModel;

            WebServiceDeviceCaptureController controller =
                captureController as WebServiceDeviceCaptureController;
            if (controller == null || _operation == null)
            {
                return ECaptureAnalyzerResult.UnknownError;
            }

            string analysisRunStorageDirectory = Path.Combine(
                _outputRootProvider(),
                $"xlca_{analysisRun.Id}");
            analysisResultsModel.OutputDirectory =
                analysisRunStorageDirectory;
            analysisResultsModel.SourceCaptureName = controller.DeviceName;
            var progress = new Progress<OperationProgress>(update =>
            {
                if (TryGetProcessStep(update.Phase, out var processStep))
                {
                    SetProcessStatus(analysisResultsModel, processStep);
                }
            });
            var result = await _operation.ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromRecords(
                        controller.GetCaptureRecords(),
                        controller.DeviceName),
                    new AnalysisOptions(
                        ProduceLegacyReports: true)),
                new OperationContext(
                    workingDirectory: analysisRunStorageDirectory,
                    progress: progress),
                CancellationToken.None);
            if (result.Diagnostics.Any(diagnostic =>
                    diagnostic.Code ==
                    AnalyzeCaptureOperation.WorkLimitCode))
            {
                return ECaptureAnalyzerResult.WorkLimit;
            }
            if (result.Outcome is OperationOutcome.InvalidInput or
                OperationOutcome.NoAnalyzableData)
            {
                return ECaptureAnalyzerResult.NoSuitableData;
            }
            if (!result.Succeeded ||
                result.Value?.Legacy?.PerConsoleReports == null)
            {
                return ECaptureAnalyzerResult.UnknownError;
            }

            // at this point we should show any report views
            // NOTE: for now we are just showing the reports for the first
            // console that we found in the data

            if (result.Value.Legacy.PerConsoleReports.Count == 0)
            {
                return ECaptureAnalyzerResult.NoSuitableData;
            }

            var firstReports = result.Value.Legacy.PerConsoleReports.First();
            foreach (var report in firstReports.Value)
            {
                ReportViewModel model = ReportViewModelFactory.CreateForDocument(
                    _captureAppModel,
                    controller.DeviceName,
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
            _operation = null;
        }

        private void SetProcessStatus(AnalysisResultsModel model, AnalysisResultsModel.ProcessStep processStep)
        {
            PublicUtilities.SafeInvoke(() => model.ActiveProcessStep = processStep);
        }

        private void ObserveOperationException(
            Type type,
            string stack)
        {
            if (_exceptionObserver != null)
            {
                try
                {
                    _exceptionObserver(type, stack);
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    // GUI diagnostics are best effort.
                }
                return;
            }

            PublicUtilities.AppLog(
                LogLevel.ERROR,
                $"Capture analysis failed ({type.FullName}).{Environment.NewLine}{stack}");
        }

        private static bool TryGetProcessStep(
            string phase,
            out AnalysisResultsModel.ProcessStep processStep)
        {
            processStep = phase switch
            {
                "analysis.convert" =>
                    AnalysisResultsModel.ProcessStep.ConvertingProxyConnectionsToServiceCallItems,
                "analysis.group" =>
                    AnalysisResultsModel.ProcessStep.CreatingServiceDataFromCallItems,
                "analysis.convert-uris" =>
                    AnalysisResultsModel.ProcessStep.RunningUrlConverterOnData,
                "analysis.rules" =>
                    AnalysisResultsModel.ProcessStep.RunningValidationRules,
                "analysis.dump-data" =>
                    AnalysisResultsModel.ProcessStep.DumpingServiceCallData,
                "analysis.dump-items" =>
                    AnalysisResultsModel.ProcessStep.DumpingServiceCallItems,
                "analysis.dump-results" =>
                    AnalysisResultsModel.ProcessStep.DumpingRuleResults,
                _ => AnalysisResultsModel.ProcessStep.NotStarted,
            };
            return processStep != AnalysisResultsModel.ProcessStep.NotStarted;
        }

        // The command line shares this conversion (WebServiceCaptureRecordConverter); the GUI analyzer keeps its
        // entry point for existing callers.
        internal static ServiceCallItem ProxyConnectionModelToServiceCallItem(
            IDataRecord dataRecord) =>
            WebServiceCaptureRecordConverter.Convert(dataRecord);
    }
}
