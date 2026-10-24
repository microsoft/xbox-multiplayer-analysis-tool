// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XMAT.WebServiceCapture
{
    // The per-user WinINet connection settings that a Local PC capture changes and recovery restores.
    internal sealed record ProxySettings(int Flags, string Server, string Bypass, string AutoConfigUrl)
    {
        internal const int Direct = 0x1;
        internal const int Proxy = 0x2;

        internal bool UsesProxy => (Flags & Proxy) != 0;

        internal ProxySettings WithProxy(ProxyEndpoint endpoint) =>
            this with { Flags = Proxy, Server = endpoint.ToString() };

        internal ProxySettings WithoutProxy() => this with { Flags = (Flags & ~Proxy) | Direct };

        internal ProxySettings WithoutProxyTo(string server) => ProxiesTo(server) ? WithoutProxy() : this;

        internal bool ProxiesTo(string server) => ProxyEndpoint.TryParse(server, out var endpoint) && ProxiesTo(endpoint);

        // Whether traffic goes to `endpoint`: the proxy is on and its server is that same host and port.
        internal bool ProxiesTo(ProxyEndpoint endpoint) =>
            UsesProxy && ProxyEndpoint.TryParse(Server, out var current) && current.Matches(endpoint);

        // Settings read back as the same as these: identical flags, since each flag is a distinct connection type,
        // and identical strings, except that an empty string may come back as missing.
        internal bool IsEquivalentTo(ProxySettings other) =>
            other != null && Flags == other.Flags &&
            SameText(Server, other.Server) && SameText(Bypass, other.Bypass) &&
            SameText(AutoConfigUrl, other.AutoConfigUrl);

        private static bool SameText(string first, string second) =>
            string.IsNullOrEmpty(first) ? string.IsNullOrEmpty(second) : string.Equals(first, second, StringComparison.Ordinal);
    }

    internal interface IProxySettingsStore
    {
        ProxySettings Read();
        void Write(ProxySettings settings);
    }

    internal sealed class WinInetProxySettingsStore : IProxySettingsStore
    {
        internal static readonly WinInetProxySettingsStore Instance = new();

        public ProxySettings Read() => InternetProxy.ReadSettings();

        public void Write(ProxySettings settings) => InternetProxy.WriteSettings(settings);
    }

    // Keeps the settings in a file, so tests and the guards they start never write WinINet. Tests derive
    // from it to script reads and writes; the guard is still given the same file.
    internal class FileProxySettingsStore : IProxySettingsStore
    {
        internal FileProxySettingsStore(string path) => FilePath = path;

        internal string FilePath { get; }

        public virtual ProxySettings Read() => JsonSerializer.Deserialize<ProxySettings>(SharedFile.ReadAllText(FilePath));

        public virtual void Write(ProxySettings settings) => SharedFile.Replace(FilePath, JsonSerializer.Serialize(settings));
    }

    internal static class SharedFile
    {
        // Other XMAT processes may replace or delete the file while it is being read.
        internal static string ReadAllText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        // Readers see either the old or the new content, never a partially written file.
        internal static void Replace(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    var bytes = Encoding.UTF8.GetBytes(contents);
                    stream.Write(bytes, 0, bytes.Length);
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        internal static void Delete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
