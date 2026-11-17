// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.AgentAccess;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed record StartupRecoverySnapshot(
        string Revision, string PcRevision, bool PcUsesProxy, string PcProxyServer, bool PcLeasePresent,
        string PcLeaseOwnerState, bool CanRecoverPcLease, bool CanDisableAbandonedLoopback,
        IReadOnlyList<string> AbandonedXboxConsoles, bool MoreXboxRecords, string Guidance);

    internal sealed class ApplicationStartupRecovery(
        Func<PcProxyRecovery> pc = null, Func<AbandonedXboxSessions> xbox = null)
    {
        private PcProxyRecovery Pc => pc?.Invoke() ?? PcProxyRecovery.Current;
        private AbandonedXboxSessions Xbox() => xbox?.Invoke() ??
            XboxProxySessions.FindAbandoned(PcProxyRecovery.DefaultDirectory, cleanInvalid: false, maximumRecords: 64);

        internal Task<StartupRecoverySnapshot> InspectAsync(CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Describe(Pc.InspectStartup(), Xbox());
            }, cancellationToken);

        internal Task<object> ExecuteAsync(
            string expectedRevision, bool disableAbandonedLoopback, bool acknowledgeXbox,
            CancellationToken cancellationToken) => Task.Run<object>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pcState = Pc.InspectStartup();
                var xboxState = Xbox();
                var current = Describe(pcState, xboxState);
                if (current.Revision != expectedRevision)
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                if (!acknowledgeXbox && !current.CanRecoverPcLease &&
                    !(disableAbandonedLoopback && current.CanDisableAbandonedLoopback))
                    throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                var result = acknowledgeXbox ? RecoveryResult.NotNeeded :
                    Pc.RecoverStartup(pcState.Revision, disableAbandonedLoopback, cancellationToken);
                if (result == RecoveryResult.Superseded)
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                if (result == RecoveryResult.Failed)
                    throw new AgentAccessException(AgentAccessError.RecoveryRequired);
                if (acknowledgeXbox)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    XboxProxySessions.Acknowledge(xboxState);
                    if (xboxState.Files.Any(File.Exists))
                        throw new AgentAccessException(AgentAccessError.RecoveryRequired);
                }
                return new
                {
                    result,
                    xboxNoticeAcknowledged = acknowledgeXbox,
                    xboxProxyChanged = false,
                    consoleRestarted = false,
                    windowsProxyMayHaveChanged = !acknowledgeXbox && result == RecoveryResult.Restored
                };
            }, cancellationToken);

        private static StartupRecoverySnapshot Describe(PcProxyStartupState pc, AbandonedXboxSessions xbox)
        {
            var consoles = xbox.Consoles.Take(64).Select(name => name.Length > 256 ? name[..256] : name).ToArray();
            var revision = AgentOperationDescriptor.Hash(new { pc.Revision, consoles, xbox.Files, xbox.HasMore });
            return new(revision, pc.Revision, pc.Settings.UsesProxy, pc.Settings.Server, pc.Lease != null,
                pc.OwnerState?.ToString(), pc.Lease != null && pc.OwnerState == ProcessState.Dead,
                pc.AbandonedLoopback && (pc.Lease == null || pc.OwnerState == ProcessState.Dead),
                consoles, xbox.HasMore || xbox.Consoles.Count > 64,
                Localization.GetLocalizedString("AGENT_STARTUP_RECOVERY_GUIDANCE"));
        }
    }
}
