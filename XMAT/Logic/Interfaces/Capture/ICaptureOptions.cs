// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.SharedInterfaces
{
    public interface ICaptureOptions
    {
        void Initialize(ICaptureMethod captureMethod);

        void EnableOptions();

        void DisableOptions();
    }
}
