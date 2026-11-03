// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum AdministrationFailure
    {
        InvalidRequest,
        SourceChanged,
        TargetNotFound,
        Unavailable,
        PersistenceFailed,
        PrerequisiteRequired,
        LimitExceeded,
        Timeout
    }

    public sealed class AdministrationException(AdministrationFailure failure)
        : InvalidOperationException(failure.ToString())
    {
        public AdministrationFailure Failure { get; } = failure;
    }

    public enum HostPolicyList { Block, Bypass }
    public enum HostPolicyAction { Add, Remove, Clear, TakeOver }
    public enum HostPolicyOwnership { Desktop, ThisSession, AnotherSession }

    public sealed record HostPolicyEntryDescriptor(
        Guid Id, HostPolicyList List, string Pattern, long Epoch, HostPolicyOwnership Ownership);
    public sealed record HostPolicySnapshot(
        long Revision, IReadOnlyList<HostPolicyEntryDescriptor> Entries,
        bool SessionOnly = true, bool RetainedOnDisconnect = true);
    public sealed record HostPolicyRequest(
        long ExpectedRevision, HostPolicyList List, HostPolicyAction Action,
        string Pattern = null, Guid? EntryId = null, long? ExpectedEntryEpoch = null);
    public sealed record HostPolicyPlan(
        HostPolicyRequest Request, HostPolicyEntryDescriptor Entry, bool ChangesState, int AffectedEntries);
    public sealed record HostPolicyChange(
        long Revision, bool Changed, HostPolicyEntryDescriptor Entry, bool RetainedOnDisconnect = true, int AffectedEntries = 0);

    public interface IHostPolicyOperations
    {
        Task<HostPolicySnapshot> InspectAsync(Guid? sessionId = null, CancellationToken cancellationToken = default);
        Task<HostPolicyPlan> PrepareAsync(
            HostPolicyRequest request, Guid? sessionId = null, CancellationToken cancellationToken = default);
        Task<HostPolicyChange> ExecuteAsync(
            HostPolicyPlan plan, Guid? sessionId = null, CancellationToken cancellationToken = default);
    }

    public sealed record ApplicationPreferenceValues(
        int FirstProxyPort, int LastProxyPort, bool PromptToDisableProxyOnClose,
        int PacketsPerSecondThreshold, int MaximumMtu, int DuplicatePacketWindowMilliseconds,
        string Language, string Theme);
    public sealed record ApplicationPreferenceOptions(
        IReadOnlyList<string> Languages, IReadOnlyList<string> Themes,
        int MinimumPort = 1, int MaximumPort = 65535, int MinimumPacketsPerSecondThreshold = 0,
        int MaximumPacketsPerSecondThreshold = int.MaxValue, int MinimumMtu = 1, int MaximumMtu = 65535,
        int MinimumDuplicateWindowMilliseconds = 0, int MaximumDuplicateWindowMilliseconds = int.MaxValue);
    public sealed record ApplicationPreferenceSnapshot(
        long Revision, ApplicationPreferenceValues Values, ApplicationPreferenceOptions Options,
        IReadOnlyList<int> ReservedProxyPorts, bool ChangeInProgress);
    public sealed record ApplicationPreferenceDifference(string Field, string Before, string After);
    public sealed record ApplicationPreferencePlan(
        long ExpectedRevision, ApplicationPreferenceValues Before, ApplicationPreferenceValues After,
        IReadOnlyList<ApplicationPreferenceDifference> Differences, bool LanguageRestartRequired);
    public sealed record ApplicationPreferenceChange(
        long Revision, bool Changed, bool Committed, bool Persisted, bool LanguageRestartRequired,
        AdministrationFailure? Failure = null);

    public interface IApplicationPreferences
    {
        Task<ApplicationPreferenceSnapshot> InspectAsync(CancellationToken cancellationToken = default);
        Task<ApplicationPreferencePlan> PrepareAsync(
            long expectedRevision, ApplicationPreferenceValues values, CancellationToken cancellationToken = default);
        Task<ApplicationPreferenceChange> ApplyAsync(
            long expectedRevision, ApplicationPreferenceValues values, CancellationToken cancellationToken = default);
        Task<ApplicationPreferenceChange> ExecuteAsync(
            ApplicationPreferencePlan plan, CancellationToken cancellationToken = default);
        Task DrainAsync();
    }

    public enum AdministrationArtifactKind { DiagnosticLogs, PublicRootCertificateDer, PublicRootCertificatePem }
    public sealed record AdministrationDestination(Guid FolderId, string FileName);
    public sealed record ApplicationDiagnosticPrerequisites(
        string ApplicationVersion, string RuntimeVersion, string OperatingSystemVersion,
        bool Elevated, bool GdkAvailable, string GdkVersion, bool LocalDesktop = true);
    public sealed record PublicRootCertificateDescriptor(
        bool Available, string Sha256, int DerBytes, string Prerequisite);
    public sealed record ApplicationDiagnosticSnapshot(
        string Revision, ApplicationDiagnosticPrerequisites Prerequisites,
        PublicRootCertificateDescriptor PublicRootCertificate, bool LogsAvailable,
        bool LogsMayContainSecrets = true, bool LogsMayChangeDuringCollection = true);
    public sealed record AdministrationExportPlan(
        string ExpectedRevision, AdministrationArtifactKind Kind, AdministrationDestination Destination,
        string CertificateSha256);
    public sealed record AdministrationExportReceipt(
        AdministrationArtifactKind Kind, Guid FolderId, string FileName, long Length,
        int Files, bool Published, bool PublicCertificateOnly);

    public interface IApplicationDiagnostics
    {
        Task<ApplicationDiagnosticSnapshot> InspectAsync(CancellationToken cancellationToken = default);
        Task<AdministrationExportPlan> PrepareExportAsync(
            string expectedRevision, AdministrationArtifactKind kind, AdministrationDestination destination,
            CancellationToken cancellationToken = default);
        Task<AdministrationExportReceipt> ExportAsync(
            AdministrationExportPlan plan, long maximumBytes, TimeSpan timeout,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default);
        Task DrainAsync();
    }

    public enum DesktopNavigationKind { Capture, Record, Report }
    public sealed record DesktopTargetProvenance(
        Guid? CaptureId = null, long? CaptureRevision = null, long? CaptureEpoch = null,
        Guid? SourceId = null, Guid? SnapshotId = null, long? RecordId = null, Guid? ReportId = null);
    public sealed record DesktopNavigationTarget(
        Guid Id, DesktopNavigationKind Kind, long Revision, long Epoch, DesktopTargetProvenance Provenance,
        [property: JsonIgnore] Guid? OwnerSessionId = null);
    public sealed record DesktopNavigationRequest(Guid TargetId, long ExpectedRevision, long ExpectedEpoch);
    public sealed record DesktopNavigationReceipt(Guid TargetId, DesktopNavigationKind Kind, bool Revealed);
    public enum DesktopLifecycleAction { Close, RequestElevatedRestart }
    public sealed record DesktopLifecycleSnapshot(
        long Revision, bool CanRequestClose, bool CanRequestElevatedRestart,
        bool NormalCloseChecksRemainAuthoritative = true, bool RestartSuccessIsNotConfirmed = true);
    public sealed record DesktopLifecyclePlan(long ExpectedRevision, DesktopLifecycleAction Action);
    public sealed record DesktopLifecycleReceipt(
        DesktopLifecycleAction Action, bool ScheduledAfterOperation,
        bool NormalCloseChecksRemainAuthoritative = true, bool RestartSuccessIsNotConfirmed = true);

    public interface IDesktopActions
    {
        Task<IReadOnlyList<DesktopNavigationTarget>> GetNavigationTargetsAsync(CancellationToken cancellationToken = default);
        Task<DesktopNavigationTarget> PrepareNavigationAsync(
            DesktopNavigationRequest request, CancellationToken cancellationToken = default);
        Task<DesktopNavigationReceipt> RevealAsync(
            DesktopNavigationRequest request, CancellationToken cancellationToken = default);
        Task<DesktopLifecycleSnapshot> InspectLifecycleAsync(CancellationToken cancellationToken = default);
        Task<DesktopLifecyclePlan> PrepareLifecycleAsync(
            long expectedRevision, DesktopLifecycleAction action, CancellationToken cancellationToken = default);
        Task RequestLifecycleAsync(
            DesktopLifecyclePlan plan, Func<bool> stillAuthorized, CancellationToken cancellationToken = default);
    }
}
