// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests
{
    internal static class GcTestSupport
    {
        internal static void ForceCollection()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        // Collects until nothing references the targets any more, at most three times, letting finished work leave its
        // threads in between.
        internal static async Task CollectAsync(params WeakReference[] references)
        {
            for (int attempt = 0; attempt < 3 && references.Any(reference => reference.IsAlive); attempt++)
            {
                ForceCollection();
                await Task.Delay(50);
            }
        }
    }
}
