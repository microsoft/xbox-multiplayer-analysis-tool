// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XMAT.WebServiceCapture
{
    internal sealed record XboxProxySession(int OwnerProcessId, long OwnerCreationTime, string[] Consoles);

    // Records the consoles this XMAT is capturing, so the next start can offer to disable their proxy
    // if XMAT ends without shutting down. Consoles are never contacted automatically.
    internal sealed class XboxProxySessions
    {
        private readonly string _path;
        private readonly ProcessIdentity _owner;
        private readonly SortedSet<string> _consoles = new(StringComparer.OrdinalIgnoreCase);

        internal XboxProxySessions(string directory, ProcessIdentity owner)
        {
            _owner = owner;
            _path = Path.Combine(directory, $"xbox-{owner.ProcessId}-{owner.CreationTime}.json");
        }

        internal void SetCapturing(string console, bool capturing)
        {
            lock (_consoles)
            {
                if (capturing ? _consoles.Add(console) : _consoles.Remove(console))
                {
                    Persist();
                }
            }
        }

        // A normal exit leaves each console's proxy as the user chose when closing.
        internal void Clear()
        {
            lock (_consoles)
            {
                _consoles.Clear();
                Persist();
            }
        }

        private void Persist()
        {
            try
            {
                if (_consoles.Count == 0)
                {
                    SharedFile.Delete(_path);
                }
                else
                {
                    SharedFile.Replace(_path, JsonSerializer.Serialize(
                        new XboxProxySession(_owner.ProcessId, _owner.CreationTime, _consoles.ToArray())));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Could not record the Xbox capture state: {ex}");
            }
        }

        // Consoles that an XMAT, which ended without shutting down, was capturing. The records stay until
        // Acknowledge is called once the offer to disable those proxies has been answered, so a start that
        // ends before then offers them again.
        internal static AbandonedXboxSessions FindAbandoned(string directory)
        {
            var consoles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            if (!Directory.Exists(directory))
            {
                return new AbandonedXboxSessions(consoles.ToArray(), files);
            }
            foreach (var file in Directory.EnumerateFiles(directory, "xbox-*.json"))
            {
                XboxProxySession session;
                try
                {
                    session = JsonSerializer.Deserialize<XboxProxySession>(SharedFile.ReadAllText(file));
                }
                catch (JsonException)
                {
                    session = null;
                }
                catch (IOException)
                {
                    continue;
                }
                if (session != null &&
                    new ProcessIdentity(session.OwnerProcessId, session.OwnerCreationTime).GetState() != ProcessState.Dead)
                {
                    continue;
                }
                if (session?.Consoles is { Length: > 0 })
                {
                    consoles.UnionWith(session.Consoles);
                    files.Add(file);
                }
                else
                {
                    // Nothing to offer from an unreadable or empty record.
                    SharedFile.Delete(file);
                }
            }
            return new AbandonedXboxSessions(consoles.ToArray(), files);
        }

        internal static void Acknowledge(AbandonedXboxSessions sessions)
        {
            foreach (var file in sessions.Files)
            {
                try
                {
                    SharedFile.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Could not remove the Xbox capture record {file}: {ex}");
                }
            }
        }
    }

    internal sealed record AbandonedXboxSessions(IReadOnlyList<string> Consoles, IReadOnlyList<string> Files);
}
