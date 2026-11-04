// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Operations.Export
{
    internal sealed class CaptureExportPublisher : ICaptureExportPublisher
    {
        public Task<PublishedCapture> PublishAsync(
            string destination,
            bool replaceExisting,
            Action<string> validateDestination,
            Func<string, CancellationToken, Task> writeStaged,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
