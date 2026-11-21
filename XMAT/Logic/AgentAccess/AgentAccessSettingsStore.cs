// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using XMAT.ApplicationServices;

namespace XMAT.AgentAccess
{
    internal sealed class AgentAccessSettingsStore
    {
        private const int MaximumBytes = 64 * 1024;
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        private readonly string _mutexName;

        internal static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Xbox Multiplayer Analysis Tool", "agent-access.json");

        internal string Path { get; }

        internal AgentAccessSettingsStore(string path = null)
        {
            Path = System.IO.Path.GetFullPath(path ?? DefaultPath);
            _mutexName = @"Local\XMAT.AgentPolicy." +
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.ToUpperInvariant())))[..24];
        }

        internal AgentAccessSettingsRead Read()
        {
            try
            {
                using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (file.Length > MaximumBytes)
                    return Invalid();
                var settings = JsonSerializer.Deserialize<AgentAccessSettings>(file, JsonOptions);
                return settings?.IsValid == true
                    ? new(settings, AgentPolicyReadStatus.Loaded) : Invalid();
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(new(), AgentPolicyReadStatus.Missing);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                SecurityException or JsonException or NotSupportedException)
            {
                return Invalid();
            }
        }

        internal Task<AgentAccessSettings> SaveAsync(
            AgentAccessSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (settings?.IsValid != true || expectedRevision < 0 || expectedRevision == long.MaxValue)
                throw new AgentPolicyException(AgentPolicyFailure.InvalidPolicy);
            return Task.Run(() =>
            {
                using var mutex = new Mutex(false, _mutexName);
                var acquired = false;
                try
                {
                    try
                    {
                        var wait = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], TimeSpan.FromSeconds(10));
                        acquired = wait == 0;
                        if (!acquired)
                            cancellationToken.ThrowIfCancellationRequested();
                        if (wait != 0)
                            throw new AgentPolicyException(AgentPolicyFailure.Busy);
                    }
                    catch (AbandonedMutexException) { acquired = true; }
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = Read().Settings;
                    if (current.Revision != expectedRevision)
                        throw new AgentPolicyException(AgentPolicyFailure.PolicyChanged);
                    var committed = settings with { Revision = checked(expectedRevision + 1) };
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    var json = JsonSerializer.Serialize(committed, JsonOptions);
                    cancellationToken.ThrowIfCancellationRequested();
                    AtomicSettingsFile.Write(Path, json);
                    return committed;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
                {
                    throw new AgentPolicyException(AgentPolicyFailure.SaveFailed);
                }
                finally
                {
                    if (acquired)
                        mutex.ReleaseMutex();
                }
            }, cancellationToken);
        }

        private static AgentAccessSettingsRead Invalid() => new(new(), AgentPolicyReadStatus.Invalid);

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                MaxDepth = 8,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            };
            options.Converters.Add(new JsonStringEnumConverter<AgentDataSharing>(allowIntegerValues: false));
            options.Converters.Add(new JsonStringEnumConverter<AgentActionAccess>(allowIntegerValues: false));
            return options;
        }
    }
}
