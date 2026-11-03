// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.ApplicationServices
{
    internal sealed class HostPolicyOperations(HostListPolicy policy, Dispatcher dispatcher) : IHostPolicyOperations
    {
        public Task<HostPolicySnapshot> InspectAsync(Guid? sessionId = null, CancellationToken cancellationToken = default) =>
            InvokeAsync(() => policy.Inspect(sessionId), cancellationToken);

        public Task<HostPolicyPlan> PrepareAsync(
            HostPolicyRequest request, Guid? sessionId = null, CancellationToken cancellationToken = default) =>
            InvokeAsync(() => policy.Prepare(request, sessionId), cancellationToken);

        public Task<HostPolicyChange> ExecuteAsync(
            HostPolicyPlan plan, Guid? sessionId = null, CancellationToken cancellationToken = default) =>
            InvokeAsync(() => policy.Execute(plan, sessionId), cancellationToken);

        private Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            return dispatcher.CheckAccess() ? Task.FromResult(Run()) : dispatcher.InvokeAsync(Run).Task;
            T Run()
            {
                cancellationToken.ThrowIfCancellationRequested();
                return action();
            }
        }
    }
}
