// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Scripting;

namespace XMAT.ApplicationServices
{
    internal static class ScriptOperations
    {
        internal static ScriptStateSnapshot Update(ScriptModel script, string source, long? expectedRevision = null)
        {
            ArgumentNullException.ThrowIfNull(script);
            var before = script.CaptureState();
            if (!script.TryUpdateScript(source, expectedRevision ?? before.Revision, out var updated))
                throw new ScriptRevisionChangedException();
            return updated;
        }

        internal static async Task<ScriptActivationReceipt> SetEnabledAsync(
            ScriptModel script, bool enabled, long? expectedRevision = null, string expectedSourceHash = null,
            bool exactSourceOnly = false, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(script);
            cancellationToken.ThrowIfCancellationRequested();
            var before = script.CaptureState();
            if ((expectedRevision.HasValue && before.Revision != expectedRevision.Value) ||
                (expectedSourceHash != null && before.SourceHash != expectedSourceHash))
                throw new ScriptRevisionChangedException();
            if (before.IsEnabled == enabled && !before.IsCompiling)
                return new(enabled ? ScriptActivationResult.Enabled : ScriptActivationResult.Superseded,
                    before, false, Array.AsReadOnly(script.CompilationStatus));
            if (!enabled)
            {
                if (!script.TryDisableScript(before.Revision, out var disabled))
                    throw new ScriptRevisionChangedException();
                return new(ScriptActivationResult.Superseded, disabled, true, Array.AsReadOnly(script.CompilationStatus));
            }
            return await script.ActivateScriptAsync<WebServiceCaptureScriptParams>(
                before.Revision, before.SourceHash, exactSourceOnly, cancellationToken);
        }
    }
}
