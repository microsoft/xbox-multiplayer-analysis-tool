// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XMAT.SharedInterfaces
{
    public interface ICaptureOptions
    {
        void Initialize(ICaptureMethod captureMethod);

        void EnableOptions();

        void DisableOptions();
    }
}
