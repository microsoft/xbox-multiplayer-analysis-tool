// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace XMAT.Hosting
{
    internal static class StatefulAgentHost
    {
        internal static int Run(
            AgentHostArguments options, Func<Dispatcher, ApplicationRuntime> createRuntime = null,
            Func<ApplicationRuntime, Task> ready = null) => ApplicationDispatch.RunRuntime(async dispatcher =>
        {
            await using var runtime = createRuntime?.Invoke(dispatcher) ??
                new ApplicationRuntime(dispatcher, ApplicationHostMode.Headless, instanceId: options.InstanceId);
            runtime.Initialize();
            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Model.HostLifecycle.Configure(() => stop.TrySetResult());
            await runtime.MarkReadyAsync();
            if (ready != null)
                await ready(runtime);
            while (true)
            {
                await stop.Task;
                try
                {
                    await runtime.DisposeAsync();
                    return 0;
                }
                catch (System.Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    await runtime.ResumeAfterBlockedShutdownAsync();
                }
            }
        });
    }
}
