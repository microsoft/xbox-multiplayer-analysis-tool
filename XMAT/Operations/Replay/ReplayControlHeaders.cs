// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // Headers that tell Fiddler how to send a request. XMAT never acts on them, and refuses a request that carries one,
    // whether it would send the request or export it, rather than go on as if it did: a captured header is never a
    // permission. Removing it makes the request usable. The draft's own lines are read, so a header that Connection
    // nominates, which the built request drops, is refused too.
    internal static class ReplayControlHeaders
    {
        internal const string Code = "replay.request.control-header";

        internal static IReadOnlyCollection<string> Names { get; } = new[]
        {
            "Fiddler-Host",
            "X-OverrideHost",
            "X-IgnoreCertCNMismatch",
            "X-OverrideGateway",
        };

        internal static bool Has(RequestReplayDraft draft) => draft.HasAnyHeader(Names);
    }
}
