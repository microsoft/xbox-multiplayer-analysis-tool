// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal static class AgentFileErrors
    {
        internal static AgentAccessException Translate(CaptureFileException exception) =>
            exception.Failure == CaptureFileFailure.PublicationUncertain
                ? new(AgentAccessError.RecoveryRequired, AgentJson.Encode(new
                {
                    error = AgentAccessError.RecoveryRequired,
                    publication = "unknown",
                    doNotRetry = true,
                    retainedTemporary = exception.RecoveryTemporaryPath,
                    requestedDestination = exception.RecoveryDestinationPath
                }))
                : new(exception.Failure switch
                {
                    CaptureFileFailure.FolderChanged or CaptureFileFailure.InputChanged => AgentAccessError.SourceChanged,
                    CaptureFileFailure.DestinationExists => AgentAccessError.DestinationExists,
                    CaptureFileFailure.InvalidPath or CaptureFileFailure.ProtectedSource => AgentAccessError.InvalidPath,
                    CaptureFileFailure.InvalidCapture or CaptureFileFailure.UnsupportedVersion => AgentAccessError.InvalidCapture,
                    CaptureFileFailure.SchemaLimit => AgentAccessError.LimitExceeded,
                    _ => AgentAccessError.OperationUnavailable
                });
    }
}
