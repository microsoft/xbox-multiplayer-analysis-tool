// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal enum AgentDataSharing { Redacted, Complete }
    internal enum AgentActionAccess { InspectAndExport, FullControl }

    internal sealed record AgentAccessSettings
    {
        public int Version { get; init; } = 1;
        public long Revision { get; init; }
        public bool Enabled { get; init; }
        public AgentDataSharing DataSharing { get; init; } = AgentDataSharing.Redacted;
        public AgentActionAccess Actions { get; init; } = AgentActionAccess.InspectAndExport;
        public bool AutomaticBackups { get; init; }

        internal bool IsValid => Version == 1 && Revision >= 0 &&
            Enum.IsDefined(DataSharing) && Enum.IsDefined(Actions);

        internal bool Allows(AgentPermission permission)
        {
            if (!IsValid || !Enabled || permission == null)
                return false;
            return Actions == AgentActionAccess.FullControl || permission.Capability is
                AgentCapability.SummaryRead or AgentCapability.RawDataRead or AgentCapability.ScriptRead or
                AgentCapability.Diagnostics or AgentCapability.Navigation or AgentCapability.QueryResultExport or
                AgentCapability.FileWrite or AgentCapability.CaptureExport or AgentCapability.ScriptExport or
                AgentCapability.FileRead or AgentCapability.CaptureLoad or AgentCapability.AnalysisReportExport or AgentCapability.HostHousekeeping;
        }

        internal bool Allows(AgentOperationDescriptor operation) =>
            operation != null && operation.Requirements.All(Allows) &&
            (Actions == AgentActionAccess.FullControl || (operation.Effects &
                (AgentOperationEffects.NetworkConfiguration | AgentOperationEffects.ConsoleRestart |
                AgentOperationEffects.DataDeletion | AgentOperationEffects.TrustedCodeExecution)) == 0);

        internal AgentAccessSettings RestrictTo(AgentAccessSettings other) => this with
        {
            Enabled = Enabled && other.Enabled,
            DataSharing = DataSharing == AgentDataSharing.Redacted || other.DataSharing == AgentDataSharing.Redacted
                ? AgentDataSharing.Redacted : AgentDataSharing.Complete,
            Actions = Actions == AgentActionAccess.InspectAndExport || other.Actions == AgentActionAccess.InspectAndExport
                ? AgentActionAccess.InspectAndExport : AgentActionAccess.FullControl,
            AutomaticBackups = AutomaticBackups || other.AutomaticBackups
        };
    }

    internal enum AgentPolicyReadStatus { Missing, Loaded, Invalid }
    internal enum AgentPolicyFailure { InvalidPolicy, PolicyChanged, SaveFailed, Busy }

    internal sealed record AgentAccessSettingsRead(AgentAccessSettings Settings, AgentPolicyReadStatus Status);

    internal sealed class AgentPolicyException(AgentPolicyFailure failure) : InvalidOperationException(failure.ToString())
    {
        internal AgentPolicyFailure Failure { get; } = failure;
    }
}
