// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed record AgentBackupMember(string Name, long Length, string Sha256);
    internal sealed record AgentBackupManifest(
        int Version, string Owner, Guid BackupId, Guid OperationId, Guid SourceId, string DataRevision,
        string Kind, string Reason, string MutationScope, string OriginalPath, long Sequence,
        DateTimeOffset CompletedAt, string ActionStatus, IReadOnlyList<AgentBackupMember> Files, IReadOnlyList<string> Exclusions);
    internal sealed record AgentBackupReceipt(
        Guid BackupId, Guid OperationId, string Directory, long Length, string Kind, string ActionStatus, bool OutcomeRecorded = true)
    {
        internal IDisposable Pin { get; init; }
    }
    internal sealed class AgentBackupException(AgentBackupReceipt prepared = null, Exception cause = null)
        : IOException("The required local backup could not be completed.", cause)
    {
        internal AgentBackupReceipt Prepared { get; } = prepared;
    }

    internal sealed class AgentBackupStore
    {
        internal const long StorageLimit = 2L * 1024 * 1024 * 1024;
        internal const int BundleLimit = 10;
        private const string Owner = "Microsoft.XMAT.AgentBackup";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            MaxDepth = 16,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        private readonly string _root;
        private readonly long _maximumBytes;
        private readonly int _maximumBundles;
        private readonly string _mutexName;

        internal static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Xbox Multiplayer Analysis Tool", "AgentAccess", "Backups");

        internal AgentBackupStore(string root = null, long maximumBytes = StorageLimit, int maximumBundles = BundleLimit)
        {
            _root = Path.GetFullPath(root ?? DefaultDirectory);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBundles);
            _maximumBytes = maximumBytes;
            _maximumBundles = maximumBundles;
            _mutexName = @"Local\XMAT.AgentBackups." + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(_root.ToUpperInvariant())));
        }

        internal Task<AgentBackupReceipt> CaptureAsync(
            Guid operationId, Guid sourceId, string revision, string reason, string mutationScope,
            ICaptureDatabaseBackup source, CancellationToken cancellationToken) =>
            CreateAsync(operationId, sourceId, revision, "capture", reason, mutationScope, null,
                (path, token) => source.CreateSnapshotToFile(path, _maximumBytes, token), cancellationToken);

        internal Task<AgentBackupReceipt> FileAsync(
            Guid operationId, string originalPath, Stream source, CancellationToken cancellationToken) =>
            CreateAsync(operationId, Guid.Empty, null, "file", "overwrite", originalPath, originalPath, (path, token) =>
            {
                source.Position = 0;
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    total += count;
                    if (total > _maximumBytes)
                        throw new AgentBackupException();
                    output.Write(buffer, 0, count);
                }
                output.Flush(flushToDisk: true);
            }, cancellationToken);

        private Task<AgentBackupReceipt> CreateAsync(
            Guid operationId, Guid sourceId, string revision, string kind, string reason, string mutationScope, string originalPath,
            Action<string, CancellationToken> write, CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                try
                {
                    return WithLock(() => Create(operationId, sourceId, revision, kind, reason, mutationScope,
                        originalPath, write, cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (AgentBackupException) { throw; }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                { throw new AgentBackupException(cause: exception); }
            }, cancellationToken);

        private AgentBackupReceipt Create(
            Guid operationId, Guid sourceId, string revision, string kind, string reason, string mutationScope, string originalPath,
            Action<string, CancellationToken> write, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_root);
            using var root = new WindowsExportFolder(_root);
            var existing = Inventory(root, cancellationToken);
            var replay = existing.FirstOrDefault(bundle => bundle.Manifest.OperationId == operationId &&
                bundle.Manifest.SourceId == sourceId && bundle.Manifest.Kind == kind &&
                bundle.Manifest.OriginalPath == originalPath && bundle.Manifest.DataRevision == revision);
            if (replay != null)
            {
                EnforceRetention(existing, replay.Manifest.OperationId, cancellationToken);
                return Receipt(replay) with { Pin = new BackupPin(replay.Path, replay.Manifest.Files[0].Name) };
            }
            var id = Guid.NewGuid();
            using var staging = root.CreateTemporaryDirectory();
            AgentBackupReceipt prepared = null;
            Bundle published = null;
            try
            {
                var name = kind == "capture" ? "capture.xmatcap" : "previous-file.bin";
                var path = Path.Combine(staging.Path, name);
                write(path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                AgentBackupMember member;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (file.Length > _maximumBytes)
                        throw new AgentBackupException();
                    file.Flush(flushToDisk: true);
                    member = new(name, file.Length, Convert.ToHexString(
                        SHA256.HashDataAsync(file, cancellationToken).AsTask().GetAwaiter().GetResult()));
                }
                var manifest = new AgentBackupManifest(1, Owner, id, operationId, sourceId, revision,
                    kind, reason, mutationScope, originalPath,
                    existing.Count == 0 ? 1 : checked(existing.Max(bundle => bundle.Manifest.Sequence) + 1),
                    DateTimeOffset.UtcNow, "Prepared",
                    [member], kind == "capture"
                        ? ["Memory-only WebSocket messages", "Unsaved script and SQL drafts", "Device and network configuration"]
                        : Array.Empty<string>());
                using (var file = staging.CreateFile("manifest.json"))
                {
                    JsonSerializer.Serialize(file, manifest, JsonOptions);
                    file.Flush(flushToDisk: true);
                }
                var length = member.Length + new FileInfo(Path.Combine(staging.Path, "manifest.json")).Length;
                if (length + 128 > _maximumBytes)
                    throw new AgentBackupException();
                cancellationToken.ThrowIfCancellationRequested();
                staging.Publish(id.ToString("N"));
                staging.Dispose();
                var bundle = new Bundle(Path.Combine(_root, id.ToString("N")), manifest, length);
                published = bundle;
                prepared = Receipt(bundle);
                existing.Add(bundle);
                EnforceRetention(existing, operationId, cancellationToken);
                return prepared with { Pin = new BackupPin(bundle.Path, member.Name) };
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                if (!staging.Published)
                    staging.DeleteUnpublished();
                else if (published != null)
                {
                    try
                    {
                        DeleteBundle(published, CancellationToken.None);
                        prepared = null;
                    }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                    {
                        // Keep the durable, unused preimage visible when the filesystem prevents cleanup.
                        prepared = prepared with { OutcomeRecorded = false };
                    }
                }
                if (exception is OperationCanceledException && prepared == null)
                    throw;
                throw new AgentBackupException(prepared, exception);
            }
        }

        internal async Task<AgentBackupReceipt> CommittedAsync(AgentBackupReceipt receipt)
        {
            try
            {
                await Task.Run(() => WithLock(() =>
                {
                    using var root = new WindowsExportFolder(_root);
                    var bundle = Inventory(root, CancellationToken.None).SingleOrDefault(item =>
                        item.Manifest.BackupId == receipt.BackupId) ?? throw new AgentBackupException();
                    AtomicSettingsFile.Write(Path.Combine(bundle.Path, "manifest.json"),
                        JsonSerializer.Serialize(bundle.Manifest with { ActionStatus = "Committed" }, JsonOptions));
                    return true;
                }, CancellationToken.None));
                return receipt with { ActionStatus = "Committed" };
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            { return receipt with { ActionStatus = "Committed", OutcomeRecorded = false }; }
        }

        private List<Bundle> Inventory(WindowsExportFolder root, CancellationToken cancellationToken)
        {
            var paths = Directory.EnumerateDirectories(root.Path).Take(257).ToArray();
            if (paths.Length > 256)
                throw new AgentBackupException();
            var bundles = new List<Bundle>();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(path), "N", out var id))
                    continue;
                using var directory = new WindowsExportFolder(path);
                using var file = directory.OpenInput("manifest.json", out _);
                if (file.Length > 65536)
                    throw new AgentBackupException();
                var manifest = JsonSerializer.Deserialize<AgentBackupManifest>(file, JsonOptions);
                if (manifest is not { Version: 1, Owner: Owner, Sequence: > 0 } ||
                    manifest.BackupId != id || manifest.Files?.Count != 1 ||
                    manifest.Kind is not ("capture" or "file") ||
                    manifest.ActionStatus is not ("Prepared" or "Committed") ||
                    manifest.Files[0].Name != (manifest.Kind == "capture" ? "capture.xmatcap" : "previous-file.bin") ||
                    manifest.Files[0].Length < 0 || manifest.Files[0].Sha256?.Length != 64 ||
                    Directory.EnumerateDirectories(path).Any() || Directory.EnumerateFiles(path).Count() != 2)
                    throw new AgentBackupException();
                using var member = directory.OpenInput(manifest.Files[0].Name, out _);
                if (member.Length != manifest.Files[0].Length)
                    throw new AgentBackupException();
                bundles.Add(new(path, manifest, file.Length + member.Length));
            }
            return bundles;
        }

        private void EnforceRetention(List<Bundle> bundles, Guid keepOperation, CancellationToken cancellationToken)
        {
            var groups = bundles.GroupBy(bundle => bundle.Manifest.OperationId).ToArray();
            var length = bundles.Sum(bundle => bundle.Length + 128);
            var count = groups.Length;
            foreach (var group in groups.Where(group => group.Key != keepOperation).OrderBy(group =>
                group.Max(bundle => bundle.Manifest.Sequence)))
            {
                if (count <= _maximumBundles && length <= _maximumBytes)
                    return;
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var bundle in group)
                {
                    DeleteBundle(bundle, cancellationToken);
                    length -= bundle.Length + 128;
                }
                count--;
            }
            if (count > _maximumBundles || length > _maximumBytes)
                throw new AgentBackupException();
        }

        private static void DeleteBundle(Bundle bundle, CancellationToken cancellationToken)
        {
            using (var directory = new WindowsExportFolder(bundle.Path))
            {
                foreach (var member in bundle.Manifest.Files)
                {
                    using (var file = directory.OpenInput(member.Name, out _))
                        if (Convert.ToHexString(SHA256.HashDataAsync(file, cancellationToken).AsTask().GetAwaiter().GetResult()) != member.Sha256)
                            throw new AgentBackupException();
                    File.Delete(Path.Combine(directory.Path, member.Name));
                }
                File.Delete(Path.Combine(directory.Path, "manifest.json"));
            }
            Directory.Delete(bundle.Path);
        }

        private T WithLock<T>(Func<T> action, CancellationToken cancellationToken)
        {
            using var mutex = new Mutex(false, _mutexName);
            var acquired = false;
            try
            {
                try
                { acquired = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], TimeSpan.FromSeconds(30)) == 0; }
                catch (AbandonedMutexException) { acquired = true; }
                cancellationToken.ThrowIfCancellationRequested();
                if (!acquired)
                    throw new AgentBackupException();
                return action();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }

        private static AgentBackupReceipt Receipt(Bundle bundle) =>
            new(bundle.Manifest.BackupId, bundle.Manifest.OperationId, bundle.Path, bundle.Length,
                bundle.Manifest.Kind, bundle.Manifest.ActionStatus);

        private sealed record Bundle(string Path, AgentBackupManifest Manifest, long Length);

        private sealed class BackupPin : IDisposable
        {
            private readonly WindowsExportFolder _directory;
            private readonly FileStream _file;
            internal BackupPin(string path, string member)
            {
                _directory = new(path);
                try
                { _file = _directory.OpenInput(member, out _); }
                catch { _directory.Dispose(); throw; }
            }
            public void Dispose()
            {
                _file.Dispose();
                _directory.Dispose();
            }
        }
    }
}
