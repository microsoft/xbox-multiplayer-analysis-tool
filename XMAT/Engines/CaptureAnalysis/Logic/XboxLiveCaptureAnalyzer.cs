// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CaptureAnalysisEngine;
using XMAT.ApplicationServices;
using XMAT.Models;
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

        public XboxLiveCaptureAnalyzer() : this(AppContext.BaseDirectory, () => PublicUtilities.StorageDirectoryPath) { }

        internal XboxLiveCaptureAnalyzer(
            string installDirectory, Func<string> outputRootProvider,
            Action<Type, string> exceptionObserver = null, Func<AnalysisWorkBudget> workBudgetFactory = null)
        {
            _installDirectory = installDirectory;
            _outputRootProvider = outputRootProvider ?? throw new ArgumentNullException(nameof(outputRootProvider));
            _exceptionObserver = exceptionObserver;
            _workBudgetFactory = workBudgetFactory;
        }

        internal XboxLiveCaptureAnalyzer(TraceAnalyzer analyzer, ICaptureAppModel appModel = null, string outputRoot = null)
            : this(AppContext.BaseDirectory, () => outputRoot ?? PublicUtilities.StorageDirectoryPath)
        {
            _captureAppModel = appModel;
            _operation = AnalyzeCaptureOperation.FromAnalyzer(analyzer);
            _operation.ExceptionObserver = ObserveOperationException;
        }

        public static ICaptureAnalyzer Analyzer => AnalyzerInstance;
        internal static readonly XboxLiveCaptureAnalyzer AnalyzerInstance = new();
        internal bool IsInitialized => _operation != null;
        public override string ToString() => Description;
        public ICaptureMethod SupportedCaptureMethod => WebServiceCaptureMethod.Method;
        public string Description => Localization.GetLocalizedString("WEBCAP_ANALYZER_TITLE");
        public bool IsSelected { get; set; }
        public ICaptureAnalyzerPreferences PreferencesModel => null;

        public void Initialize(ICaptureAppModel appModel)
        {
            _captureAppModel = appModel;
            _operation = null;
            var assets = AnalysisAssets.Load(_installDirectory, AnalysisAssetLoadPolicy.GuiCompatible);
            _operation = new AnalyzeCaptureOperation(assets) { ExceptionObserver = ObserveOperationException };
            if (_workBudgetFactory != null)
                _operation.WorkBudgetFactory = _workBudgetFactory;
        }

        public async Task<ECaptureAnalyzerResult> RunAsync(
            ICaptureAnalysisRun analysisRun, IDeviceCaptureController captureController)
        {
            var model = new AnalysisResultsModel();
            analysisRun.AnalysisData = model;
            if (captureController is not WebServiceDeviceCaptureController controller || _operation == null)
                return ECaptureAnalyzerResult.UnknownError;
            var directory = Path.Combine(_outputRootProvider(), $"xlca_{analysisRun.Id}");
            model.OutputDirectory = directory;
            model.SourceCaptureName = controller.DeviceName;
            XboxLiveAnalysisResult canonical;
            try
            {
                if (_captureAppModel is CaptureAppModel application && application.HasInvestigationTarget(controller))
                {
                    var result = await ((CaptureInvestigation)application.CaptureInvestigation).AnalyzeDesktopAsync(
                        analysisRun, controller, this, directory);
                    model.InvestigationData = result;
                    var details = (CaptureDataDetails)result.Details;
                    canonical = details.CanonicalWebAnalysis;
                    await details.WriteDesktopArtifactsAsync(directory);
                }
                else
                {
                    var result = await ExecuteRecordsAsync(controller.GetCaptureRecords(), controller.DeviceName,
                        directory, step => SetProcessStatus(model, step), CancellationToken.None);
                    if (result.Diagnostics.Any(diagnostic => diagnostic.Code == AnalyzeCaptureOperation.WorkLimitCode))
                        return ECaptureAnalyzerResult.WorkLimit;
                    if (result.Outcome is OperationOutcome.InvalidInput or OperationOutcome.NoAnalyzableData)
                        return ECaptureAnalyzerResult.NoSuitableData;
                    if (!result.Succeeded || result.Value?.Canonical == null)
                        return ECaptureAnalyzerResult.UnknownError;
                    canonical = Canonical(result.Value.Canonical);
                }
            }
            catch (AnalysisWorkLimitException) { return ECaptureAnalyzerResult.WorkLimit; }
            model.CanonicalAnalysis = canonical;
            if (canonical.Analysis.Consoles.Count == 0)
                return ECaptureAnalyzerResult.NoSuitableData;
            foreach (var report in canonical.Analysis.Consoles[0].Reports)
            {
                var view = ReportViewModelFactory.CreateForDocument(
                    _captureAppModel, controller.DeviceName, directory, report);
                if (view != null && view.Show)
                    model.Reports.Add(view);
            }
            if (model.InvestigationData?.Details is CaptureDataDetails completed)
                await completed.AdoptDesktopFilesAsync(directory);
            SetProcessStatus(model, AnalysisResultsModel.ProcessStep.Complete);
            return ECaptureAnalyzerResult.Success;
        }

        public void Shutdown() => _operation = null;

        internal XboxLiveAnalysisResult AnalyzeRecords(
            IEnumerable<IDataRecord> records, string outputDirectory = null, long runId = 0,
            Action<AnalysisResultsModel.ProcessStep> progress = null, CancellationToken cancellationToken = default)
        {
            if (_operation == null)
                throw new InvalidOperationException("The Xbox Live analyzer is not initialized.");
            var result = ExecuteRecordsAsync(records, string.Empty, outputDirectory, progress, cancellationToken)
                .GetAwaiter().GetResult();
            if (result.Outcome == OperationOutcome.Cancelled)
                throw new OperationCanceledException(cancellationToken);
            if (result.Diagnostics.Any(diagnostic => diagnostic.Code == AnalyzeCaptureOperation.WorkLimitCode))
                throw new AnalysisWorkLimitException();
            if (result.Value?.Canonical != null)
                return Canonical(result.Value.Canonical);
            throw new InvalidOperationException("The shared capture analysis operation could not complete.");
        }

        private Task<OperationResult<AnalysisResult>> ExecuteRecordsAsync(
            IEnumerable<IDataRecord> records, string deviceName, string outputDirectory,
            Action<AnalysisResultsModel.ProcessStep> progress, CancellationToken cancellationToken) =>
            _operation.ExecuteAsync(new AnalyzeCaptureRequest(AnalysisInput.FromRecords(records, deviceName),
                new AnalysisOptions(ProduceLegacyReports: outputDirectory != null, RetainCanonicalResults: true)),
                new OperationContext(outputDirectory, progress == null ? null : new ProcessProgress(progress)), cancellationToken);

        private static XboxLiveAnalysisResult Canonical(CanonicalAnalysisResult value) =>
            new(value.Analysis, value.RecordByBreadcrumb, value.ConvertedRecords, value.SkippedRecords);

        private static void SetProcessStatus(AnalysisResultsModel model, AnalysisResultsModel.ProcessStep step) =>
            PublicUtilities.SafeInvoke(() => model.ActiveProcessStep = step);

        private void ObserveOperationException(Type type, string stack)
        {
            if (_exceptionObserver != null)
            {
                try
                { _exceptionObserver(type, stack); }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception)) { }
                return;
            }
            PublicUtilities.AppLog(LogLevel.ERROR, $"Capture analysis failed ({type.FullName}).{Environment.NewLine}{stack}");
        }

        private static bool TryGetProcessStep(string phase, out AnalysisResultsModel.ProcessStep step)
        {
            step = phase switch
            {
                "analysis.convert" => AnalysisResultsModel.ProcessStep.ConvertingProxyConnectionsToServiceCallItems,
                "analysis.group" => AnalysisResultsModel.ProcessStep.CreatingServiceDataFromCallItems,
                "analysis.convert-uris" => AnalysisResultsModel.ProcessStep.RunningUrlConverterOnData,
                "analysis.rules" => AnalysisResultsModel.ProcessStep.RunningValidationRules,
                "analysis.dump-data" => AnalysisResultsModel.ProcessStep.DumpingServiceCallData,
                "analysis.dump-items" => AnalysisResultsModel.ProcessStep.DumpingServiceCallItems,
                "analysis.dump-results" => AnalysisResultsModel.ProcessStep.DumpingRuleResults,
                _ => AnalysisResultsModel.ProcessStep.NotStarted
            };
            return step != AnalysisResultsModel.ProcessStep.NotStarted;
        }

        private sealed class ProcessProgress(Action<AnalysisResultsModel.ProcessStep> action) : IProgress<OperationProgress>
        {
            public void Report(OperationProgress value)
            {
                if (TryGetProcessStep(value.Phase, out var step))
                    action(step);
            }
        }

        internal static ServiceCallItem ProxyConnectionModelToServiceCallItem(IDataRecord record) =>
            WebServiceCaptureRecordConverter.Convert(record);
    }
}
