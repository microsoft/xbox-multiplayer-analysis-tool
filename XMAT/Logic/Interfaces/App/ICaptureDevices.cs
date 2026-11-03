// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum CaptureDeviceKind
    {
        LocalPC,
        XboxConsole,
        DefaultXboxConsole,
        GenericProxyDevice,
        WindowsHandheldOrRemoteDevice
    }

    public enum CaptureDeviceFailure
    {
        None,
        InvalidRequest,
        TargetNotFound,
        SourceChanged,
        CaptureBusy,
        ReadOnlyCapture,
        UnsupportedCapture,
        NotInitialized,
        GdkRequired,
        NoDefaultConsole,
        RecoveryRequired,
        ProxyStateUnknown,
        ProxyEndpointInUse,
        PersistenceFailed,
        Cancelled
    }

    public enum CaptureDeviceProxyState
    {
        NotApplicable,
        NotInspected,
        Manual,
        Disabled,
        Enabled,
        Unreachable,
        Unknown,
        GdkRequired
    }

    public sealed record CaptureDeviceRequest(
        CaptureDeviceKind Kind,
        CaptureType CaptureType,
        string Address = null);

    public sealed record CaptureDeviceCreation(
        CaptureDeviceRequest Request,
        DeviceType DeviceType,
        string DeviceName,
        Guid? ExistingCaptureId);

    public sealed record CaptureDeviceEndpoint(string Host, ushort Port);

    public sealed record CaptureDeviceState(
        CaptureDescriptor Capture,
        bool IsTransitioning,
        ushort? PendingProxyPort,
        string ConfiguredProxyHost,
        bool IsProxyPortReserved,
        bool IsPendingProxyPortReserved,
        CaptureDeviceProxyState ProxyState,
        CaptureDeviceEndpoint Endpoint,
        bool EndpointBelongsToCapture,
        CaptureDeviceFailure RemovalFailure,
        CaptureDeviceFailure PendingPortFailure);

    public sealed record CaptureDeviceChange(
        Guid CaptureId,
        long Revision,
        long CaptureEpoch,
        bool Changed,
        bool Persisted,
        bool Removed = false,
        CaptureDeviceFailure Failure = CaptureDeviceFailure.None)
    {
        public bool Succeeded => Failure == CaptureDeviceFailure.None;
    }

    public sealed class CaptureDeviceException(CaptureDeviceFailure failure)
        : InvalidOperationException(failure.ToString())
    {
        public CaptureDeviceFailure Failure { get; } = failure;
    }

    public interface ICaptureDevices
    {
        Task<CaptureDeviceCreation> PrepareAddAsync(
            CaptureDeviceRequest request,
            CancellationToken cancellationToken = default);

        Task<CaptureDeviceChange> AddAsync(
            CaptureDeviceCreation creation,
            CancellationToken cancellationToken = default);

        Task<CaptureDeviceState> GetDeviceAsync(
            Guid captureId,
            bool inspectProxy = false,
            CancellationToken cancellationToken = default);

        Task<CaptureDeviceState> PrepareRemoveAsync(
            Guid captureId,
            long expectedRevision,
            CancellationToken cancellationToken = default);

        Task<CaptureDeviceChange> RemoveAsync(
            Guid captureId,
            long expectedRevision,
            CancellationToken cancellationToken = default);

        Task<CaptureDeviceChange> SetPendingProxyPortAsync(
            Guid captureId,
            long expectedRevision,
            ushort? port,
            CancellationToken cancellationToken = default);
    }
}
