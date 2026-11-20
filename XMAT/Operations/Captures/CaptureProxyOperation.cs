// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Export;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Operations.Captures
{
    internal sealed record CaptureProxyRequest(
        int Port, int DurationSeconds, string OutputPath, NetworkShapingPlan Shaping,
        string RootPfx = null, IPAddress ListenAddress = null);

    internal sealed record CaptureProxyResult(long Records, PublishedCapture Publication, NetworkShapingMetadata Shaping);

    internal sealed class CaptureProxyOperation : IOperation<CaptureProxyRequest, CaptureProxyResult>
    {
        public async Task<OperationResult<CaptureProxyResult>> ExecuteAsync(
            CaptureProxyRequest request, OperationContext context, CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            try
            {
                // Everything a user can fix is checked before any listener or staging file exists.
                OperationResult<CaptureProxyResult> invalid = Validate(request);
                if (invalid != null)
                {
                    return invalid;
                }
                X509Certificate2 authority;
                try
                {
                    authority = LoadAuthority(request.RootPfx);
                }
                catch (Exception ex) when (request.RootPfx != null && IsUnusableAuthority(ex))
                {
                    return InvalidAuthority();
                }
                using (authority)
                {
                    CertificateManager certificates;
                    try
                    {
                        certificates = new CertificateManager(authority);
                    }
                    catch (ArgumentException)
                    {
                        return InvalidAuthority();
                    }
                    using (certificates)
                    {
                        return await CaptureAsync(request, certificates, context, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return Failure(OperationOutcome.Cancelled, "capture.cancelled", "Capture was cancelled; no output was published.");
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return Failure(OperationOutcome.Failed, "capture.failed", "The capture failed; no output was published.");
            }
        }

        private static OperationResult<CaptureProxyResult> Validate(CaptureProxyRequest request)
        {
            if (request == null || request.Port is < 1 or > 65535 ||
                request.DurationSeconds is < 1 or > 86400 ||
                !string.Equals(Path.GetExtension(request.OutputPath), ".xmatcap", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(OperationOutcome.InvalidInput, "capture.options.invalid", "Capture options are invalid.");
            }
            try
            {
                ArgumentNullException.ThrowIfNull(request.Shaping);
                request.Shaping.Validate();
            }
            catch (ArgumentException)
            {
                return Failure(OperationOutcome.InvalidInput, "capture.profile.invalid", "The shaping settings are invalid.");
            }
            if (request.Shaping.Schedule?.Any(change => change.AfterSeconds >= request.DurationSeconds) == true)
            {
                return Failure(
                    OperationOutcome.InvalidInput, "capture.profile.invalid",
                    "Scheduled shaping changes must occur before the capture duration ends.");
            }
            string parent;
            try
            {
                parent = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
            {
                return Failure(OperationOutcome.InvalidInput, "capture.options.invalid", "Capture options are invalid.");
            }
            return string.IsNullOrEmpty(parent) || !Directory.Exists(parent)
                ? Failure(
                    OperationOutcome.InvalidInput, "capture.output.parent-missing",
                    "The folder that should contain the output file does not exist.")
                : null;
        }

        private static async Task<OperationResult<CaptureProxyResult>> CaptureAsync(
            CaptureProxyRequest request, CertificateManager certificates,
            OperationContext context, CancellationToken cancellationToken)
        {
            long records = 0;
            NetworkShapingMetadata metadata = null;
            bool listenUnavailable = false;
            try
            {
                var diagnostics = new List<OperationDiagnostic>();
                var publisher = new CaptureExportPublisher();
                PublishedCapture publication = await publisher.PublishAsync(
                    request.OutputPath, false, false, _ => { },
                    async (staged, token) =>
                    {
                        var proxy = new WebServiceProxy(certificates, applicationDiagnostics: false)
                        {
                            ResponseStreaming = new ResponseStreamingOptions(),
                        };
                        if (request.RootPfx == null)
                        {
                            diagnostics.Add(new OperationDiagnostic(
                                "capture.tls.opaque", OperationDiagnosticSeverity.Warning,
                                "TLS tunnels are shaped but not decrypted. Supply an explicit CA PFX for HTTPS inspection; no trust store is changed."));
                            proxy.BypassList = new BypassListModel();
                            if (!proxy.BypassList.Add("*"))
                            {
                                throw new InvalidOperationException("Unable to configure opaque TLS.");
                            }
                        }
                        var shaping = new NetworkShapingController();
                        var setter = new SetNetworkShapingOperation();
                        var initial = await setter.ExecuteAsync(
                            new(shaping, request.Shaping.Initial, request.Shaping.BodyTimeoutSeconds),
                            context, token).ConfigureAwait(false);
                        if (!initial.Succeeded)
                        {
                            throw new ArgumentException("Invalid initial shaping profile.");
                        }
                        // The recorded history starts at the requested profile, not the controller's initial Off.
                        shaping.ResetHistory();
                        diagnostics.AddRange(initial.Diagnostics);
                        using var recorder = new ProxyCaptureRecorder(staged);
                        recorder.Attach(proxy);
                        string log = Path.Join(Path.GetDirectoryName(staged), "capture.log");
                        using var running = CancellationTokenSource.CreateLinkedTokenSource(token);
                        Task schedule = Task.CompletedTask;
                        try
                        {
                            try
                            {
                                await proxy.StartProxyAsync(new WebServiceProxyOptions
                                {
                                    Port = request.Port,
                                    ListenAddress = request.ListenAddress ?? IPAddress.Loopback,
                                    LogFilePath = log,
                                    CurrentLogLevel = LogLevel.NONE,
                                    Shaping = shaping,
                                    BodyTimeoutSeconds = request.Shaping.BodyTimeoutSeconds,
                                }, token).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (IsListenUnavailable(ex))
                            {
                                listenUnavailable = true;
                                throw;
                            }
                            context.Progress.Report(new OperationProgress("capture.listening", request.Port));
                            schedule = ScheduleAsync(shaping, request.Shaping, diagnostics, running.Token);
                            Task duration = Task.Delay(TimeSpan.FromSeconds(request.DurationSeconds), token);
                            Task finished = await Task.WhenAny(duration, recorder.Failure.Task, schedule).ConfigureAwait(false);
                            await finished.ConfigureAwait(false);
                            if (finished == recorder.Failure.Task)
                            {
                                throw new InvalidOperationException("Capture recording failed or reached its safety limit.");
                            }
                        }
                        finally
                        {
                            await running.CancelAsync().ConfigureAwait(false);
                            try
                            {
                                try
                                {
                                    await schedule.ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (running.IsCancellationRequested)
                                {
                                    Trace.TraceInformation("The shaping schedule stopped with the capture.");
                                }
                            }
                            finally
                            {
                                await proxy.StopProxyAsync().ConfigureAwait(false);
                                File.Delete(log);
                            }
                        }
                        token.ThrowIfCancellationRequested();
                        if (recorder.Failure.Task.IsCompleted)
                        {
                            throw new InvalidOperationException("Capture recording failed.");
                        }
                        metadata = NetworkShapingMetadata.From(shaping, request.Shaping.BodyTimeoutSeconds);
                        recorder.RecordShaping(metadata);
                        records = recorder.Records;
                    }, cancellationToken).ConfigureAwait(false);
                return OperationResult<CaptureProxyResult>.Success(new(records, publication, metadata), diagnostics);
            }
            catch (OperationCanceledException)
            {
                return Failure(OperationOutcome.Cancelled, "capture.cancelled", "Capture was cancelled; no output was published.");
            }
            catch (CaptureExportCleanupException ex)
            {
                return new OperationResult<CaptureProxyResult>(
                    OperationOutcome.CleanupFailed,
                    ex.Publication == null ? null : new(records, ex.Publication, metadata),
                    new[] { new OperationDiagnostic(
                        "capture.cleanup.failed", OperationDiagnosticSeverity.Error, "Capture cleanup failed.") });
            }
            catch (CaptureExportDestinationException ex)
            {
                return ex.Code switch
                {
                    "export.output.exists" => Failure(
                        OperationOutcome.Conflict, "capture.output.exists",
                        "The output file already exists; capture never overwrites output."),
                    "export.output.locked" => Failure(
                        OperationOutcome.Conflict, "capture.output.locked", "The output file is locked by another program."),
                    _ => Failure(OperationOutcome.Conflict, "capture.output.conflict", "The output path cannot be used."),
                };
            }
            catch (CaptureSaveException ex) when (ex.Failure == CaptureSaveFailure.Refused)
            {
                return Failure(OperationOutcome.Conflict, "capture.output.conflict", "The output path cannot be used.");
            }
            catch (CaptureExportException ex) when (ex.Code == CaptureExportErrorCodes.OutputUnprotected)
            {
                return Failure(
                    OperationOutcome.Failed, "capture.output.unprotected",
                    "The output folder cannot keep the capture private; use storage with Windows permissions, such as NTFS.");
            }
            catch (Exception) when (listenUnavailable)
            {
                return Failure(
                    OperationOutcome.Conflict, "capture.listen.unavailable",
                    "The listening address or port could not be opened. It may be in use, reserved, or not assigned to this PC.");
            }
            catch (ArgumentException)
            {
                return Failure(OperationOutcome.InvalidInput, "capture.options.invalid", "Capture options are invalid.");
            }
        }

        private static bool IsListenUnavailable(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is AddressInUseException ||
                    current is SocketException
                    {
                        SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AddressNotAvailable or SocketError.AccessDenied,
                    })
                {
                    return true;
                }
                if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsListenUnavailable))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsUnusableAuthority(Exception exception) =>
            exception is IOException or UnauthorizedAccessException or CryptographicException or
                ArgumentException or NotSupportedException or SecurityException;

        private static OperationResult<CaptureProxyResult> InvalidAuthority() =>
            Failure(
                OperationOutcome.InvalidInput, "capture.ca.invalid",
                "The CA certificate could not be read or is not a currently valid, passwordless RSA or ECDSA CA PFX with its private key.");

        private static async Task ScheduleAsync(
            NetworkShapingController controller, NetworkShapingPlan plan,
            List<OperationDiagnostic> diagnostics, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            foreach (ScheduledShapingChange change in plan.Schedule ?? Array.Empty<ScheduledShapingChange>())
            {
                double milliseconds = change.AfterSeconds * 1000.0 - clock.Elapsed.TotalMilliseconds;
                if (milliseconds > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var result = await new SetNetworkShapingOperation().ExecuteAsync(
                    new(controller, change.Profile, plan.BodyTimeoutSeconds),
                    OperationContext.None, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException("The scheduled shaping change failed.");
                }
                foreach (OperationDiagnostic diagnostic in result.Diagnostics.Where(
                    diagnostic => !diagnostics.Exists(existing => existing.Code == diagnostic.Code)))
                {
                    diagnostics.Add(diagnostic);
                }
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        private static X509Certificate2 LoadAuthority(string path)
        {
            if (path != null)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 8 * 1024 * 1024)
                {
                    throw new ArgumentException("The CA file exceeds its size limit.");
                }
                var bytes = new byte[(int)stream.Length];
                try
                {
                    stream.ReadExactly(bytes);
                    return X509CertificateLoader.LoadPkcs12(
                        bytes, null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=XMAT temporary proxy CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        private static OperationResult<CaptureProxyResult> Failure(OperationOutcome outcome, string code, string message) =>
            OperationResult<CaptureProxyResult>.Failure(
                outcome, new OperationDiagnostic(code, OperationDiagnosticSeverity.Error, message));
    }
}
