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

        // Selects the open tab whose capture controller this is, found by reference, and returns it; null when no open tab
        // has it, such as once the tab is closed. Tabs are never selected by name, which several tabs can share.
        ICaptureDeviceContext SelectCaptureDeviceContext(IDeviceCaptureController captureController);
    }
}
