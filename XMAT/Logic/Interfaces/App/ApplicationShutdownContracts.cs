// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace XMAT.SharedInterfaces
{
    public enum ShutdownDocumentAction { Cancel, Save, Discard }
    public sealed record ShutdownDocument(Guid Id, long Revision, string Title, string FilePath);
    public sealed record ShutdownDocumentChoice(
        Guid Id, long ExpectedRevision, ShutdownDocumentAction Action = ShutdownDocumentAction.Cancel,
        string Path = null, bool Overwrite = false);
    public sealed record ApplicationShutdownChoices
    {
        public bool DiscardCapturedData { get; init; }
        public string SaveCapturePath { get; init; }
        public bool OverwriteCapture { get; init; }
        public bool DiscardScriptDrafts { get; init; }
        public bool DisableXboxProxies { get; init; }
        public IReadOnlyList<ShutdownDocumentChoice> Documents { get; init; } = Array.Empty<ShutdownDocumentChoice>();
    }

    public sealed record ApplicationShutdownSnapshot(
        string Revision, bool Headless, bool CanRequestClose, bool HasCapturedData,
        IReadOnlyList<CaptureDescriptor> Captures, IReadOnlyList<ShutdownDocument> UnsavedSqlDocuments,
        IReadOnlyList<CaptureScriptDescriptor> ScriptDrafts, bool RecoveryRequired);
    public sealed record ApplicationShutdownReceipt(
        bool Requested, string PreparedRevision, int StoppedCaptures, bool CaptureSaved,
        int SavedSqlDocuments, bool SettingsSaved, bool ProcessExitConfirmed = false, int XboxProxyAttempts = 0);
}
