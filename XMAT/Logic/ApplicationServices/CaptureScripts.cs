// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using XMAT.Models;
using XMAT.Scripting;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureScripts(CaptureAppModel model, CaptureCatalog catalog) : ICaptureScripts
    {
        public Task<IReadOnlyList<CaptureScriptDescriptor>> GetScriptsAsync(Guid captureId, CancellationToken cancellationToken = default) =>
            WithCaptureAsync<IReadOnlyList<CaptureScriptDescriptor>>(captureId, (target, proxy) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<CaptureScriptDescriptor>>(proxy.Scripts.Select((script, index) =>
                    Describe(captureId, Event(index), script.CaptureState())).ToArray());
            });

        public Task<IReadOnlyList<CaptureScriptSymbol>> GetSymbolsAsync(Guid captureId, CancellationToken cancellationToken = default) =>
            WithCaptureAsync<IReadOnlyList<CaptureScriptSymbol>>(captureId, (target, proxy) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<CaptureScriptSymbol>>(proxy.ScriptTypes.Select(Symbol).ToArray());
            });

        public Task<CaptureScriptSource> ReadAsync(Guid captureId, Guid scriptId, CancellationToken cancellationToken = default) =>
            WithScriptAsync(captureId, scriptId, (target, proxy, script, @event) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = script.CaptureState();
                return Task.FromResult(new CaptureScriptSource(Describe(captureId, @event, state), state.Source ?? string.Empty, target.DeviceName));
            });

        public Task<CaptureScriptEditResult> UpdateAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash, string source,
            CancellationToken cancellationToken = default) =>
            WithScriptAsync(captureId, scriptId, (target, proxy, script, @event) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(source);
                if (proxy.IsTransitioning)
                    throw new CaptureScriptException(CaptureScriptError.Busy);
                var before = Check(script, expectedRevision, expectedSourceHash);
                var updated = ScriptOperations.Update(script, source, before.Revision);
                return Task.FromResult(new CaptureScriptEditResult(Describe(captureId, @event, updated),
                    updated.Revision != before.Revision));
            });

        public Task<CaptureScriptValidation> ValidateAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash,
            CancellationToken cancellationToken = default) =>
            WithScriptAsync(captureId, scriptId, async (target, proxy, script, @event) =>
            {
                Check(script, expectedRevision, expectedSourceHash);
                var validation = await script.ValidateScriptAsync<WebServiceCaptureScriptParams>(
                    expectedRevision, expectedSourceHash, cancellationToken);
                var current = script.CaptureState();
                var attached = model.CaptureDeviceContexts.Contains(target) && proxy.Scripts.Contains(script);
                return new CaptureScriptValidation(Describe(captureId, @event, current), validation.Revision,
                    validation.SourceHash, attached && validation.IsCurrent,
                    validation.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                    validation.Diagnostics.Select(Diagnostic).ToArray());
            });

        public Task<CaptureScriptTransition> SetEnabledAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash, bool enabled,
            CancellationToken cancellationToken = default) =>
            WithScriptAsync(captureId, scriptId, async (target, proxy, script, @event) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = Check(script, expectedRevision, expectedSourceHash);
                if (proxy.IsTransitioning || (enabled && before.IsCompiling))
                    throw new CaptureScriptException(CaptureScriptError.Busy);
                var receipt = await ScriptOperations.SetEnabledAsync(script, enabled, expectedRevision, expectedSourceHash,
                    exactSourceOnly: true, cancellationToken);
                if (!model.CaptureDeviceContexts.Contains(target) || !proxy.Scripts.Contains(script))
                {
                    if (receipt.Changed)
                        script.TryDisableScript(receipt.State.Revision, out _);
                    throw new CaptureScriptException(CaptureScriptError.SourceChanged);
                }
                var current = script.CaptureState();
                return new CaptureScriptTransition(Describe(captureId, @event, receipt.State),
                    enabled ? receipt.Outcome switch
                    {
                        ScriptActivationResult.Enabled => CaptureScriptOutcome.Enabled,
                        ScriptActivationResult.Failed => CaptureScriptOutcome.CompilationFailed,
                        _ => CaptureScriptOutcome.SourceChanged
                    } : CaptureScriptOutcome.Disabled,
                    receipt.Changed, receipt.Diagnostics.Select(Diagnostic).ToArray(),
                    current.Revision == receipt.State.Revision && current.SourceHash == receipt.State.SourceHash);
            });

        public async Task<bool> DisableOwnedAsync(Guid captureId, Guid scriptId, long expectedRevision)
        {
            try
            {
                return await WithScriptAsync(captureId, scriptId, (target, proxy, script, @event) =>
                {
                    var state = script.CaptureState();
                    return Task.FromResult(state.Revision == expectedRevision &&
                        (state.IsEnabled || state.IsCompiling) && script.TryDisableScript(expectedRevision, out _));
                });
            }
            catch (CaptureScriptException exception) when (exception.Error is
                CaptureScriptError.TargetNotFound or CaptureScriptError.SourceChanged or CaptureScriptError.ReadOnlyCapture)
            {
                return false;
            }
        }

        private Task<T> WithScriptAsync<T>(
            Guid captureId, Guid scriptId,
            Func<ICaptureDeviceContext, WebServiceDeviceCaptureController, ScriptModel, CaptureScriptEvent, Task<T>> action) =>
            WithCaptureAsync(captureId, (target, proxy) =>
            {
                var index = -1;
                for (var position = 0; position < proxy.Scripts.Count; position++)
                {
                    if (proxy.Scripts[position]?.Id == scriptId)
                    { index = position; break; }
                }
                if (index < 0)
                    throw new CaptureScriptException(CaptureScriptError.TargetNotFound);
                return action(target, proxy, proxy.Scripts[index], Event(index));
            });

        private async Task<T> WithCaptureAsync<T>(
            Guid captureId, Func<ICaptureDeviceContext, WebServiceDeviceCaptureController, Task<T>> action)
        {
            try
            {
                return await catalog.WithTargetAsync(captureId, null, (target, descriptor) =>
                {
                    if (target.IsReadOnly)
                        throw new CaptureScriptException(CaptureScriptError.ReadOnlyCapture);
                    if (target.CaptureType != CaptureType.WebProxy || target.CaptureController is not WebServiceDeviceCaptureController proxy)
                        throw new CaptureScriptException(CaptureScriptError.UnsupportedCapture);
                    return action(target, proxy);
                });
            }
            catch (AgentAccessException exception) when (exception.Error is AgentAccessError.OperationNotFound or AgentAccessError.SourceChanged)
            {
                throw new CaptureScriptException(CaptureScriptError.TargetNotFound);
            }
            catch (ScriptRevisionChangedException) { throw new CaptureScriptException(CaptureScriptError.SourceChanged); }
        }

        private static ScriptStateSnapshot Check(ScriptModel script, long revision, string hash)
        {
            var state = script.CaptureState();
            if (state.Revision != revision || hash == null || state.SourceHash != hash)
                throw new CaptureScriptException(CaptureScriptError.SourceChanged);
            return state;
        }

        private static CaptureScriptDescriptor Describe(Guid captureId, CaptureScriptEvent @event, ScriptStateSnapshot state) =>
            new(state.Id, captureId, @event, state.Revision, state.SourceHash,
                Encoding.UTF8.GetByteCount(state.Source ?? string.Empty), state.IsEnabled, state.IsCompiling);

        private static CaptureScriptEvent Event(int index) => Enum.IsDefined((CaptureScriptEvent)index)
            ? (CaptureScriptEvent)index : throw new CaptureScriptException(CaptureScriptError.UnsupportedCapture);

        private static CaptureScriptSymbol Symbol(ScriptTypeInfo symbol) =>
            new(symbol.Name, symbol.Type, symbol.Description,
                symbol.Properties?.Select(Symbol).ToArray() ?? Array.Empty<CaptureScriptSymbol>());

        private static CaptureScriptDiagnostic Diagnostic(Diagnostic diagnostic)
        {
            var text = diagnostic.GetMessage(CultureInfo.InvariantCulture);
            var truncated = text.Length > 2048;
            if (truncated)
                text = text[..(char.IsHighSurrogate(text[2047]) ? 2047 : 2048)];
            var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
            return new(diagnostic.Id, diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => CaptureScriptDiagnosticSeverity.Error,
                DiagnosticSeverity.Warning => CaptureScriptDiagnosticSeverity.Warning,
                DiagnosticSeverity.Info => CaptureScriptDiagnosticSeverity.Info,
                _ => CaptureScriptDiagnosticSeverity.Hidden
            }, text, diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null,
                diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : null,
                diagnostic.Location.IsInSource ? span.EndLinePosition.Line + 1 : null,
                diagnostic.Location.IsInSource ? span.EndLinePosition.Character + 1 : null, truncated);
        }
    }
}
