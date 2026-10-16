// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests.Proxy
{
    // Tests that hold thread-pool threads (to simulate work that ignores cancellation) or keep
    // several cores busy run alone, after the parallel collections. In parallel on a small CI
    // runner with coverage they starved unrelated timing-sensitive tests and their own deadlines.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class IsolatedProxyTests
    {
        public const string Name = "Proxy tests that run alone";
    }
}
