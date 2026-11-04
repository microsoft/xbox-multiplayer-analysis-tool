// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Operations
{
    // A UI-free use case shared by the GUI and the command line. Implementations never show dialogs, never touch
    // System.Windows.Application, a Dispatcher or CommandManager, and report failures through the result.
    internal interface IOperation<TRequest, TResult>
    {
        Task<OperationResult<TResult>> ExecuteAsync(
            TRequest request, OperationContext context, CancellationToken cancellationToken);
    }
}
