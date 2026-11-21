// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT
{
    internal sealed partial class AnalysisReportExporter
    {
        internal async Task<long> ExportApprovedReplacementAsync(
            AnalysisExportSource source, WindowsExportFolder parent, CaptureReportDestination destination, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(beforePublish);
            var sourceDirectory = ValidateSourceDirectory(source);
            var path = parent.Destination(destination.DirectoryName);
            ValidateDestinationParent(sourceDirectory, path);
            EnsureCompatiblePaths(path, source.ArtifactManifest, parent.Path);
            using var target = new WindowsExportFolder(path);
            if (CaptureFilePublication.Identity(target.Identity) != destination.ExistingDirectoryIdentity ||
                destination.Files == null || destination.Files.Count != source.ArtifactManifest.Count)
                throw new CaptureFileException(CaptureFileFailure.FolderChanged);
            var replacements = destination.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long length = 0;
            foreach (var artifact in source.ArtifactManifest)
            {
                ValidateArtifactDefinition(artifact, sourceDirectory, hashes);
                hashes.Add(artifact.RelativePath, artifact.Sha256);
                if (!replacements.ContainsKey(artifact.RelativePath))
                    throw new CaptureFileException(CaptureFileFailure.InputChanged);
                length = checked(length + artifact.Length);
                if (length > maximumBytes)
                    throw new QueryException(QueryError.ResultLimit, "The report exceeds the approved output limit.");
            }
            using var sourceFolder = new WindowsExportFolder(sourceDirectory);
            var work = new ReplacementWork();
            Exception failure = null;
            try
            {
                foreach (var artifact in source.ArtifactManifest)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var member = new ReplacementMember(artifact.RelativePath, target.CreateTemporary(readable: true));
                    work.Members.Add(member);
                    await CopyApprovedArtifactAsync(sourceFolder, member.Output, artifact, cancellationToken);
                    member.Publication = await CaptureFilePublication.PrepareAsync(target, artifact.RelativePath,
                        replacements[artifact.RelativePath].Previous, member.Output, cancellationToken);
                }
                await beforePublish(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var member in work.Members)
                    member.Publication.Demand();
                foreach (var member in work.Members)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    member.Publication.Commit();
                }
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            { failure = exception; }
            var all = work.Members.ToArray();
            var cleaned = await work.CleanupAsync();
            var published = all.Where(member => member.Publication?.Published == true).Select(member => member.Name).ToArray();
            var uncertain = all.Any(member => member.Publication?.Uncertain == true);
            if (!cleaned || published.Length > 0 && failure != null || uncertain)
                throw new AnalysisReportExportException(AnalysisReportExportFailureKind.PublishFailed,
                    "The report replacement requires review. Do not retry the whole export.",
                    failure, published.Length > 0 || uncertain ? AnalysisReportPublicationState.Unknown : AnalysisReportPublicationState.NotPublished, path)
                {
                    PublishedFiles = published,
                    RetainedFiles = work.Members.Select(member => member.Name).ToArray(),
                    ResiduePath = cleaned ? null : path,
                    CleanupResidue = cleaned ? null : work.CleanupAsync
                };
            if (failure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return length;
        }

        private sealed class ReplacementMember(string name, FileStream output)
        {
            internal string Name { get; } = name;
            internal FileStream Output { get; } = output;
            internal CaptureFilePublication.Prepared Publication { get; set; }
        }

        private sealed class ReplacementWork
        {
            internal List<ReplacementMember> Members { get; } = new();
            internal async Task<bool> CleanupAsync()
            {
                foreach (var member in Members.ToArray())
                {
                    if (member.Publication?.Uncertain == true && !member.Publication.ResolveOutcome())
                        continue;
                    try
                    {
                        if (member.Publication?.Published != true)
                            WindowsExportFolder.DeleteTemporary(member.Output);
                        if (member.Publication != null)
                            await member.Publication.DisposeAsync();
                        await member.Output.DisposeAsync();
                        Members.Remove(member);
                    }
                    catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                    { }
                }
                return Members.Count == 0;
            }
        }
    }
}
