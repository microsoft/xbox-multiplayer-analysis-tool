// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.Operations.Processes
{
    public enum ClientProcessStatus
    {
        NotRecorded,
        Local,
        Imported,
        Remote,
        Unresolved
    }

    public sealed record ClientProcessInfo(
        string Name,
        int ProcessId,
        DateTimeOffset? StartTimeUtc,
        ClientProcessStatus Status)
    {
        public static ClientProcessInfo NotRecorded { get; } =
            new(string.Empty, 0, null, ClientProcessStatus.NotRecorded);
    }
}
