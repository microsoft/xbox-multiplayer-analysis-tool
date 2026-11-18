// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;

namespace XMAT.SharedInterfaces
{
    internal interface ICaptureTextSourceProvider
    {
        CaptureTextSource CaptureTextSource { get; }
    }
}
