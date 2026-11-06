// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace XMAT.Tests.Echo
{
    // Stands in for curl.exe in tests. It reads xmat-echo.json from its working folder, which the test writes:
    //   record   - a file that it appends one JSON line to for each run (required);
    //   version  - the version that it answers curl's probe (-q --version) with, 8.21.0 by default;
    //   unicode  - whether that answer lists the Unicode feature, true by default;
    //   mode     - "read" (the default) reads stdin to its end and exits 0; "hold" closes its stdin without reading and
    //              keeps running until it is stopped.
    // A record holds its kind (probe or send), process id, arguments as .NET read them, arguments as the Windows
    // command-line parser reads them, the raw command line, the names of its environment variables with the values of
    // the system folders only (RecordedValues), and for a send, the stdin bytes' length and SHA-256.
    internal static class Program
    {
        private const int StdInputHandle = -10;

        // The only environment values that a record keeps, which tests compare. Any other value, such as a secret in the
        // environment of whoever runs the tests, is recorded as null: tests check which variables a child got, not what
        // they held.
        private static readonly HashSet<string> RecordedValues = new(StringComparer.OrdinalIgnoreCase)
        {
            "SystemRoot", "windir", "SystemDrive",
        };

        private static int Main(string[] args)
        {
            using JsonDocument config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "xmat-echo.json")));
            JsonElement root = config.RootElement;
            string record = root.GetProperty("record").GetString();
            string version = root.TryGetProperty("version", out JsonElement versionValue) ? versionValue.GetString() : "8.21.0";
            bool unicode = !root.TryGetProperty("unicode", out JsonElement unicodeValue) || unicodeValue.GetBoolean();
            string mode = root.TryGetProperty("mode", out JsonElement modeValue) ? modeValue.GetString() : "read";
            bool probe = args.Length == 2 && args[0] == "-q" && args[1] == "--version";

            var entry = new Dictionary<string, object>
            {
                ["kind"] = probe ? "probe" : "send",
                ["pid"] = Environment.ProcessId,
                ["args"] = args,
                ["nativeArgs"] = NativeArguments(),
                ["commandLine"] = Marshal.PtrToStringUni(NativeMethods.GetCommandLineW()),
                ["environment"] = Environment.GetEnvironmentVariables()
                    .Cast<DictionaryEntry>()
                    .ToDictionary(
                        variable => (string)variable.Key,
                        variable => RecordedValues.Contains((string)variable.Key) ? (string)variable.Value : null,
                        StringComparer.OrdinalIgnoreCase),
            };

            if (probe)
            {
                Append(record, entry);
                Console.Out.Write(
                    $"curl {version} (Windows) xmat-echo\r\n" +
                    "Release-Date: 2026-01-01\r\n" +
                    "Protocols: http https\r\n" +
                    "Features: AsynchDNS IPv6 Largefile SSL" + (unicode ? " Unicode" : string.Empty) + "\r\n");
                return 0;
            }

            if (mode == "hold")
            {
                Append(record, entry);
                NativeMethods.CloseHandle(NativeMethods.GetStdHandle(StdInputHandle));
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }

            using var input = new MemoryStream();
            using (Stream stdin = Console.OpenStandardInput())
            {
                stdin.CopyTo(input);
            }

            entry["stdinBytes"] = input.Length;
            entry["stdinSha256"] = Convert.ToHexString(SHA256.HashData(input.ToArray()));
            Append(record, entry);
            return 0;
        }

        private static string[] NativeArguments()
        {
            IntPtr list = NativeMethods.CommandLineToArgvW(NativeMethods.GetCommandLineW(), out int count);
            try
            {
                return Enumerable.Range(1, Math.Max(0, count - 1))
                    .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, index * IntPtr.Size)))
                    .ToArray();
            }
            finally
            {
                NativeMethods.LocalFree(list);
            }
        }

        private static void Append(string path, Dictionary<string, object> entry)
        {
            string line = JsonSerializer.Serialize(entry) + "\n";
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.AppendAllText(path, line);
                    return;
                }
                catch (IOException) when (attempt < 50)
                {
                    Thread.Sleep(20);
                }
            }
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", ExactSpelling = true)]
            internal static extern IntPtr GetCommandLineW();

            [DllImport("shell32.dll", ExactSpelling = true, SetLastError = true)]
            internal static extern IntPtr CommandLineToArgvW(IntPtr commandLine, out int count);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            internal static extern IntPtr LocalFree(IntPtr memory);

            [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
            internal static extern IntPtr GetStdHandle(int handle);

            [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr handle);
        }
    }
}
