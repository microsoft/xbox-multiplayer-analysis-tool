// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using XMAT.Operations.Captures;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    internal readonly record struct CaptureSourceStamp(
        Guid SourceId,
        long RetirementEpoch);

    internal sealed record CaptureDeleteTarget(
        CaptureRowKey Key,
        WeakReference<ProxyConnectionModel> Model);

    internal sealed record CaptureDeleteResult(
        int Requested,
        IReadOnlyList<CaptureRowKey> Deleted,
        int RetainedHidden,
        int Unavailable,
        CaptureSourceStamp SourceStamp);
}
