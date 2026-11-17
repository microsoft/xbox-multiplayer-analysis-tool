// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace XMAT.Operations.Captures
{
    internal static class CaptureDeviceIds
    {
        internal static string Ordinal(int zeroBasedIndex) =>
            "d" + (zeroBasedIndex + 1).ToString(CultureInfo.InvariantCulture);
    }
}
