// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XMAT.AgentConnection
{
    internal sealed record AgentInstanceRecord(
        int Version, Guid InstanceId, int ProcessId, long StartedUtcTicks,
        string UserSid, bool Elevated, string Mode, string State, bool Enabled)
    {
        internal static AgentInstanceRecord ForCurrentProcess(Guid instanceId, string mode, string state, bool enabled)
        {
            using var process = Process.GetCurrentProcess();
            var user = AgentProcessIdentity.Current();
            return new(1, instanceId, process.Id, process.StartTime.ToUniversalTime().Ticks,
                user.UserSid, user.Elevated, mode, state, enabled);
        }

        internal string PipeName => $"XMAT.Agent.{InstanceId:N}";
    }

    internal enum AgentDiscoveryFailure { None, NotRunning, AccessDisabled, NotReady, Ambiguous, WrongElevation, LimitExceeded }
    internal sealed record AgentInstanceSelection(AgentInstanceRecord Instance, AgentDiscoveryFailure Failure);

    internal sealed class AgentInstanceDirectory
    {
        private const int MaximumRecords = 64;
        private const int MaximumRecordBytes = 16 * 1024;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            MaxDepth = 8,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        private readonly string _directory;
        private readonly string _trustedExecutable;

        internal static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Xbox Multiplayer Analysis Tool", "AgentAccess", "instances");

        internal AgentInstanceDirectory(string trustedExecutable, string directory = null)
        {
            _trustedExecutable = Path.GetFullPath(trustedExecutable);
            _directory = Path.GetFullPath(directory ?? DefaultDirectory);
        }

        internal IReadOnlyList<AgentInstanceRecord> Read()
        {
            if (!Directory.Exists(_directory))
                return Array.Empty<AgentInstanceRecord>();
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("XMAT_AGENT_INSTANCE_DIRECTORY_INVALID");
            var files = Directory.EnumerateFiles(_directory, "*.json").Take(MaximumRecords + 1).ToArray();
            if (files.Length > MaximumRecords)
                throw new IOException("XMAT_AGENT_INSTANCE_LIMIT");
            var current = AgentProcessIdentity.Current();
            var records = new List<AgentInstanceRecord>();
            foreach (var path in files)
            {
                var record = ReadRecord(path);
                if (record != null && record.UserSid == current.UserSid && IsIntendedProcess(record))
                    records.Add(record);
            }
            return records;
        }

        internal AgentInstanceSelection Select(Guid? instanceId = null)
        {
            IReadOnlyList<AgentInstanceRecord> records;
            try
            { records = Read(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { return new(null, AgentDiscoveryFailure.LimitExceeded); }
            var current = AgentProcessIdentity.Current();
            var requested = records.Where(record => !instanceId.HasValue || record.InstanceId == instanceId).ToArray();
            var sameElevation = requested.Where(record => record.Elevated == current.Elevated).ToArray();
            var ready = sameElevation.Where(record => record.Enabled && record.State == "ready").ToArray();
            if (ready.Length == 1)
                return new(ready[0], AgentDiscoveryFailure.None);
            if (ready.Length > 1)
                return new(null, AgentDiscoveryFailure.Ambiguous);
            if (sameElevation.Length > 0)
                return new(null, sameElevation.All(record => !record.Enabled)
                    ? AgentDiscoveryFailure.AccessDisabled : AgentDiscoveryFailure.NotReady);
            return new(null, requested.Length > 0 ? AgentDiscoveryFailure.WrongElevation : AgentDiscoveryFailure.NotRunning);
        }

        internal void Publish(AgentInstanceRecord record)
        {
            if (!IsValid(record) || record.ProcessId != Environment.ProcessId ||
                record.UserSid != AgentProcessIdentity.Current().UserSid)
                throw new ArgumentException("XMAT_AGENT_INSTANCE_INVALID", nameof(record));
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("XMAT_AGENT_INSTANCE_DIRECTORY_INVALID");
            PruneExited();
            var path = Path.Combine(_directory, record.InstanceId.ToString("N") + ".json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions), new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }

        internal void PublishStarting(AgentInstanceRecord record)
        {
            if (!IsValid(record) || record.State != "starting" || !IsIntendedProcess(record) ||
                new AgentProcessIdentity(record.UserSid, record.Elevated) != AgentProcessIdentity.Current())
                throw new IOException("XMAT_AGENT_INSTANCE_INVALID");
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("XMAT_AGENT_INSTANCE_DIRECTORY_INVALID");
            PruneExited();
            var path = Path.Combine(_directory, record.InstanceId.ToString("N") + ".json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions), new UTF8Encoding(false));
                try
                { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { File.Delete(temporary); }
        }

        internal void Remove(AgentInstanceRecord record)
        {
            var path = Path.Combine(_directory, record.InstanceId.ToString("N") + ".json");
            var current = ReadRecord(path);
            if (current?.ProcessId == record.ProcessId && current.StartedUtcTicks == record.StartedUtcTicks)
                File.Delete(path);
        }

        private AgentInstanceRecord ReadRecord(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return null;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (file.Length > MaximumRecordBytes)
                    return null;
                var record = JsonSerializer.Deserialize<AgentInstanceRecord>(file, JsonOptions);
                return IsValid(record) && Path.GetFileName(path) == record.InstanceId.ToString("N") + ".json" ? record : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { return null; }
        }

        private bool IsIntendedProcess(AgentInstanceRecord record)
        {
            try
            {
                using var process = Process.GetProcessById(record.ProcessId);
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.StartedUtcTicks &&
                    AgentProcessIdentity.ExecutablePath((uint)record.ProcessId) is { } executable &&
                    string.Equals(Path.GetFullPath(executable), _trustedExecutable,
                        StringComparison.OrdinalIgnoreCase) &&
                    AgentProcessIdentity.Read((uint)record.ProcessId) ==
                        new AgentProcessIdentity(record.UserSid, record.Elevated);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                Win32Exception or IOException or UnauthorizedAccessException)
            { return false; }
        }

        private void PruneExited()
        {
            var user = AgentProcessIdentity.Current().UserSid;
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Take(256))
            {
                var record = ReadRecord(path);
                if (record?.UserSid != user)
                    continue;
                var exited = false;
                try
                {
                    using var process = Process.GetProcessById(record.ProcessId);
                    exited = process.HasExited || process.StartTime.ToUniversalTime().Ticks != record.StartedUtcTicks;
                }
                catch (ArgumentException) { exited = true; }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
                if (exited)
                {
                    try
                    { File.Delete(path); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        private static bool IsValid(AgentInstanceRecord record) =>
            record != null && record.Version == 1 && record.InstanceId != Guid.Empty &&
            record.ProcessId > 0 && record.StartedUtcTicks > 0 &&
            !string.IsNullOrEmpty(record.UserSid) && record.UserSid.Length <= 256 &&
            record.Mode is "desktop" or "headless" &&
            record.State is "starting" or "ready" or "disabled" or "stopping" or "failed";
    }
}
