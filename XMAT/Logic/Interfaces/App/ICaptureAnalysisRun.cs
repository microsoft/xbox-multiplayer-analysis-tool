// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

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
