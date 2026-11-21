// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum CaptureProxyAction { Enable, Disable, ApplyPendingPort }

    public enum CaptureProxyPhase
    {
        None,
        StartingListener,
        DisablingDevice,
        WaitingForDisabled,
        SavingPort,
        EnablingDevice,
        WaitingForEnabled,
        StoppingListener,
        Complete
    }

    public enum CaptureProxyFailure
    {
        None,
        InvalidRequest,
        TargetNotFound,
        SourceChanged,
        CaptureBusy,
        ReadOnlyCapture,
        UnsupportedCapture,
        GdkRequired,
        NoLocalAddress,
        PendingPortChange,
        NoPendingPort,
        ConfigurationUnknown,
        ForeignEndpoint,
        ConfigurationTimeout,
        PersistenceFailed,
        RecoveryRequired,
        ExecutionFailed
    }

    public sealed record CaptureProxyPreflight(
        Guid CaptureId,
        long ExpectedRevision,
        CaptureProxyAction Action,
        DeviceType DeviceType,
        string TargetAddress,
        string SourceAddress,
        CaptureDeviceProxyState Configuration,
        CaptureDeviceEndpoint Endpoint,
        bool EndpointBelongsToCapture,
        bool MayRestartConsole,
        bool RequiresManualSetup,
        bool StartsListener,
        ushort Port,
        ushort PendingPort);

    public sealed record CaptureProxyResult(
        Guid CaptureId,
        CaptureProxyAction Action,
        bool Changed,
        bool IsRunning,
        ushort Port,
        ushort PendingPort,
        long Revision,
        long CaptureEpoch,
        bool ConfigurationConfirmed,
        bool ConfigurationWriteRequested,
        bool PortCommitted,
        bool Persisted,
        bool RequiresManualSetup,
        CaptureProxyPhase Phase,
        CaptureProxyFailure Failure)
    {
        public bool Succeeded => Failure == CaptureProxyFailure.None;
    }

    public sealed class CaptureProxyException(CaptureProxyFailure failure)
        : InvalidOperationException(failure.ToString())
    {
        public CaptureProxyFailure Failure { get; } = failure;
    }

    public interface ICaptureProxyOperations
    {
        Task<IReadOnlyList<string>> GetSourceAddressesAsync(
            Guid captureId,
            CancellationToken cancellationToken = default);

        Task<CaptureProxyPreflight> PrepareAsync(
            Guid captureId,
            long expectedRevision,
            CaptureProxyAction action,
            string sourceAddress = null,
            CancellationToken cancellationToken = default);

        Task<CaptureProxyResult> ExecuteAsync(
            CaptureProxyPreflight preflight,
            CancellationToken cancellationToken = default);
    }
}
