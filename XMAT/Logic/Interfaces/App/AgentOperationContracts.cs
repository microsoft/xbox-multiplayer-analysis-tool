// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace XMAT.SharedInterfaces
{
    public enum AgentCapability
    {
        SummaryRead,
        RawDataRead,
        CaptureControl,
        DeviceConfiguration,
        ScriptRead,
        ScriptEdit,
        ScriptExecute,
        HostPolicy,
        Preferences,
        Diagnostics,
        Navigation,
        Lifecycle,
        QueryResultExport,
        DeviceCreation,
        FileWrite,
        CaptureExport,
        ScriptExport,
        FileRead,
        CaptureLoad,
        CaptureImport,
        AnalysisReportExport
    }

    public enum AgentScopeKind { Application, Capture, Source, ExportFolder, InputFile }

    public sealed record AgentScope
    {
        public AgentScopeKind Kind { get; }
        public Guid Id { get; }

        public AgentScope(AgentScopeKind kind, Guid id)
        {
            if (!Enum.IsDefined(kind) ||
                (kind == AgentScopeKind.Application ? id != Guid.Empty : id == Guid.Empty))
            {
                throw new ArgumentException("The scope kind and identity do not agree.", nameof(id));
            }
            Kind = kind;
            Id = id;
        }

        public static AgentScope Application { get; } = new(AgentScopeKind.Application, Guid.Empty);

        public static AgentScope Capture(Guid id) => ForResource(AgentScopeKind.Capture, id);
        public static AgentScope Source(Guid id) => ForResource(AgentScopeKind.Source, id);
        public static AgentScope ExportFolder(Guid id) => ForResource(AgentScopeKind.ExportFolder, id);
        public static AgentScope InputFile(Guid id) => ForResource(AgentScopeKind.InputFile, id);

        private static AgentScope ForResource(AgentScopeKind kind, Guid id)
        {
            if (id == Guid.Empty)
            {
                throw new ArgumentException("A resource scope requires an identity.", nameof(id));
            }
            return new(kind, id);
        }
    }

    public sealed record AgentPermission
    {
        public AgentCapability Capability { get; }
        public AgentScope Scope { get; }

        public AgentPermission(AgentCapability capability, AgentScope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);
            var allowed = capability switch
            {
                AgentCapability.SummaryRead => true,
                AgentCapability.RawDataRead or AgentCapability.QueryResultExport or
                AgentCapability.CaptureExport or AgentCapability.CaptureImport or AgentCapability.AnalysisReportExport =>
                    scope.Kind == AgentScopeKind.Source,
                AgentCapability.FileWrite => scope.Kind == AgentScopeKind.ExportFolder,
                AgentCapability.FileRead => scope.Kind == AgentScopeKind.InputFile,
                AgentCapability.CaptureControl or AgentCapability.DeviceConfiguration or
                AgentCapability.ScriptRead or AgentCapability.ScriptEdit or
                AgentCapability.ScriptExecute or AgentCapability.ScriptExport => scope.Kind == AgentScopeKind.Capture,
                AgentCapability.HostPolicy or AgentCapability.Preferences or
                AgentCapability.Diagnostics or AgentCapability.Navigation or
                AgentCapability.Lifecycle or AgentCapability.DeviceCreation or AgentCapability.CaptureLoad =>
                    scope.Kind == AgentScopeKind.Application,
                _ => false
            };
            if (!allowed)
            {
                throw new ArgumentException("The capability does not support this scope.", nameof(scope));
            }
            Capability = capability;
            Scope = scope;
        }
    }

    [Flags]
    public enum AgentOperationEffects
    {
        None = 0,
        StateChange = 1,
        NetworkConfiguration = 2,
        ConsoleRestart = 4,
        DataDeletion = 8,
        FileOverwrite = 16,
        TrustedCodeExecution = 32,
        SensitiveDataExport = 64,
        HumanOwnedResource = 128
    }

    public enum AgentSessionState { Connected, Revoked, Disconnected }

    public enum AgentOperationState
    {
        AwaitingApproval,
        Queued,
        Running,
        CancellationRequested,
        Succeeded,
        Failed,
        Cancelled,
        Denied,
        SourceChanged,
        RecoveryRequired
    }

    public enum AgentAccessError
    {
        AccessDisabled,
        SessionClosed,
        PermissionsUpdating,
        PermissionDenied,
        OperationNotFound,
        OperationNotFinished,
        ApprovalRequired,
        IdempotencyConflict,
        LimitExceeded,
        OwnershipConflict,
        ResultUnavailable,
        ExecutionFailed,
        SourceChanged,
        OperationUnavailable,
        UnsupportedOperation,
        QueryFailed,
        QueryTimeout,
        RecoveryRequired,
        InvalidRequest,
        ScriptCompilationFailed,
        ElevationRequired,
        GdkRequired,
        PersistenceFailed,
        NoDefaultConsole,
        DestinationExists,
        InvalidPath,
        InvalidCapture
    }

    public sealed class AgentAccessException(
        AgentAccessError error,
        JsonElement? protectedResult = null) : InvalidOperationException(error.ToString())
    {
        public AgentAccessError Error { get; } = error;
        public JsonElement? ProtectedResult { get; } = protectedResult?.Clone();
    }

    public sealed record AgentLimits
    {
        public int ResponseRows { get; }
        public int ResponseBytes { get; }
        public int MaximumOperations { get; }
        public int QueryTimeoutSeconds { get; }
        public long MaximumQueryResultBytes { get; }
        public int MaximumQueryResults { get; }

        public AgentLimits(
            int responseRows = 100,
            int responseBytes = 64 * 1024,
            int maximumOperations = 1024,
            int queryTimeoutSeconds = 60,
            long maximumQueryResultBytes = 512L * 1024 * 1024,
            int maximumQueryResults = 4)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(responseRows);
            ArgumentOutOfRangeException.ThrowIfLessThan(responseBytes, 256);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOperations);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queryTimeoutSeconds);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(queryTimeoutSeconds, (int)((uint.MaxValue - 1L) / 1000));
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumQueryResultBytes, 16 * 1024);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumQueryResultBytes, int.MaxValue);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumQueryResults);
            ResponseRows = responseRows;
            ResponseBytes = responseBytes;
            MaximumOperations = maximumOperations;
            QueryTimeoutSeconds = queryTimeoutSeconds;
            MaximumQueryResultBytes = maximumQueryResultBytes;
            MaximumQueryResults = maximumQueryResults;
        }
    }

    public sealed record AgentSessionSnapshot(
        Guid Id,
        string DeclaredClientName,
        AgentSessionState State,
        bool PermissionsUpdating,
        IReadOnlyList<AgentPermission> Permissions,
        AgentLimits Limits);

    public sealed record AgentOperationSnapshot(
        Guid Id,
        Guid SessionId,
        string Action,
        AgentOperationState State,
        AgentAccessError? Error,
        AgentOperationEffects Effects,
        IReadOnlyList<AgentPermission> Requirements,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt,
        bool ExecutionStarted,
        bool EffectsMayHaveOccurred,
        bool HasResult);

    public enum AgentResourceKind { Capture, Script, QueryResult, Artifact, CaptureData, ScriptValidation }
    public enum AgentResourceState { Owned, Cleaning, RecoveryRequired }
    public enum AgentCleanupOutcome { Released, NoLongerOwned, RecoveryRequired }

    public readonly record struct AgentResourceKey(AgentResourceKind Kind, Guid Id);

    public sealed record AgentOwnedResourceSnapshot(
        AgentResourceKey Resource,
        Guid SessionId,
        string Revision,
        AgentResourceState State);
}
