// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.NetworkTrace.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Models
{
    internal sealed record CaptureInvestigationRecordTarget(
        ICaptureDeviceContext Context, CaptureDescriptor Capture,
        ProxyConnectionModel WebProxyRecord, NetworkTracePacketDataModel NetworkPacket);
}
