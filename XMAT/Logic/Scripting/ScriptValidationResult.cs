// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace XMAT.Scripting
{
    public sealed record ScriptValidationResult(
        string SourceHash,
        bool IsCurrent,
        IReadOnlyList<Diagnostic> Diagnostics,
        long Revision = 0);

    internal sealed record ScriptStateSnapshot(
        Guid Id, long Revision, string SourceHash, string Source, bool IsEnabled, bool IsCompiling);

    internal sealed record ScriptActivationReceipt(
        ScriptActivationResult Outcome, ScriptStateSnapshot State, bool Changed, IReadOnlyList<Diagnostic> Diagnostics);

    internal sealed class ScriptRevisionChangedException() : InvalidOperationException("The script revision changed.") { }
}
