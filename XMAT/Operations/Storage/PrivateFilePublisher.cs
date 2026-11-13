// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XMAT.Operations.Storage
{
    internal enum PrivateFilePublication
    {
        Published,
        Exists,
        Unsupported,
        Cancelled,
        Failed,
        CleanupFailed,
    }

    // Test-only checkpoints around the operations whose race outcomes the publisher must preserve.
    internal enum PrivateFilePublisherCheckpoint
    {
        BeforeCreate,
        BeforeWrite,
        BeforeRename,
        AfterCommit,
    }

    // Test seams. Production uses cryptographic staging names, the real admission check and handle deletion.
    internal sealed record PrivateFilePublisherTestHooks(
        Func<int, string> StagingName = null,
        Action<PrivateFilePublisherCheckpoint> Checkpoint = null,
        Action<SafeFileHandle> BeforeVerify = null,
        Func<SafeFileHandle, bool> VerifyStaging = null,
        Func<SafeFileHandle, bool> RemoveStaging = null,
        Func<bool> CloseSucceeded = null);

    // Publishes one private file without following or replacing any namespace entry. The parent is held from its volume
    // root, the private staging file is created and verified through its handle, and a no-replace handle-relative rename
    // is the commit. It logs nothing and exposes no exception text.
    internal static class PrivateFilePublisher
    {
        internal const string StagingPrefix = ".xmat-";
        internal const string StagingSuffix = ".tmp";
        internal const int StagingNameLength = 42;
        private const int StagingAttempts = 4;
        private const int RandomBytes = 16;
        private const int StreamBufferBytes = 64 * 1024;

        // Synchronous; the GUI calls it off the UI thread. write may throw OperationCanceledException (Cancelled).
        internal static PrivateFilePublication Publish(
            string path, Action<Stream> write, CancellationToken cancellationToken) =>
            Publish(path, write, null, cancellationToken);

        internal static PrivateFilePublication Publish(
            string path,
            Action<Stream> write,
            PrivateFilePublisherTestHooks hooks,
            CancellationToken cancellationToken)
        {
            if (write == null)
            {
                return PrivateFilePublication.Failed;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                StorageAdmissionFailure? parsed = StorageAdmission.ParseTarget(path, out StorageTarget target);
                if (parsed != null)
                {
                    return AdmissionResult(parsed.Value);
                }

                cancellationToken.ThrowIfCancellationRequested();
                StorageAdmissionFailure? held =
                    StorageAdmission.HoldParent(target, out StorageAnchor anchor, null, cancellationToken);
                if (held != null)
                {
                    return AdmissionResult(held.Value);
                }

                using (anchor)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AnchoredHandles.Exists(anchor.Parent, target.Name))
                    {
                        return PrivateFilePublication.Exists;
                    }

                    return PublishHeld(anchor, target.Name, write, hooks, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return PrivateFilePublication.Cancelled;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return PrivateFilePublication.Failed;
            }
        }

        private static PrivateFilePublication PublishHeld(
            StorageAnchor anchor,
            string targetName,
            Action<Stream> write,
            PrivateFilePublisherTestHooks hooks,
            CancellationToken cancellationToken)
        {
            SafeFileHandle staging = null;
            FileStream stream = null;
            bool committed = false;
            PrivateFilePublication result = PrivateFilePublication.Failed;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var security = StorageAdmission.CurrentUserFileSecurity();
                for (int attempt = 1; attempt <= StagingAttempts && staging == null; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = hooks?.StagingName?.Invoke(attempt) ?? NewStagingName();
                    if (!IsStagingName(name))
                    {
                        return PrivateFilePublication.Failed;
                    }

                    hooks?.Checkpoint?.Invoke(PrivateFilePublisherCheckpoint.BeforeCreate);
                    cancellationToken.ThrowIfCancellationRequested();
                    staging = AnchoredHandles.CreateNewFile(anchor.Parent, name, security);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (staging == null)
                {
                    return PrivateFilePublication.Failed;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!anchor.IsChainAdmitted())
                {
                    result = PrivateFilePublication.Unsupported;
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hooks?.BeforeVerify?.Invoke(staging);
                    cancellationToken.ThrowIfCancellationRequested();
                    bool verified = hooks?.VerifyStaging?.Invoke(staging) ?? StorageAdmission.IsPrivateFile(staging);
                    if (!verified)
                    {
                        result = PrivateFilePublication.Unsupported;
                    }
                    else
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stream = new FileStream(staging, FileAccess.Write, StreamBufferBytes, isAsync: false);
                        using var destination = new PublisherWriteStream(stream);
                        hooks?.Checkpoint?.Invoke(PrivateFilePublisherCheckpoint.BeforeWrite);
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            write(destination);
                        }
                        finally
                        {
                            // The callback never owns the file and cannot keep writing after it returns.
                            destination.Stop();
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        stream.Flush();
                        cancellationToken.ThrowIfCancellationRequested();
                        AnchoredHandles.FlushFileBuffers(staging);

                        cancellationToken.ThrowIfCancellationRequested();
                        if (AnchoredHandles.Exists(anchor.Parent, targetName))
                        {
                            result = PrivateFilePublication.Exists;
                        }
                        else
                        {
                            hooks?.Checkpoint?.Invoke(PrivateFilePublisherCheckpoint.BeforeRename);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!AnchoredHandles.TryRenameFile(staging, anchor.Parent, targetName))
                            {
                                result = PrivateFilePublication.Exists;
                            }
                            else
                            {
                                // The rename is the commit. Nothing after this point may delete the published file.
                                committed = true;
                                result = PrivateFilePublication.Published;
                                hooks?.Checkpoint?.Invoke(PrivateFilePublisherCheckpoint.AfterCommit);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = committed ? PrivateFilePublication.Published : PrivateFilePublication.Cancelled;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                result = committed ? PrivateFilePublication.Published : PrivateFilePublication.Failed;
            }
            finally
            {
                bool removed = staging == null || committed || Remove(staging, hooks);
                Close(stream, staging, hooks);
                if (!committed && !removed)
                {
                    // Cleanup failure takes precedence over every pre-commit outcome.
                    result = PrivateFilePublication.CleanupFailed;
                }
            }

            return result;
        }

        private static bool Remove(SafeFileHandle staging, PrivateFilePublisherTestHooks hooks)
        {
            try
            {
                if (hooks?.RemoveStaging != null)
                {
                    return hooks.RemoveStaging(staging);
                }

                AnchoredHandles.MarkFileForDeletion(staging);
                return true;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return false;
            }
        }

        private static void Close(
            FileStream stream, SafeFileHandle staging, PrivateFilePublisherTestHooks hooks)
        {
            try
            {
                if (stream != null)
                {
                    stream.Dispose();
                }
                else
                {
                    staging?.Dispose();
                }

                if (hooks?.CloseSucceeded?.Invoke() == false)
                {
                    throw new IOException("The test close failed.");
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // The result was decided by the commit or cleanup; closing reports no path and cannot change it.
                try
                {
                    staging?.Dispose();
                }
                catch (Exception retry) when (!ExceptionUtilities.IsFatal(retry))
                {
                    // Safe-handle cleanup has no further recovery path.
                }
            }
        }

        // The writer sees only this non-owning, write-only view. Disposing it does not close the publisher's handle, and
        // Stop makes every later operation fail before the file is flushed or committed.
        private sealed class PublisherWriteStream : Stream
        {
            private readonly FileStream _stream;
            private readonly object _gate = new();
            private bool _active = true;

            internal PublisherWriteStream(FileStream stream)
            {
                _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite
            {
                get
                {
                    lock (_gate)
                    {
                        return _active;
                    }
                }
            }

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            internal void Stop()
            {
                lock (_gate)
                {
                    _active = false;
                }
            }

            public override void Flush()
            {
                lock (_gate)
                {
                    Active();
                    _stream.Flush();
                }
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    Active();
                    _stream.Write(buffer, offset, count);
                }
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                lock (_gate)
                {
                    Active();
                    _stream.Write(buffer);
                }
            }

            public override System.Threading.Tasks.Task WriteAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(buffer);
                ReadOnlyMemory<byte> memory = buffer.AsMemory(offset, count);
                if (cancellationToken.IsCancellationRequested)
                {
                    return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
                }

                Write(memory.Span);
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public override System.Threading.Tasks.ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return System.Threading.Tasks.ValueTask.FromCanceled(cancellationToken);
                }

                Write(buffer.Span);
                return System.Threading.Tasks.ValueTask.CompletedTask;
            }

            public override void WriteByte(byte value)
            {
                lock (_gate)
                {
                    Active();
                    _stream.WriteByte(value);
                }
            }

            protected override void Dispose(bool disposing)
            {
                // The callback does not own the publisher's stream. StreamWriter and similar wrappers may dispose this
                // view to flush themselves, but the publisher keeps the file handle until cleanup or commit.
                base.Dispose(disposing);
            }

            private void Active()
            {
                ObjectDisposedException.ThrowIf(!_active, this);
            }
        }

        private static string NewStagingName()
        {
            Span<byte> random = stackalloc byte[RandomBytes];
            RandomNumberGenerator.Fill(random);
            return StagingPrefix + Convert.ToHexString(random) + StagingSuffix;
        }

        private static bool IsStagingName(string name)
        {
            if (name == null ||
                name.Length != StagingNameLength ||
                !name.StartsWith(StagingPrefix, StringComparison.Ordinal) ||
                !name.EndsWith(StagingSuffix, StringComparison.Ordinal))
            {
                return false;
            }

            int end = name.Length - StagingSuffix.Length;
            for (int index = StagingPrefix.Length; index < end; index++)
            {
                if (!char.IsAsciiHexDigit(name[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static PrivateFilePublication AdmissionResult(StorageAdmissionFailure failure) => failure switch
        {
            StorageAdmissionFailure.ParentMissing => PrivateFilePublication.Failed,
            _ => PrivateFilePublication.Unsupported,
        };
    }
}
