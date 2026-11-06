// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    internal enum BoundedSazFailure
    {
        Malformed,
        InputTooLarge,
        EntryTooLarge,
        ExpandedDataTooLarge,
        TooManyRecords,
        TooManyEntries,
        CentralDirectoryTooLarge,
        HeaderLimit,
    }

    internal sealed class BoundedSazException : Exception
    {
        internal BoundedSazException(BoundedSazFailure failure)
        {
            Failure = failure;
        }

        internal BoundedSazException(BoundedSazFailure failure, Exception innerException)
            : base("The SAZ input is invalid.", innerException)
        {
            Failure = failure;
        }

        internal BoundedSazFailure Failure { get; }
    }

    internal sealed record BoundedSazLimits(
        long MaxInputBytes,
        long MaxEntryBytes,
        long MaxTotalUncompressedBytes,
        int MaxRecords,
        long MaxArchiveEntries,
        long MaxCentralDirectoryBytes);

    internal sealed record BoundedSazReadResult(
        IReadOnlyList<ProxyConnectionModel> Records,
        int SkippedIncompleteFrames,
        int SkippedMalformedHeaderLines);

    internal static class BoundedSazReader
    {
        internal const long DefaultMaxCentralDirectoryBytes =
            32L * 1024 * 1024;

        // The central directory lists three entries per request; DefaultMaxCentralDirectoryBytes is sized for the
        // default 100,000 requests and grows in proportion to the request limit (CentralDirectoryLimitForRecords).
        // These absolute ceilings on the directory's size and on its entries hold whatever limits a caller passes, so
        // the archive metadata stays bounded independently of the data limits. They fit xmat analyze's largest request
        // limit (three entries per request plus 64, and 32 MiB per 100,000 requests). Both are policy ceilings, not
        // a guaranteed capacity or a bound on xmat's memory use.
        internal const long MaxCentralDirectoryCeiling =
            SazArchivePreflight.MaxCentralDirectoryBytes;
        internal const long MaxArchiveEntriesCeiling =
            SazArchivePreflight.MaxArchiveEntries;
        private const int CentralDirectoryReferenceRecords = 100_000;
        private const int NonFrameEntryAllowance = 64;

        private static readonly Regex FrameEntryPattern = new(
            @"^(?<frame>[0-9]+)_(?<kind>c\.txt|s\.txt|m\.xml)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        internal static BoundedSazReadResult Read(
            Stream input,
            BoundedSazLimits limits,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(limits);
            ValidateLimits(limits);

            try
            {
                if (!input.CanRead ||
                    !input.CanSeek ||
                    input.Length > limits.MaxInputBytes)
                {
                    throw new BoundedSazException(BoundedSazFailure.InputTooLarge);
                }

                PreflightArchive(input, limits, cancellationToken);
                input.Seek(0, SeekOrigin.Begin);
                using var archive = new ZipArchive(input, ZipArchiveMode.Read, true);
                var frames = new Dictionary<uint, FrameParts>();
                long totalUncompressedBytes = 0;

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var match = FrameEntryPattern.Match(entry.Name);
                    FrameParts frame = null;
                    string kind = null;
                    if (match.Success &&
                        uint.TryParse(match.Groups["frame"].Value, out var frameId))
                    {
                        if (!frames.TryGetValue(frameId, out frame))
                        {
                            if (frames.Count >= limits.MaxRecords)
                            {
                                throw new BoundedSazException(BoundedSazFailure.TooManyRecords);
                            }

                            frame = new FrameParts(frameId);
                            frames.Add(frameId, frame);
                        }
                        kind = match.Groups["kind"].Value;
                    }

                    var bytes = ReadEntry(
                        entry,
                        kind != null,
                        limits,
                        ref totalUncompressedBytes,
                        cancellationToken);
                    if (kind != null)
                    {
                        frame.Set(kind, bytes);
                    }
                }

                var records = new List<ProxyConnectionModel>(frames.Count);
                var skippedIncompleteFrames = 0;
                var skippedMalformedHeaderLines = 0;
                try
                {
                    foreach (var frame in frames.Values.OrderBy(value => value.Id))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        byte[] requestBytes = frame.Request;
                        byte[] metadataBytes = frame.Metadata;
                        byte[] responseBytes = frame.Response;
                        frame.ReleaseBuffers();
                        if (requestBytes == null ||
                            metadataBytes == null)
                        {
                            skippedIncompleteFrames++;
                            continue;
                        }

                        using var request =
                            new MemoryStream(
                                requestBytes,
                                false);
                        using var metadata =
                            new MemoryStream(
                                metadataBytes,
                                false);
                        using var response = responseBytes == null
                            ? null
                            : new MemoryStream(
                                responseBytes,
                                false);
                        ProxyConnectionModel record =
                            FiddlerSazHandler.FromFiddlerFrame(
                                frame.Id,
                                request,
                                metadata,
                                response,
                                out var skippedInFrame);
                        skippedMalformedHeaderLines += skippedInFrame;
                        if (record == null)
                        {
                            skippedIncompleteFrames++;
                            continue;
                        }
                        records.Add(record);
                    }
                }
                finally
                {
                    foreach (FrameParts frame in
                        frames.Values)
                    {
                        frame.ReleaseBuffers();
                    }
                    frames.Clear();
                }

                return new BoundedSazReadResult(
                    records,
                    skippedIncompleteFrames,
                    skippedMalformedHeaderLines);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BoundedSazException)
            {
                throw;
            }
            catch (FiddlerSazHeaderLimitException ex)
            {
                throw new BoundedSazException(
                    BoundedSazFailure.HeaderLimit,
                    ex);
            }
            catch (OverflowException ex)
            {
                throw new BoundedSazException(
                    BoundedSazFailure.ExpandedDataTooLarge,
                    ex);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new BoundedSazException(BoundedSazFailure.Malformed, ex);
            }
        }

        internal static long EntryLimitForRecords(int maxRecords)
        {
            return checked((long)maxRecords * 3 + NonFrameEntryAllowance);
        }

        // DefaultMaxCentralDirectoryBytes per 100,000 requests (rounded up), at least DefaultMaxCentralDirectoryBytes
        // and at most MaxCentralDirectoryCeiling.
        internal static long CentralDirectoryLimitForRecords(int maxRecords)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);
            long scaled = checked(
                ((DefaultMaxCentralDirectoryBytes * maxRecords) + CentralDirectoryReferenceRecords - 1) /
                CentralDirectoryReferenceRecords);
            return Math.Clamp(
                scaled,
                DefaultMaxCentralDirectoryBytes,
                MaxCentralDirectoryCeiling);
        }

        private static void PreflightArchive(
            Stream input,
            BoundedSazLimits limits,
            CancellationToken cancellationToken)
        {
            try
            {
                SazArchivePreflight.Validate(
                    input,
                    limits.MaxArchiveEntries,
                    limits.MaxCentralDirectoryBytes,
                    cancellationToken);
            }
            catch (SazArchiveException ex)
            {
                throw new BoundedSazException(
                    ex.Failure switch
                    {
                        SazArchiveFailure.TooManyEntries =>
                            BoundedSazFailure.TooManyEntries,
                        SazArchiveFailure.CentralDirectoryTooLarge =>
                            BoundedSazFailure.CentralDirectoryTooLarge,
                        _ => BoundedSazFailure.Malformed,
                    },
                    ex);
            }
        }

        // HTTP bodies stay opaque here. Any future decoder must enforce a decoded-size bound at the decode point.
        private static byte[] ReadEntry(
            ZipArchiveEntry entry,
            bool retain,
            BoundedSazLimits limits,
            ref long totalUncompressedBytes,
            CancellationToken cancellationToken)
        {
            using var source = entry.Open();
            using var destination = retain ? new MemoryStream() : null;
            var buffer = new byte[64 * 1024];
            long entryBytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                entryBytes = checked(entryBytes + read);
                totalUncompressedBytes = checked(totalUncompressedBytes + read);
                if (entryBytes > limits.MaxEntryBytes)
                {
                    throw new BoundedSazException(BoundedSazFailure.EntryTooLarge);
                }
                if (totalUncompressedBytes > limits.MaxTotalUncompressedBytes)
                {
                    throw new BoundedSazException(BoundedSazFailure.ExpandedDataTooLarge);
                }

                destination?.Write(buffer, 0, read);
            }

            return destination?.ToArray();
        }

        private static void ValidateLimits(BoundedSazLimits limits)
        {
            if (limits.MaxInputBytes <= 0 ||
                limits.MaxEntryBytes <= 0 ||
                limits.MaxTotalUncompressedBytes <= 0 ||
                limits.MaxRecords <= 0 ||
                limits.MaxArchiveEntries <= 0 ||
                limits.MaxCentralDirectoryBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limits));
            }
        }

        private sealed class FrameParts
        {
            internal FrameParts(uint id)
            {
                Id = id;
            }

            internal uint Id { get; }
            internal byte[] Request { get; private set; }
            internal byte[] Metadata { get; private set; }
            internal byte[] Response { get; private set; }

            internal void ReleaseBuffers()
            {
                Request = null;
                Metadata = null;
                Response = null;
            }

            internal void Set(string kind, byte[] bytes)
            {
                if (kind.Equals("c.txt", StringComparison.OrdinalIgnoreCase))
                {
                    Request = SetOnce(Request, bytes);
                }
                else if (kind.Equals("m.xml", StringComparison.OrdinalIgnoreCase))
                {
                    Metadata = SetOnce(Metadata, bytes);
                }
                else if (kind.Equals("s.txt", StringComparison.OrdinalIgnoreCase))
                {
                    Response = SetOnce(Response, bytes);
                }
            }

            private static byte[] SetOnce(byte[] existing, byte[] value)
            {
                if (existing != null)
                {
                    throw new BoundedSazException(BoundedSazFailure.Malformed);
                }
                return value;
            }
        }
    }
}
