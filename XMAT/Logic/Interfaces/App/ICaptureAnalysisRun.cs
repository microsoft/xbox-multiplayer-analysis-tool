// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using XMAT.Operations.Processes;

namespace XMAT.SharedInterfaces
{
    public interface ICaptureAnalysisRun
    {
        Int64 Id { get; }
        bool IsProcessing { get; }
        object AnalysisData { get; set; }

        // The client-process selectors fixed when the run starts; empty analyzes every session.
        ProcessFilterSpec ProcessFilter { get; }
    }
}
