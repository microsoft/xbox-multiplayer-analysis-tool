// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public sealed record CaptureClearReceipt(Guid CaptureId, long Revision, long CaptureEpoch, bool Cleared, bool ViewsNotified);
    public interface ICaptureDataOperations
    {
        Task<CaptureClearReceipt> ClearAsync(
            Guid captureId, long expectedRevision, CancellationToken cancellationToken = default);
    }
}
