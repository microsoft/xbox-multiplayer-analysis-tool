// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    [Collection(IsolatedProxyTests.Name)]
    public sealed class ProcessAttributionIntegrationTests
    {
        [Fact]
        public async Task TwoChildProcessesAreBothForwardedRecordedAndFilterable()
        {
            var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrivals = 0;
            await using var origin =
                await LoopbackOrigin.StartAsync(
                    async context =>
                    {
                        if (Interlocked.Increment(ref arrivals) == 2)
                        {
                            bothArrived.TrySetResult();
                        }
                        await release.Task.WaitAsync(context.RequestAborted);
                        await context.Response.WriteAsync("ok");
                    });
            await using var proxy = await ProxyTestHarness.StartAsync();
            await using var first = ControlledChildProxyClient.Start(proxy.Port, origin.Url("/first"));
            await using var second = ControlledChildProxyClient.Start(proxy.Port, origin.Url("/second"));
            try
            {
                await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(2, proxy.CapturedRequests.Count);
                ClientProcessContext[] contexts = proxy.CapturedRequestProcessContexts.ToArray();
                Assert.Equal(2, contexts.Length);
                Assert.All(contexts, context => Assert.NotNull(context));
                await Task.WhenAll(contexts.Select(WaitForIdentityAsync)).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(
                    new[]
                    {
                        first.ProcessId,
                        second.ProcessId,
                    }.OrderBy(value => value),
                    contexts.Select(context => context.Identity.ProcessId).OrderBy(value => value));

                CaptureSessionRef[] sessions =
                    contexts
                        .Select(
                            (context, index) =>
                                new CaptureSessionRef(
                                    new CaptureRowKey(
                                        Guid.NewGuid(),
                                        1,
                                        index + 1,
                                        index + 1),
                                    "d1",
                                    context.Identity))
                        .ToArray();
                OperationResult<
                    FilterCaptureSessionsResult> result =
                    await new FilterCaptureSessionsOperation()
                        .ExecuteAsync(
                            new FilterCaptureSessionsRequest(
                                sessions,
                                new ProcessFilterSpec(
                                    ImmutableArray.Create(
                                        first.ProcessId
                                            .ToString(
                                                System.Globalization
                                                    .CultureInfo
                                                    .InvariantCulture)),
                                    ImmutableArray<string>
                                        .Empty)),
                            OperationContext.None,
                            CancellationToken.None);

                Assert.Single(result.Value.IncludedKeys);
                Assert.Equal(2, proxy.CapturedRequests.Count);
            }
            finally
            {
                release.TrySetResult();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await first.WaitForExitAsync(timeout.Token);
                await second.WaitForExitAsync(timeout.Token);
            }
        }

        private static Task WaitForIdentityAsync(ClientProcessContext context)
        {
            if (context.Identity.Status != ClientProcessStatus.NotRecorded)
            {
                return Task.CompletedTask;
            }
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(ClientProcessContext sender, ClientProcessInfo info)
            {
                if (info.Status != ClientProcessStatus.NotRecorded)
                {
                    sender.IdentityChanged -= Changed;
                    completed.TrySetResult();
                }
            }
            context.IdentityChanged += Changed;
            if (context.Identity.Status != ClientProcessStatus.NotRecorded)
            {
                context.IdentityChanged -= Changed;
                completed.TrySetResult();
            }
            return completed.Task;
        }
    }
}
