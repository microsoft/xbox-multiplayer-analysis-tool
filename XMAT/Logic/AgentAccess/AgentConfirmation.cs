// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed record AgentScriptVersion(Guid Id, string SourceHash);

    internal sealed record AgentConfirmation(
        Guid OperationId, string ConfirmationToken, string ArgumentsHash, AgentOperationEffects Effects,
        string Warning, DateTimeOffset ExpiresAt, bool AutomaticBackups, bool HumanAttestation = false);
}
