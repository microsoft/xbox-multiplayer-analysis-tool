// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using XMAT.SharedInterfaces;

namespace XMAT.Operations.Analysis
{
    // The web-proxy requests of one device in a capture file, with the ordinal id (d1, d2, ...) that analysis reports.
    internal sealed record CaptureDeviceRecords(string Id, IReadOnlyList<IDataRecord> Records);

    // A capture file read with the analysis input rules (AnalyzeCaptureOperation.ReadCapture), for operations that need
    // its requests rather than findings.
    internal sealed record CaptureRecordSet(string InputKind, string InputSha256, IReadOnlyList<CaptureDeviceRecords> Devices);
}
