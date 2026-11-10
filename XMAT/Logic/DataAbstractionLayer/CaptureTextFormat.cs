// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.DataAbstractionLayer
{
    internal static class CaptureTextFormat
    {
        internal const int AmbiguousUserVersion = 0;
        internal const int LiteralUserVersion = 1;
    }

    internal enum CaptureTextSemantics
    {
        Literal,
        AmbiguousUnmarked,
    }

    internal sealed class CaptureTextSourceException : Exception
    {
        internal const string SourceUnavailableCode =
            "capture.text.source-unavailable";
        internal const string SourceUnavailableMessage =
            "The capture text source is no longer available for a source-bound decision.";

        internal CaptureTextSourceException()
            : base(SourceUnavailableMessage)
        {
        }

        internal string Code => SourceUnavailableCode;
    }

    internal sealed class CaptureTextSource
    {
        private readonly Func<CancellationToken, byte[]> _computeSha256;
        private readonly Func<bool> _isAvailable;
        private readonly SemaphoreSlim _bindLock = new(1, 1);
        private string _sha256;

        private CaptureTextSource(
            CaptureTextSemantics semantics,
            SqLtFileIdentity? identity,
            Func<CancellationToken, byte[]> computeSha256,
            Func<bool> isAvailable)
        {
            Semantics = semantics;
            Identity = identity;
            _computeSha256 = computeSha256;
            _isAvailable = isAvailable;
        }

        internal static CaptureTextSource Literal { get; } =
            new(CaptureTextSemantics.Literal, null, null, null);

        internal static CaptureTextSource Unknown { get; } =
            new(
                CaptureTextSemantics.AmbiguousUnmarked,
                null,
                null,
                null);

        internal CaptureTextSemantics Semantics { get; }

        internal SqLtFileIdentity? Identity { get; }

        internal string Sha256 =>
            Volatile.Read(ref _sha256);

        internal bool IsBindable =>
            _computeSha256 != null &&
            _isAvailable();

        internal static CaptureTextSource CreateFile(
            CaptureTextSemantics semantics,
            SqLtFileIdentity identity,
            Func<CancellationToken, byte[]> computeSha256,
            Func<bool> isAvailable)
        {
            ArgumentNullException.ThrowIfNull(computeSha256);
            ArgumentNullException.ThrowIfNull(isAvailable);
            return new CaptureTextSource(
                semantics,
                identity,
                computeSha256,
                isAvailable);
        }

        // A source for text already read into memory from the file whose content had this SHA-256: bound, and available
        // after the file is closed, because the text it describes no longer depends on the file.
        internal static CaptureTextSource CreateSnapshot(
            CaptureTextSemantics semantics,
            SqLtFileIdentity identity,
            string sha256)
        {
            ArgumentException.ThrowIfNullOrEmpty(sha256);
            var source = new CaptureTextSource(
                semantics,
                identity,
                _ => throw new InvalidOperationException(),
                () => true);
            source._sha256 = sha256.ToLowerInvariant();
            return source;
        }

        internal static CaptureTextSource Of(object value) =>
            value is XMAT.SharedInterfaces.ICaptureTextSourceProvider provider
                ? provider.CaptureTextSource ??
                    Unknown
                : Unknown;

        internal async ValueTask<CaptureTextSource> BindAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_computeSha256 == null)
            {
                if (Semantics == CaptureTextSemantics.Literal)
                {
                    return this;
                }

                throw new CaptureTextSourceException();
            }

            if (!_isAvailable())
            {
                throw new CaptureTextSourceException();
            }

            await _bindLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_isAvailable())
                {
                    throw new CaptureTextSourceException();
                }

                if (_sha256 == null)
                {
                    byte[] hash;
                    try
                    {
                        hash = await Task.Run(
                            () => _computeSha256(cancellationToken),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (
                        !ExceptionUtilities.IsFatal(exception))
                    {
                        throw new CaptureTextSourceException();
                    }

                    Volatile.Write(
                        ref _sha256,
                        Convert.ToHexString(hash).ToLowerInvariant());
                }

                return this;
            }
            finally
            {
                _bindLock.Release();
            }
        }

        internal bool Matches(CaptureTextSource other) =>
            other != null &&
            Semantics == other.Semantics &&
            Identity.HasValue &&
            other.Identity.HasValue &&
            Identity.Value == other.Identity.Value &&
            Sha256 != null &&
            string.Equals(
                Sha256,
                other.Sha256,
                StringComparison.Ordinal);
    }
}
