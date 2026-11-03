// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum CaptureScriptEvent { SslConnectionRequest, WebRequest, WebResponse }
    public enum CaptureScriptOutcome { Enabled, Disabled, CompilationFailed, SourceChanged }
    public enum CaptureScriptError { TargetNotFound, UnsupportedCapture, ReadOnlyCapture, SourceChanged, Busy, InvalidSource }
    public enum CaptureScriptDiagnosticSeverity { Hidden, Info, Warning, Error }

    public sealed class CaptureScriptException(CaptureScriptError error) : InvalidOperationException(error.ToString())
    {
        public CaptureScriptError Error { get; } = error;
    }

    public sealed record CaptureScriptDescriptor(
        Guid Id, Guid CaptureId, CaptureScriptEvent Event, long Revision, string SourceHash,
        long SourceBytes, bool IsEnabled, bool IsCompiling);

    public sealed record CaptureScriptSource(CaptureScriptDescriptor Script, string Source, string CaptureName);
    public sealed record CaptureScriptSymbol(string Name, string Type, string Description, IReadOnlyList<CaptureScriptSymbol> Properties);
    public sealed record CaptureScriptEditResult(CaptureScriptDescriptor Script, bool Changed);
    public sealed record CaptureScriptDiagnostic(
        string Id, CaptureScriptDiagnosticSeverity Severity, string Message,
        int? StartLine, int? StartColumn, int? EndLine, int? EndColumn, bool IsTruncated);
    public sealed record CaptureScriptValidation(
        CaptureScriptDescriptor Script, long ValidatedRevision, string SourceHash,
        bool IsCurrent, bool HasErrors, IReadOnlyList<CaptureScriptDiagnostic> Diagnostics);
    public sealed record CaptureScriptTransition(
        CaptureScriptDescriptor Script, CaptureScriptOutcome Outcome, bool Changed,
        IReadOnlyList<CaptureScriptDiagnostic> Diagnostics, bool IsCurrent = true);

    public interface ICaptureScripts
    {
        Task<IReadOnlyList<CaptureScriptDescriptor>> GetScriptsAsync(Guid captureId, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CaptureScriptSymbol>> GetSymbolsAsync(Guid captureId, CancellationToken cancellationToken = default);
        Task<CaptureScriptSource> ReadAsync(Guid captureId, Guid scriptId, CancellationToken cancellationToken = default);
        Task<CaptureScriptEditResult> UpdateAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash, string source,
            CancellationToken cancellationToken = default);
        Task<CaptureScriptValidation> ValidateAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash,
            CancellationToken cancellationToken = default);
        Task<CaptureScriptTransition> SetEnabledAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash, bool enabled,
            CancellationToken cancellationToken = default);
        Task<bool> DisableOwnedAsync(Guid captureId, Guid scriptId, long expectedRevision);
    }
}
