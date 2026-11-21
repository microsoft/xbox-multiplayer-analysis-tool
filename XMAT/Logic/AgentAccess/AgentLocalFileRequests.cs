// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal static class AgentLocalFileRequests
    {
        internal static async Task<CaptureLocalDestination> OutputAsync(
            AgentRuntime runtime, AgentSession session, ICaptureFileExports exports,
            string path, string extension, bool overwrite, CancellationToken cancellationToken)
        {
            runtime.Demand(session, Array.Empty<AgentPermission>());
            if (exports == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            DemandExtension(path, extension);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(runtime.GetResponseLimits(session).QueryTimeoutSeconds));
            try
            { return await exports.PrepareDestinationAsync(path, overwrite, timeout.Token); }
            catch (CaptureFileException exception) { throw AgentFileErrors.Translate(exception); }
            catch (QueryException) { throw new AgentAccessException(AgentAccessError.LimitExceeded); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
        }

        internal static async Task<InputFileDescriptor> InputAsync(
            AgentRuntime runtime, AgentSession session, ICaptureFileInputs inputs,
            string path, CaptureInputKind kind, CancellationToken cancellationToken)
        {
            runtime.Demand(session, Array.Empty<AgentPermission>());
            if (inputs == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            DemandExtension(path, kind switch
            {
                CaptureInputKind.ScriptSource => ".txt",
                CaptureInputKind.CaptureDatabase => ".xmatcap",
                CaptureInputKind.FiddlerSaz => ".saz",
                CaptureInputKind.NetworkTraceEtl => ".etl",
                _ => throw new AgentAccessException(AgentAccessError.InvalidRequest)
            });
            var limits = runtime.GetResponseLimits(session);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
            try
            { return await inputs.RegisterAsync(path, limits.MaximumQueryResultBytes, timeout.Token); }
            catch (CaptureFileException exception) { throw AgentFileErrors.Translate(exception); }
            catch (QueryException) { throw new AgentAccessException(AgentAccessError.LimitExceeded); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
        }

        private static void DemandExtension(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                throw new AgentAccessException(AgentAccessError.InvalidPath);
        }

        internal static string Warning(FileOverwriteState overwrite) => overwrite == null ? string.Empty :
            Environment.NewLine + Localization.GetLocalizedString("AGENT_FILE_OVERWRITE_WARNING", overwrite.Length, overwrite.Sha256);
    }
}
