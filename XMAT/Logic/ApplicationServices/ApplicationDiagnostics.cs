// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.ApplicationServices
{
    internal sealed class ApplicationDiagnostics : IApplicationDiagnostics
    {
        private const int MaximumLogFiles = 64;
        private const int MaximumCertificateBytes = 1024 * 1024;
        private readonly AdministrationArtifacts _artifacts;
        private readonly Func<ApplicationDiagnosticPrerequisites> _prerequisites;
        private readonly Func<byte[]> _publicCertificate;
        private readonly Func<string> _logDirectory;
        private readonly object _drainGate = new();
        private TaskCompletionSource _drained;
        private int _pendingExports;

        internal ApplicationDiagnostics(
            ICaptureFileExports folders, Func<ApplicationDiagnosticPrerequisites> prerequisites = null,
            Func<byte[]> publicCertificate = null, Func<string> logDirectory = null)
        {
            _artifacts = new(folders);
            _prerequisites = prerequisites ?? ReadPrerequisites;
            _publicCertificate = publicCertificate ?? ProxyPublicRootCertificate.Read;
            _logDirectory = logDirectory ?? (() => Path.Combine(PublicUtilities.StorageDirectoryPath, "Logs"));
        }

        public Task<ApplicationDiagnosticSnapshot> InspectAsync(CancellationToken cancellationToken = default) =>
            Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Inspect().Snapshot;
            }, cancellationToken);

        public async Task<AdministrationExportPlan> PrepareExportAsync(
            string expectedRevision, AdministrationArtifactKind kind, AdministrationDestination destination,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(expectedRevision))
                throw new AdministrationException(AdministrationFailure.InvalidRequest);
            var snapshot = await InspectAsync(cancellationToken);
            if (snapshot.Revision != expectedRevision)
                throw new AdministrationException(AdministrationFailure.SourceChanged);
            if (kind == AdministrationArtifactKind.DiagnosticLogs ? !snapshot.LogsAvailable : !snapshot.PublicRootCertificate.Available)
                throw new AdministrationException(AdministrationFailure.PrerequisiteRequired);
            await _artifacts.ValidateAsync(destination, kind, cancellationToken);
            return new(expectedRevision, kind, destination,
                kind == AdministrationArtifactKind.DiagnosticLogs ? null : snapshot.PublicRootCertificate.Sha256);
        }

        public async Task<AdministrationExportReceipt> ExportAsync(
            AdministrationExportPlan plan, long maximumBytes, TimeSpan timeout,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default)
        {
            lock (_drainGate)
            {
                if (_pendingExports++ == 0)
                    _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                return await ExportCoreAsync(plan, maximumBytes, timeout, beforePublish, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_drainGate)
                {
                    if (--_pendingExports == 0)
                        _drained.TrySetResult();
                }
            }
        }

        public Task DrainAsync()
        {
            lock (_drainGate)
            { return _pendingExports == 0 ? Task.CompletedTask : _drained.Task; }
        }

        private async Task<AdministrationExportReceipt> ExportCoreAsync(
            AdministrationExportPlan plan, long maximumBytes, TimeSpan timeout,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken)
        {
            if (plan == null || maximumBytes <= 0 || timeout <= TimeSpan.Zero ||
                timeout.TotalMilliseconds > uint.MaxValue - 1L)
                throw new AdministrationException(AdministrationFailure.InvalidRequest);
            ArgumentNullException.ThrowIfNull(beforePublish);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                return await Task.Run(async () =>
                {
                    var token = deadline.Token;
                    token.ThrowIfCancellationRequested();
                    var current = Inspect();
                    DemandCurrent(plan, current.Snapshot);
                    var files = 1;
                    var length = await _artifacts.PublishAsync(plan.Destination, plan.Kind, maximumBytes, async (output, innerToken) =>
                    {
                        if (plan.Kind == AdministrationArtifactKind.DiagnosticLogs)
                            files = await WriteLogsAsync(current, output, maximumBytes, innerToken);
                        else
                        {
                            if (current.Certificate == null)
                                throw new AdministrationException(AdministrationFailure.PrerequisiteRequired);
                            var bytes = plan.Kind == AdministrationArtifactKind.PublicRootCertificateDer
                                ? current.Certificate : Encoding.ASCII.GetBytes(PemEncoding.WriteString("CERTIFICATE", current.Certificate) + "\n");
                            if (bytes.LongLength > maximumBytes)
                                throw new AdministrationException(AdministrationFailure.LimitExceeded);
                            await output.WriteAsync(bytes, innerToken);
                        }
                    }, async innerToken =>
                    {
                        innerToken.ThrowIfCancellationRequested();
                        DemandCurrent(plan, Inspect().Snapshot);
                        await beforePublish(innerToken);
                    }, token);
                    return new AdministrationExportReceipt(
                        plan.Kind, plan.Destination.FolderId, plan.Destination.FileName, length, files, true,
                        plan.Kind != AdministrationArtifactKind.DiagnosticLogs);
                }, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new AdministrationException(AdministrationFailure.Timeout);
            }
        }

        private State Inspect()
        {
            var prerequisites = _prerequisites();
            var certificate = _publicCertificate();
            if (certificate is { Length: > MaximumCertificateBytes })
                throw new AdministrationException(AdministrationFailure.LimitExceeded);
            if (certificate is { Length: > 0 })
            {
                // This loader accepts a public X.509 certificate, not a PFX/PKCS#12 key container.
                using var publicOnly = X509CertificateLoader.LoadCertificate(certificate);
                certificate = publicOnly.Export(X509ContentType.Cert);
            }
            else
                certificate = null;
            var certificateDescriptor = new PublicRootCertificateDescriptor(certificate != null,
                certificate == null ? null : Convert.ToHexString(SHA256.HashData(certificate)),
                certificate?.Length ?? 0, certificate == null ? "DesktopProxyInitializationRequired" : null);
            var logsPath = _logDirectory();
            WindowsExportFolder.FileIdentity? logsIdentity = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(logsPath))
                {
                    using var directory = new WindowsExportFolder(logsPath);
                    logsIdentity = directory.Identity;
                }
            }
            catch (Exception exception) when (exception is CaptureFileException or IOException or UnauthorizedAccessException)
            {
                // Availability exposes no private path or operating-system exception text.
            }
            var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { prerequisites, certificateDescriptor, logsIdentity })));
            return new(new(revision, prerequisites, certificateDescriptor, logsIdentity.HasValue),
                certificate, logsPath, logsIdentity);
        }

        private static void DemandCurrent(AdministrationExportPlan plan, ApplicationDiagnosticSnapshot current)
        {
            if (!Enum.IsDefined(plan.Kind) || plan.ExpectedRevision != current.Revision ||
                (plan.Kind != AdministrationArtifactKind.DiagnosticLogs && plan.CertificateSha256 != current.PublicRootCertificate.Sha256))
                throw new AdministrationException(AdministrationFailure.SourceChanged);
        }

        private static async Task<int> WriteLogsAsync(State state, Stream output, long maximumBytes, CancellationToken cancellationToken)
        {
            using var directory = new WindowsExportFolder(state.LogsPath);
            if (directory.Identity != state.LogsIdentity)
                throw new AdministrationException(AdministrationFailure.SourceChanged);
            var names = Directory.EnumerateFiles(directory.Path, "*.log", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).Take(MaximumLogFiles + 1).ToArray();
            if (names.Length > MaximumLogFiles)
                throw new AdministrationException(AdministrationFailure.LimitExceeded);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(state.Snapshot);
            long totalInput = metadata.Length;
            if (totalInput > maximumBytes)
                throw new AdministrationException(AdministrationFailure.LimitExceeded);
            await using (var manifest = zip.CreateEntry("diagnostics.json", CompressionLevel.Fastest).Open())
                await manifest.WriteAsync(metadata, cancellationToken);
            var buffer = new byte[81920];
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var input = WindowsDiagnosticLog.Open(directory, name);
                var remaining = input.Length;
                if (remaining > maximumBytes - totalInput)
                    throw new AdministrationException(AdministrationFailure.LimitExceeded);
                totalInput += remaining;
                await using var entry = zip.CreateEntry("logs/" + name, CompressionLevel.Fastest).Open();
                while (remaining > 0)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                    if (count == 0)
                        throw new AdministrationException(AdministrationFailure.SourceChanged);
                    await entry.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    remaining -= count;
                }
            }
            return names.Length;
        }

        private static ApplicationDiagnosticPrerequisites ReadPrerequisites()
        {
            var gdkPath = GDKXHelper.GetGDKXPath(out var gdkVersion);
            using var identity = WindowsIdentity.GetCurrent();
            var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return new(typeof(ApplicationDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown",
                Environment.Version.ToString(), Environment.OSVersion.Version.ToString(), elevated,
                !string.IsNullOrEmpty(gdkPath), Version.TryParse(gdkVersion, out var version) ? version.ToString() : null);
        }

        private sealed record State(
            ApplicationDiagnosticSnapshot Snapshot, byte[] Certificate, string LogsPath,
            WindowsExportFolder.FileIdentity? LogsIdentity);
    }
}
