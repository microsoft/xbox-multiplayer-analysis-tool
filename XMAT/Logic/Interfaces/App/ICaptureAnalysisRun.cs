// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace XMAT.SharedInterfaces
{
    public interface ICaptureAnalysisRun
    {
        Int64 Id { get; }
        bool IsProcessing { get; }
        object AnalysisData { get; set; }
    }
}
