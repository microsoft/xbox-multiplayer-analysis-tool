// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum ECaptureAnalyzerResult
    {
        Success,
        NoSuitableData,
        UnknownError
    }

    public interface ICaptureAnalyzer
    {
        ICaptureMethod SupportedCaptureMethod { get; }

        string Description { get; }

        bool IsSelected { get; set; }

        ICaptureAnalyzerPreferences PreferencesModel { get; }

        void Initialize(ICaptureAppModel appModel);

        // TODO: this should be able to accommodate multiple device capture controllers
        // at some point in the future
        Task<ECaptureAnalyzerResult> RunAsync(ICaptureAnalysisRun analysisRun, IDeviceCaptureController captureController);

        void Shutdown();
    }
}
