// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests.HeadlessParity
{
    // The stateful Windows host needs a dispatcher without a WPF Application. Keep its exact threading calls
    // separate from the legacy GUI ratchet; neither profile permits offline operations to call this adapter.
    internal static class RuntimeDispatchProfile
    {
        private const string Prefix = "XboxMultiplayerAnalysisTool|XMAT.ApplicationDispatch::";
        private const string Dispatcher = "|System.Windows.Threading.Dispatcher::";

        internal static IReadOnlyDictionary<string, int> Calls { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [Prefix + "BindRuntime" + Dispatcher + "VerifyAccess"] = 1,
            [Prefix + "HasUiAccess" + Dispatcher + "CheckAccess"] = 1,
            [Prefix + "IsUiShuttingDown" + Dispatcher + "get_HasShutdownFinished"] = 1,
            [Prefix + "IsUiShuttingDown" + Dispatcher + "get_HasShutdownStarted"] = 1,
            [Prefix + "PostToUi" + Dispatcher + "BeginInvoke"] = 1,
            [Prefix + "QueueOnUi" + Dispatcher + "InvokeAsync"] = 2,
            [Prefix + "RunOnUiAsync" + Dispatcher + "InvokeAsync"] = 3,
            [Prefix + "RunRuntime" + Dispatcher + "get_CurrentDispatcher"] = 1,
            [Prefix + "RunRuntime" + Dispatcher + "BeginInvoke"] = 1,
            [Prefix + "RunRuntime" + Dispatcher + "BeginInvokeShutdown"] = 1,
            [Prefix + "RunRuntime" + Dispatcher + "Run"] = 1,
            [Prefix + "VerifyUiAccess" + Dispatcher + "VerifyAccess"] = 1
        };

        internal static bool Matches(ForbiddenCall call) =>
            Calls.TryGetValue(call.Key, out var count) && call.Count == count;
    }
}
