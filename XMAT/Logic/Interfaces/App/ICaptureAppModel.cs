// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;

namespace XMAT.SharedInterfaces
{
    public interface ICaptureAppModel
    {
        IDatabase ActiveDatabase { get; }

        IReadonlyDatabase LoadedDatabase { get; }

        ObservableCollection<ICaptureDeviceContext> CaptureDeviceContexts { get; }
        ObservableCollection<ICaptureAnalysisRun> AnalysisRuns { get; }

        ICaptureDeviceContext SelectCaptureDeviceContext(string deviceContextName);
    }
}
