// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ApplicationDiagnosticsTests(CaptureUiApplicationFixture application)
    {
        private static ApplicationDiagnosticPrerequisites Prerequisites() =>
            new("1.2.3", "10.0", "10.0.26100", false, false, null);

        [Fact]
        public void MissingCertificateIsAnExplicitPrerequisiteWithoutGeneratingOrReadingAStore()
        {
            application.Run(_ =>
            {
                var reads = 0;
                var diagnostics = new ApplicationDiagnostics(new CaptureFileExports(), Prerequisites,
                    () => { reads++; return null; }, () => null);
                var snapshot = Complete(diagnostics.InspectAsync());
                Assert.False(snapshot.PublicRootCertificate.Available);
                Assert.Equal("DesktopProxyInitializationRequired", snapshot.PublicRootCertificate.Prerequisite);
                Assert.False(snapshot.LogsAvailable);
                Assert.Equal(AdministrationFailure.PrerequisiteRequired, Assert.Throws<AdministrationException>(() =>
                    Complete(diagnostics.PrepareExportAsync(snapshot.Revision,
                        AdministrationArtifactKind.PublicRootCertificateDer, new(Guid.NewGuid(), "public.cer")))).Failure);
                Assert.Equal(2, reads);
                Assert.DoesNotContain("private", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
            });
        }

        [Theory]
        [InlineData(AdministrationArtifactKind.PublicRootCertificateDer, "root.cer")]
        [InlineData(AdministrationArtifactKind.PublicRootCertificatePem, "root.pem")]
        public void PublicCertificateExportContainsNoPrivateKeyAndCreatesOnlyApprovedNewFile(
            AdministrationArtifactKind kind, string fileName)
        {
            application.Run(_ =>
            {
                using var directory = new AdministrationTestData.DirectoryFixture();
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=ephemeral-test-public-root", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                var der = certificate.Export(X509ContentType.Cert);
                var folders = new CaptureFileExports();
                var folder = Complete(folders.RegisterFolderAsync(directory.Path));
                var diagnostics = new ApplicationDiagnostics(folders, Prerequisites, () => der, () => null);
                var snapshot = Complete(diagnostics.InspectAsync());
                var plan = Complete(diagnostics.PrepareExportAsync(snapshot.Revision, kind, new(folder.Id, fileName)));
                var receipt = Complete(diagnostics.ExportAsync(plan, 65536, TimeSpan.FromSeconds(10), _ => Task.CompletedTask));
                Assert.True(receipt.Published && receipt.PublicCertificateOnly);
                var output = File.ReadAllBytes(Path.Combine(directory.Path, fileName));
                using var loaded = kind == AdministrationArtifactKind.PublicRootCertificateDer
                    ? X509CertificateLoader.LoadCertificate(output)
                    : X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(output));
                Assert.False(loaded.HasPrivateKey);
                Assert.Equal(certificate.Thumbprint, loaded.Thumbprint);
                Assert.Single(Directory.GetFiles(directory.Path));
                Assert.Equal(CaptureFileFailure.DestinationExists, Assert.Throws<CaptureFileException>(() =>
                    Complete(diagnostics.PrepareExportAsync(snapshot.Revision, kind, new(folder.Id, fileName)))).Failure);
            });
        }

        [Fact]
        public void LogsRequireSeparateFolderGrantSensitiveApprovalAndKeepContentOutOfReceipts()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                using var logs = new AdministrationTestData.DirectoryFixture();
                using var destination = new AdministrationTestData.DirectoryFixture();
                const string secret = "Authorization: bearer private-test-log-token";
                File.WriteAllText(Path.Combine(logs.Path, "application.log"), secret);
                File.WriteAllText(Path.Combine(logs.Path, "not-a-log.txt"), "not included");
                var folder = Complete(setup.Model.CaptureFileExports.RegisterFolderAsync(destination.Path));
                var diagnostics = new ApplicationDiagnostics(setup.Model.CaptureFileExports, Prerequisites, () => null, () => logs.Path);
                var agent = new AgentAdministrationApplication(setup.Runtime, setup.Model.HostPolicyOperations,
                    setup.Preferences, diagnostics, setup.Model.DesktopActions, () => true, setup.Model.CaptureFileExports);
                try
                {
                    var snapshot = Complete(diagnostics.InspectAsync());
                    setup.Grant(AgentCapability.Diagnostics);
                    Assert.Equal(AgentAccessError.PermissionDenied, Assert.Throws<AgentAccessException>(() =>
                        Complete(agent.ExportDiagnosticsAsync(setup.Session, snapshot.Revision,
                            AdministrationArtifactKind.DiagnosticLogs, folder.Id, "logs.zip", "export", default))).Error);
                    Finish(setup.Runtime.SetPermissionsAsync(setup.Session,
                        [new(AgentCapability.Diagnostics, AgentScope.Application),
                            new(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id))]));
                    var operation = Complete(agent.ExportDiagnosticsAsync(setup.Session, snapshot.Revision,
                        AdministrationArtifactKind.DiagnosticLogs, folder.Id, "logs.zip", "export", default));
                    Assert.Equal(AgentOperationState.AwaitingApproval, setup.Runtime.GetOperation(setup.Session, operation).State);
                    Assert.True(setup.Runtime.GetOperation(setup.Session, operation).Effects.HasFlag(AgentOperationEffects.SensitiveDataExport));
                    Assert.Empty(Directory.GetFiles(destination.Path));
                    Assert.Contains("credentials", setup.Runtime.GetApprovalDetails(operation));
                    setup.Approve(operation);
                    var result = setup.Runtime.GetResult(setup.Session, operation);
                    Assert.True(result.GetProperty("published").GetBoolean());
                    Assert.DoesNotContain(secret, result.GetRawText());
                    Assert.DoesNotContain(logs.Path, result.GetRawText());
                    Assert.DoesNotContain(secret, JsonSerializer.Serialize(setup.Runtime.GetOperation(setup.Session, operation)));
                    using (var zip = ZipFile.OpenRead(Path.Combine(destination.Path, "logs.zip")))
                    {
                        Assert.Equal(2, zip.Entries.Count);
                        using var reader = new StreamReader(zip.GetEntry("logs/application.log").Open());
                        Assert.Equal(secret, reader.ReadToEnd());
                        Assert.Null(zip.GetEntry("logs/not-a-log.txt"));
                    }
                    Assert.Equal(operation, Complete(agent.ExportDiagnosticsAsync(setup.Session, snapshot.Revision,
                        AdministrationArtifactKind.DiagnosticLogs, folder.Id, "logs.zip", "export", default)));
                    Finish(setup.Runtime.EndSessionAsync(setup.Session));
                    Assert.Single(Directory.GetFiles(destination.Path));
                }
                finally { Finish(agent.DisposeAsync().AsTask()); }
            });
        }

        [Theory]
        [InlineData("..\\logs.zip")]
        [InlineData("nested\\logs.zip")]
        [InlineData("logs.zip:stream")]
        [InlineData("NUL.zip")]
        [InlineData("logs.pfx")]
        [InlineData("logs.zip.")]
        public void InvalidDestinationsCannotBecomeArbitraryFileOrKeyExports(string name)
        {
            application.Run(_ =>
            {
                using var directory = new AdministrationTestData.DirectoryFixture();
                using var logs = new AdministrationTestData.DirectoryFixture();
                File.WriteAllText(Path.Combine(logs.Path, "sample.log"), "private");
                var folders = new CaptureFileExports();
                var folder = Complete(folders.RegisterFolderAsync(directory.Path));
                var diagnostics = new ApplicationDiagnostics(folders, Prerequisites, () => null, () => logs.Path);
                var snapshot = Complete(diagnostics.InspectAsync());
                Assert.Equal(CaptureFileFailure.InvalidPath, Assert.Throws<CaptureFileException>(() =>
                    Complete(diagnostics.PrepareExportAsync(snapshot.Revision,
                        AdministrationArtifactKind.DiagnosticLogs, new(folder.Id, name)))).Failure);
                Assert.Empty(Directory.GetFiles(directory.Path));
            });
        }

        [Fact]
        public void SourceByteBudgetAppliesBeforeCompressionAndFailureLeavesNoStagingFiles()
        {
            application.Run(_ =>
            {
                using var directory = new AdministrationTestData.DirectoryFixture();
                using var logs = new AdministrationTestData.DirectoryFixture();
                File.WriteAllText(Path.Combine(logs.Path, "large.log"), new string('x', 65536));
                var folders = new CaptureFileExports();
                var folder = Complete(folders.RegisterFolderAsync(directory.Path));
                var diagnostics = new ApplicationDiagnostics(folders, Prerequisites, () => null, () => logs.Path);
                var snapshot = Complete(diagnostics.InspectAsync());
                var plan = Complete(diagnostics.PrepareExportAsync(snapshot.Revision,
                    AdministrationArtifactKind.DiagnosticLogs, new(folder.Id, "bounded.zip")));
                Assert.Equal(AdministrationFailure.LimitExceeded, Assert.Throws<AdministrationException>(() =>
                    Complete(diagnostics.ExportAsync(plan, 4096, TimeSpan.FromSeconds(10), _ => Task.CompletedTask))).Failure);
                Assert.Empty(Directory.GetFiles(directory.Path));
            });
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("deadline")]
        [InlineData("destination-race")]
        public void CancelDeadlineAndNewFileRaceDoNotPublishPartialOrOverwriteHumanData(string failure)
        {
            application.Run(_ =>
            {
                using var directory = new AdministrationTestData.DirectoryFixture();
                using var logs = new AdministrationTestData.DirectoryFixture();
                File.WriteAllText(Path.Combine(logs.Path, "sample.log"), "private test log");
                var folders = new CaptureFileExports();
                var folder = Complete(folders.RegisterFolderAsync(directory.Path));
                var diagnostics = new ApplicationDiagnostics(folders, Prerequisites, () => null, () => logs.Path);
                var snapshot = Complete(diagnostics.InspectAsync());
                var plan = Complete(diagnostics.PrepareExportAsync(snapshot.Revision,
                    AdministrationArtifactKind.DiagnosticLogs, new(folder.Id, "logs.zip")));
                using var cancel = new CancellationTokenSource();
                var exporting = diagnostics.ExportAsync(plan, 65536,
                    failure == "deadline" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10), async token =>
                    {
                        if (failure == "destination-race")
                            File.WriteAllText(Path.Combine(directory.Path, "logs.zip"), "human output");
                        else if (failure == "cancel")
                            cancel.Cancel();
                        else
                            await Task.Delay(Timeout.Infinite, token);
                    }, cancel.Token);
                if (failure == "destination-race")
                {
                    Assert.Equal(CaptureFileFailure.DestinationExists, Assert.Throws<CaptureFileException>(() => Complete(exporting)).Failure);
                    Assert.Equal("human output", File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Path))));
                }
                else
                {
                    if (failure == "deadline")
                        Assert.Equal(AdministrationFailure.Timeout, Assert.Throws<AdministrationException>(() => Complete(exporting)).Failure);
                    else
                        Assert.ThrowsAny<OperationCanceledException>(() => Complete(exporting));
                    Assert.Empty(Directory.GetFiles(directory.Path));
                }
            });
        }

        [Fact]
        public void ReplacingApprovedFolderOrCertificateInvalidatesExactPlan()
        {
            application.Run(_ =>
            {
                using var directory = new AdministrationTestData.DirectoryFixture();
                using var logs = new AdministrationTestData.DirectoryFixture();
                var folders = new CaptureFileExports();
                var folder = Complete(folders.RegisterFolderAsync(directory.Path));
                var diagnostics = new ApplicationDiagnostics(folders, Prerequisites, () => null, () => logs.Path);
                var snapshot = Complete(diagnostics.InspectAsync());
                var plan = Complete(diagnostics.PrepareExportAsync(snapshot.Revision,
                    AdministrationArtifactKind.DiagnosticLogs, new(folder.Id, "logs.zip")));
                var previous = directory.Path + ".old";
                Directory.Move(directory.Path, previous);
                Directory.CreateDirectory(directory.Path);
                try
                {
                    Assert.Equal(CaptureFileFailure.FolderChanged, Assert.Throws<CaptureFileException>(() =>
                        Complete(diagnostics.ExportAsync(plan, 65536, TimeSpan.FromSeconds(10), _ => Task.CompletedTask))).Failure);
                    Assert.Empty(Directory.GetFiles(directory.Path));
                }
                finally { Directory.Delete(previous); }
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=ephemeral-public", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                byte[] der = cert.Export(X509ContentType.Cert);
                diagnostics = new ApplicationDiagnostics(folders, Prerequisites, () => der, () => logs.Path);
                folder = Complete(folders.RegisterFolderAsync(directory.Path));
                snapshot = Complete(diagnostics.InspectAsync());
                plan = Complete(diagnostics.PrepareExportAsync(snapshot.Revision,
                    AdministrationArtifactKind.PublicRootCertificateDer, new(folder.Id, "root.cer")));
                der = null;
                Assert.Equal(AdministrationFailure.SourceChanged, Assert.Throws<AdministrationException>(() =>
                    Complete(diagnostics.ExportAsync(plan, 65536, TimeSpan.FromSeconds(10), _ => Task.CompletedTask))).Failure);
                Assert.Empty(Directory.GetFiles(directory.Path));
            });
        }

        [Theory]
        [InlineData("revoke")]
        [InlineData("cancel")]
        [InlineData("limits")]
        [InlineData("deadline")]
        public void ExportRechecksAuthorizationAndLimitsBeforePublicationAndReleasesAdmission(string change)
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                using var destination = new AdministrationTestData.DirectoryFixture();
                using var logs = new AdministrationTestData.DirectoryFixture();
                File.WriteAllText(Path.Combine(logs.Path, "test.log"), "private test content");
                var folder = Complete(setup.Model.CaptureFileExports.RegisterFolderAsync(destination.Path));
                var diagnostics = new ApplicationDiagnostics(setup.Model.CaptureFileExports, Prerequisites, () => null, () => logs.Path);
                var paused = new PausedDiagnostics(diagnostics);
                var agent = new AgentAdministrationApplication(setup.Runtime, setup.Model.HostPolicyOperations,
                    setup.Preferences, paused, setup.Model.DesktopActions, () => true);
                var diagnosticGrant = new AgentPermission(AgentCapability.Diagnostics, AgentScope.Application);
                var fileGrant = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id));
                try
                {
                    var limits = new AgentLimits(queryTimeoutSeconds: change == "deadline" ? 1 : 60, maximumQueryResults: 1);
                    Finish(setup.Runtime.SetPermissionsAsync(setup.Session, [diagnosticGrant, fileGrant], limits));
                    var snapshot = Complete(diagnostics.InspectAsync());
                    var operation = Complete(agent.ExportDiagnosticsAsync(setup.Session, snapshot.Revision,
                        AdministrationArtifactKind.DiagnosticLogs, folder.Id, "first.zip", "paused-export", default));
                    Assert.True(setup.Runtime.Approve(operation));
                    PumpUntil(() => paused.Entered.Task.IsCompleted);
                    var diagnosticsDrain = diagnostics.DrainAsync();
                    Assert.False(diagnosticsDrain.IsCompleted);
                    Assert.False(File.Exists(Path.Combine(destination.Path, "first.zip")));
                    if (change == "revoke")
                        Finish(setup.Runtime.SetPermissionsAsync(setup.Session, [diagnosticGrant], limits));
                    else if (change == "cancel")
                        Assert.True(Complete(setup.Runtime.CancelAsync(setup.Session, operation)));
                    else if (change == "limits")
                    {
                        Finish(setup.Runtime.SetPermissionsAsync(setup.Session, [diagnosticGrant, fileGrant],
                            new(maximumQueryResultBytes: 65536, maximumQueryResults: 1)));
                        paused.Continue.TrySetResult();
                    }
                    Finish(setup.Runtime.WaitForCompletionAsync(setup.Session, operation));
                    Finish(diagnosticsDrain);
                    var status = setup.Runtime.GetOperation(setup.Session, operation);
                    if (change == "limits")
                        Assert.Equal(AgentAccessError.LimitExceeded, status.Error);
                    else if (change == "deadline")
                        Assert.Equal(AgentAccessError.QueryTimeout, status.Error);
                    else
                        Assert.Equal(AgentOperationState.Cancelled, status.State);
                    Assert.Empty(Directory.GetFiles(destination.Path));
                    paused.Continue.TrySetResult();
                    Finish(setup.Runtime.SetPermissionsAsync(setup.Session, [diagnosticGrant, fileGrant],
                        new(maximumQueryResults: 1)));
                    var retry = Complete(agent.ExportDiagnosticsAsync(setup.Session, snapshot.Revision,
                        AdministrationArtifactKind.DiagnosticLogs, folder.Id, "retry.zip", "retry-export", default));
                    setup.Approve(retry);
                    Assert.True(setup.Runtime.GetResult(setup.Session, retry).GetProperty("published").GetBoolean());
                    Assert.Equal("retry.zip", Path.GetFileName(Assert.Single(Directory.GetFiles(destination.Path))));
                }
                finally
                {
                    paused.Continue.TrySetResult();
                    Finish(agent.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void APrivateKeyContainerIsNotAcceptedAsAPublicCertificate()
        {
            application.Run(_ =>
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=ephemeral-key-container", rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                var diagnostics = new ApplicationDiagnostics(new CaptureFileExports(), Prerequisites,
                    () => certificate.Export(X509ContentType.Pfx), () => null);
                Assert.Throws<CryptographicException>(() => Complete(diagnostics.InspectAsync()));
            });
        }

        [Fact]
        public void LogDirectoryHardLinksCannotTurnCollectionIntoAReadOutsideTheLogRoot()
        {
            application.Run(_ =>
            {
                using var source = new AdministrationTestData.DirectoryFixture();
                using var logs = new AdministrationTestData.DirectoryFixture();
                using var destination = new AdministrationTestData.DirectoryFixture();
                var original = Path.Combine(source.Path, "not-a-log.txt");
                File.WriteAllText(original, "outside the approved log root");
                Assert.True(CreateHardLink(Path.Combine(logs.Path, "redirected.log"), original, IntPtr.Zero));
                var folders = new CaptureFileExports();
                var folder = Complete(folders.RegisterFolderAsync(destination.Path));
                var diagnostics = new ApplicationDiagnostics(folders, Prerequisites, () => null, () => logs.Path);
                var snapshot = Complete(diagnostics.InspectAsync());
                var plan = Complete(diagnostics.PrepareExportAsync(snapshot.Revision,
                    AdministrationArtifactKind.DiagnosticLogs, new(folder.Id, "logs.zip")));
                Assert.Equal(AdministrationFailure.InvalidRequest, Assert.Throws<AdministrationException>(() =>
                    Complete(diagnostics.ExportAsync(plan, 65536, TimeSpan.FromSeconds(10), _ => Task.CompletedTask))).Failure);
                Assert.Empty(Directory.GetFiles(destination.Path));
                Assert.Equal("outside the approved log root", File.ReadAllText(original));
            });
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string link, string existing, IntPtr security);

        private sealed class PausedDiagnostics(IApplicationDiagnostics inner) : IApplicationDiagnostics
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<ApplicationDiagnosticSnapshot> InspectAsync(CancellationToken cancellationToken = default) =>
                inner.InspectAsync(cancellationToken);
            public Task DrainAsync() => inner.DrainAsync();
            public Task<AdministrationExportPlan> PrepareExportAsync(
                string expectedRevision, AdministrationArtifactKind kind, AdministrationDestination destination,
                CancellationToken cancellationToken = default) =>
                inner.PrepareExportAsync(expectedRevision, kind, destination, cancellationToken);
            public Task<AdministrationExportReceipt> ExportAsync(
                AdministrationExportPlan plan, long maximumBytes, TimeSpan timeout,
                Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) =>
                inner.ExportAsync(plan, maximumBytes, timeout, async token =>
                {
                    Entered.TrySetResult();
                    await Continue.Task.WaitAsync(token);
                    await beforePublish(token);
                }, cancellationToken);
        }
    }
}
