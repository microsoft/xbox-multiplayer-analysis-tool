// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
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
            long records = 0;
            NetworkShapingMetadata metadata = null;
            try
            {
                if (request == null || request.Port is < 1 or > 65535 ||
                    request.DurationSeconds is < 1 or > 86400 ||
                    !string.Equals(Path.GetExtension(request.OutputPath), ".xmatcap", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Invalid capture request.");
                }
                ArgumentNullException.ThrowIfNull(request.Shaping);
                request.Shaping.Validate();
                if (request.Shaping.Schedule != null)
                {
                    foreach (ScheduledShapingChange change in request.Shaping.Schedule)
                    {
                        if (change.AfterSeconds >= request.DurationSeconds)
                        {
                            throw new ArgumentException("The schedule extends past the capture duration.");
                        }
                    }
                }
                var diagnostics = new List<OperationDiagnostic>();
                var publisher = new CaptureExportPublisher();
                PublishedCapture publication = await publisher.PublishAsync(
                    request.OutputPath, false, false, _ => { },
                    async (staged, token) =>
                    {
                        using X509Certificate2 authority = LoadAuthority(request.RootPfx);
                        using var certificates = new CertificateManager(authority);
                        var proxy = new WebServiceProxy(certificates, applicationDiagnostics: false);
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
                        diagnostics.AddRange(initial.Diagnostics);
                        using var recorder = new ProxyCaptureRecorder(staged);
                        recorder.Attach(proxy);
                        string log = Path.Join(Path.GetDirectoryName(staged), "capture.log");
                        using var running = CancellationTokenSource.CreateLinkedTokenSource(token);
                        Task schedule = Task.CompletedTask;
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
            catch (CaptureExportDestinationException)
            {
                return Failure(OperationOutcome.Conflict, "capture.output.conflict", "The output path cannot be used.");
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                return Failure(OperationOutcome.InvalidInput, "capture.options.invalid", "Capture options or the supplied CA certificate are invalid.");
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return Failure(OperationOutcome.Failed, "capture.failed", "The capture failed; no output was published.");
            }
        }

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
