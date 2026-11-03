// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using XMAT.WebServiceCapture.Models;

namespace XMAT.SharedInterfaces
{
    public enum CaptureOperationFailure
    {
        None,
        TargetNotFound,
        ReadOnlyCapture,
        CaptureBusy,
        PendingPortChange,
        GdkRequired,
        UnsupportedCapture,
        ElevationRequired,
        RecoveryRequired,
        NotInitialized
    }

    public enum CaptureTraceRecoveryPhase { None, Stop, Download, Publish, Disconnect }

    public sealed record CaptureOperationResult(
        bool Changed,
        bool IsRunning,
        CaptureOperationFailure Failure,
        long? CaptureEpoch = null)
    {
        public bool Succeeded => Failure == CaptureOperationFailure.None;
    }

    public sealed record CaptureTransitionFailure(bool IsRunning, long CaptureEpoch)
    {
        private static readonly object MetadataKey = new();

        internal static void Attach(Exception exception, CaptureTransitionFailure failure) =>
            exception.Data[MetadataKey] = failure;

        public static CaptureTransitionFailure Get(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return exception.Data[MetadataKey] as CaptureTransitionFailure;
        }
    }

    public interface ICaptureOperations
    {
        Task<CaptureOperationFailure> GetAvailabilityAsync(
            ICaptureDeviceContext target,
            bool running,
            CancellationToken cancellationToken = default);

        Task<CaptureOperationResult> SetRunningAsync(
            ICaptureDeviceContext target,
            bool running,
            IProgress<WebProxyStopProgress> progress = null,
            CancellationToken cancellationToken = default);
    }

    public sealed record CaptureRecoveryResult(bool Acknowledged, bool TraceStoppedByApp, bool CapturedDataRetained, long? CaptureEpoch);

    public interface ICaptureRecoveryOperations
    {
        Task<CaptureRecoveryResult> AcknowledgeExternalTraceRecoveryAsync(
            ICaptureDeviceContext target, CancellationToken cancellationToken = default);
    }
}
