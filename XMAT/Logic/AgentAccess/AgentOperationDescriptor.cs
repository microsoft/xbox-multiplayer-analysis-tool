// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentOperationDescriptor
    {
        private const AgentOperationEffects SensitiveEffects =
            AgentOperationEffects.NetworkConfiguration |
            AgentOperationEffects.ConsoleRestart |
            AgentOperationEffects.DataDeletion |
            AgentOperationEffects.FileOverwrite |
            AgentOperationEffects.TrustedCodeExecution |
            AgentOperationEffects.SensitiveDataExport |
            AgentOperationEffects.HumanOwnedResource;

        internal string Action { get; }
        internal string Fingerprint { get; }
        internal string ArgumentsHash { get; }
        internal IReadOnlyList<AgentPermission> Requirements { get; }
        internal AgentOperationEffects Effects { get; }
        internal string ApprovalDetails { get; }
        internal bool RequiresApproval =>
            (Effects & SensitiveEffects) != 0 ||
            Requirements.Any(permission => permission.Capability is
                AgentCapability.ScriptExecute or AgentCapability.DeviceConfiguration or
                AgentCapability.DeviceCreation or AgentCapability.Lifecycle or AgentCapability.FileWrite or
                AgentCapability.CaptureLoad or AgentCapability.CaptureImport);

        internal AgentOperationDescriptor(
            string action,
            IEnumerable<AgentPermission> requirements,
            AgentOperationEffects effects,
            string argumentsHash,
            string approvalDetails = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(action);
            ArgumentException.ThrowIfNullOrWhiteSpace(argumentsHash);
            ArgumentNullException.ThrowIfNull(requirements);
            if (action.Length > 80 ||
                action.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_') ||
                (effects & ~(SensitiveEffects | AgentOperationEffects.StateChange)) != 0)
            {
                throw new ArgumentException("Invalid operation descriptor.", nameof(action));
            }
            var permissions = requirements.Distinct().ToArray();
            if (permissions.Length == 0 || permissions.Any(permission => permission == null))
            {
                throw new ArgumentException("Operations require explicit permissions.", nameof(requirements));
            }
            if (permissions.Any(permission => permission.Capability == AgentCapability.ScriptExecute))
            {
                effects |= AgentOperationEffects.TrustedCodeExecution | AgentOperationEffects.StateChange;
            }
            if (permissions.Any(permission => permission.Capability == AgentCapability.DeviceConfiguration))
            {
                effects |= AgentOperationEffects.NetworkConfiguration | AgentOperationEffects.StateChange;
            }
            if (permissions.Any(permission => permission.Capability == AgentCapability.Lifecycle))
            {
                effects |= AgentOperationEffects.StateChange;
            }
            if (permissions.Any(permission => permission.Capability == AgentCapability.DeviceCreation))
            {
                effects |= AgentOperationEffects.StateChange;
            }
            if (effects != AgentOperationEffects.None && permissions.All(permission =>
                permission.Capability is AgentCapability.SummaryRead or AgentCapability.RawDataRead or
                AgentCapability.ScriptRead or AgentCapability.FileRead))
            {
                throw new ArgumentException("A state-changing operation requires a control capability.", nameof(requirements));
            }
            Action = action;
            Requirements = Array.AsReadOnly(permissions);
            Effects = effects;
            ApprovalDetails = approvalDetails;
            ArgumentsHash = argumentsHash;
            Fingerprint = Hash(new { action, permissions, effects, argumentsHash });
        }

        internal static string Hash<T>(T arguments) =>
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(arguments)));

        private AgentOperationDescriptor(AgentOperationDescriptor source)
        {
            Action = source.Action;
            Fingerprint = source.Fingerprint;
            ArgumentsHash = source.ArgumentsHash;
            Requirements = source.Requirements;
            Effects = source.Effects;
        }

        internal AgentOperationDescriptor WithoutApprovalDetails() => new(this);

        public override string ToString() => Action;
    }
}
