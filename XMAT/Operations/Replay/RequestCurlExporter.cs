// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // Why a request was not exported as a cURL script; nothing was written.
    internal static class CurlExportCodes
    {
        internal const string ConsentRequired = "replay.curl.consent-required";
        internal const string Unsupported = "replay.curl.unsupported";
        internal const string HeadBody = "replay.curl.head-body";
        internal const string ArgumentsTooLarge = "replay.curl.arguments-too-large";
    }

    // What curl is not known to send the way XMAT does, so an export refuses it rather than leave it out. Raw is a raw
    // request: curl builds a request of its own, never these prepared bytes, so Raw mode offers no cURL export.
    internal enum CurlUnsupported
    {
        Http2Exact,
        ConnectTo,
        Redirects,
        NumberedUrl,
        Proxy,
        ClientCertificate,
        WindowsAuth,
        Raw,
    }

    internal sealed class CurlExportException : Exception
    {
        internal CurlExportException(string code, string message, CurlUnsupported? reason = null)
            : base(message)
        {
            Code = code;
            Reason = reason;
        }

        internal string Code { get; }

        internal CurlUnsupported? Reason { get; }
    }

    // One request frozen for export: curl.exe's arguments, after the executable, and the body, made once. Body is the
    // draft's own bytes, never copied: nothing may change them.
    internal sealed class CurlRequest
    {
        internal CurlRequest(IReadOnlyList<string> arguments, byte[] body)
        {
            Arguments = arguments;
            Body = body;
            NeedsUnicodeCurl = arguments.Any(argument => argument.Any(character => character > '\u007f'));
            CommandLineBound = RequestCurlExporter.BoundOf(arguments);
        }

        internal IReadOnlyList<string> Arguments { get; }

        internal ReadOnlyMemory<byte> Body { get; }

        // An argument has a character that is not ASCII, which only a curl.exe with its Unicode feature passes unchanged.
        internal bool NeedsUnicodeCurl { get; }

        // The most UTF-16 units that the arguments take on a Windows command line, quoted (BoundOf).
        internal long CommandLineBound { get; }
    }

    internal enum CurlScriptForm
    {
        // A .ps1 file: #Requires PowerShell 7.2, ends with exit and curl's exit code.
        File,

        // A block to paste into PowerShell: it never exits its host and leaves the result in $LASTEXITCODE.
        Pasteable,
    }

    // For tests only: the script runs this program instead of the curl.exe of Windows.
    internal sealed record CurlScriptTestHooks(string CurlPath);

    // The one way XMAT writes a request as a script that sends it with curl.exe, for the GUI and xmat.exe alike. A script
    // is PowerShell 7 for Windows and runs the curl.exe of Windows by its full path, with arguments that make curl send
    // what XMAT's sender sends: the same request line, Host, header lines in the same order and body bytes over HTTP/1.1.
    // Exporting sends nothing and starts nothing: the user runs the script. Whatever a request holds appears in a script
    // only as a quoted literal or Base64; every other line is fixed text.
    internal static class RequestCurlExporter
    {
        // A pasteable command holds at most this many body bytes, text or not; Save as cURL script takes the rest.
        internal const int MaxPasteableBodyBytes = 64 * 1024;

        // A Windows command line holds 32,767 UTF-16 units with its terminator. An export leaves room for a quoted
        // curl.exe path of up to 260 units; the script checks again with the path that it runs.
        internal const int MaxCommandLine = 32766;
        internal const int ReservedExecutablePath = 260;
        internal const long MaxArgumentsBound = MaxCommandLine - (2L * ReservedExecutablePath + 3);

        internal const string MinimumCurlVersion = "8.21.0";
        internal const string MinimumPowerShellVersion = "7.2";

        // What both of a script's curl.exe processes get of its environment: only what Windows itself needs, never a
        // proxy, curl, TLS or key log setting.
        internal static IReadOnlyList<string> ChildEnvironment { get; } =
            new[] { "SystemRoot", "windir", "SystemDrive", "TEMP", "TMP" };

        // The headers that curl adds of its own, which XMAT never sends unless the request has them.
        private static readonly string[] CurlDefaults = { "Accept", "User-Agent", "Content-Type", "Expect" };

        // Base64 lines of 64 KiB: 48 KiB of body each, whole 3-byte groups, so each line decodes alone.
        private const int Base64ChunkBytes = 48 * 1024;

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        private static readonly Regex SidecarSegment = new("^(\\.\\.|[A-Za-z0-9_-][A-Za-z0-9._-]*)$", RegexOptions.CultureInvariant);

        // The request as curl.exe's arguments and its body, or why it cannot be exported. In this order: a part that a
        // request file left out (RequestIncompleteException); a scoped IPv6 destination; the request file limits on the
        // method, URL, headers and body (FormatException); a Fiddler control header; a transport that curl is not known to
        // send the same way, or a numbered URL; HEAD with a body; a header value that is not well-formed Unicode
        // (FormatException); arguments beyond the command line. Only then is the body made, once. An invalid draft throws
        // as CreateRequest does.
        internal static CurlRequest Prepare(
            RequestReplayDraft draft,
            ReplayTransportOptions transport,
            int followRedirects,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(draft);
            transport ??= new ReplayTransportOptions();
            cancellationToken.ThrowIfCancellationRequested();
            CheckDestination(draft);

            draft.ValidateStructure();
            if (ReplayControlHeaders.Has(draft))
            {
                throw new CurlExportException(
                    ReplayControlHeaders.Code,
                    "The request has a Fiddler control header (Fiddler-Host, X-OverrideHost, X-IgnoreCertCNMismatch or " +
                        "X-OverrideGateway), which XMAT does not act on; remove it to export the request.");
            }

            if (transport.HttpVersion == ReplayHttpVersion.Http2)
            {
                throw Unsupported(CurlUnsupported.Http2Exact, "HTTP/2 only");
            }

            if (transport.ConnectTo != null)
            {
                throw Unsupported(CurlUnsupported.ConnectTo, "a connection to another endpoint");
            }

            if (transport.Proxy != null)
            {
                throw Unsupported(CurlUnsupported.Proxy, "a proxy");
            }

            if (transport.ClientCertificate != null)
            {
                throw Unsupported(CurlUnsupported.ClientCertificate, "a client certificate");
            }

            if (transport.WindowsAuth)
            {
                throw Unsupported(CurlUnsupported.WindowsAuth, "Windows authentication");
            }

            if (followRedirects > 0)
            {
                throw Unsupported(CurlUnsupported.Redirects, "following redirects");
            }

            if ((draft.Url ?? string.Empty).Contains(ReplaySequence.Placeholder, StringComparison.Ordinal))
            {
                throw Unsupported(CurlUnsupported.NumberedUrl, "a numbered URL");
            }

            long bodyLength = draft.GetBodyLength();
            if (bodyLength > 0 && IsHead(draft.Method))
            {
                throw new CurlExportException(
                    CurlExportCodes.HeadBody, "A HEAD request with a body cannot be exported as a cURL script.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            List<string> arguments;
            using (HttpRequestMessage request = draft.CreateRequest(Array.Empty<byte>()))
            {
                arguments = Arguments(request, transport.HttpVersion, bodyLength == 0);
            }

            foreach (string argument in arguments)
            {
                try
                {
                    _ = StrictUtf8.GetByteCount(argument);
                }
                catch (EncoderFallbackException ex)
                {
                    throw new FormatException("A header value is not well-formed Unicode.", ex);
                }
            }

            if (BoundOf(arguments) > MaxArgumentsBound)
            {
                throw new CurlExportException(
                    CurlExportCodes.ArgumentsTooLarge,
                    "The request's URL and headers are too large for a Windows command line, so curl.exe cannot be given " +
                        "them.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new CurlRequest(arguments, draft.GetBodyBytes());
        }

        // Shared by the GUI's pre-confirmation check and Prepare: request-file omissions, then the destination, before
        // either can produce executable cURL text.
        internal static void CheckDestination(RequestReplayDraft draft)
        {
            ArgumentNullException.ThrowIfNull(draft);
            if (draft.Omissions.Any)
            {
                throw new RequestIncompleteException();
            }

            if (ReplayDestination.IsScopedIpv6(draft.Url))
            {
                throw new CurlExportException(ReplayDestination.ScopedIpv6Code, ReplayDestination.ScopedIpv6Message);
            }
        }

        internal static void Write(
            CurlRequest request,
            CurlScriptForm form,
            string bodySidecar,
            Stream destination,
            CancellationToken cancellationToken) =>
            Write(request, form, bodySidecar, destination, null, cancellationToken);

        // Writes the script in bounded pieces. bodySidecar: null for a body that the script holds as Base64; otherwise the
        // path of the body file relative to the script's folder, such as ../bodies/00001.bin, which the caller made and
        // writes from the same bytes. A pasteable command always holds its body, of at most MaxPasteableBodyBytes.
        internal static void Write(
            CurlRequest request,
            CurlScriptForm form,
            string bodySidecar,
            Stream destination,
            CurlScriptTestHooks hooks,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(destination);
            bool pasteable = form == CurlScriptForm.Pasteable;
            if (pasteable && bodySidecar != null)
            {
                throw new ArgumentException("A pasteable command holds its body.", nameof(bodySidecar));
            }

            if (pasteable && request.Body.Length > MaxPasteableBodyBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request), "A pasteable command holds a body of at most 64 KiB.");
            }

            if (bodySidecar != null && (request.Body.IsEmpty || !IsSidecarPath(bodySidecar)))
            {
                throw new ArgumentException(
                    "A body file is a relative path of plain names, for a body that is not empty.", nameof(bodySidecar));
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var writer = new StreamWriter(destination, pasteable ? StrictUtf8 : Utf8WithBom, 64 * 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
            };
            var script = new ScriptWriter(writer, cancellationToken);
            if (pasteable)
            {
                WritePasteable(script, request, hooks);
            }
            else
            {
                WriteFile(script, request, bodySidecar, hooks);
            }

            writer.Flush();
        }

        // The most UTF-16 units that these arguments take on a Windows command line: quoted for ArgumentList, each
        // quote and each backslash at most doubles, plus two quotes and a space.
        internal static long BoundOf(IEnumerable<string> arguments) =>
            arguments.Sum(argument => (2L * argument.Length) + 3);

        // A PowerShell single-quoted literal: a quote, including the curly single quotes that PowerShell also takes as
        // quotes, is doubled. Nothing else in such a literal is special.
        internal static string Literal(string value) =>
            "'" + value.Replace("'", "''", StringComparison.Ordinal)
                .Replace("\u2018", "\u2018\u2018", StringComparison.Ordinal)
                .Replace("\u2019", "\u2019\u2019", StringComparison.Ordinal)
                .Replace("\u201a", "\u201a\u201a", StringComparison.Ordinal)
                .Replace("\u201b", "\u201b\u201b", StringComparison.Ordinal) + "'";

        private static CurlExportException Unsupported(CurlUnsupported reason, string what) =>
            new(
                CurlExportCodes.Unsupported,
                $"A cURL script is not available with {what}: curl is not known to send the same request.",
                reason);

        // curl.exe's arguments, in this order: no configuration file (-q, first), no globbing, no proxy, HTTP and HTTPS
        // only, the path as it is, no progress meter; HTTP/1.1 when it is asked for exactly; the method; the URL as the
        // request has it; each header, as XMAT writes it, request headers then content headers, repeated names joined;
        // Content-Length: 0 for an empty body, which XMAT always sends; curl's own Accept, User-Agent, Content-Type and
        // Expect removed unless the request has them; the body from stdin.
        private static List<string> Arguments(HttpRequestMessage request, ReplayHttpVersion version, bool emptyBody)
        {
            var arguments = new List<string>
            {
                "-q", "--globoff", "--noproxy", "*", "--proto", "=http,https", "--path-as-is", "--silent", "--show-error",
            };
            if (version == ReplayHttpVersion.Http11)
            {
                arguments.Add("--http1.1");
            }

            // XMAT's sender writes a known method, in any case, as .NET spells it (patch as PATCH), and any other token as it
            // is. --request HEAD alone does not tell curl to stop reading after the response headers.
            string method = HttpMethod.Parse(request.Method.Method).Method;
            if (IsHead(method))
            {
                arguments.Add("--head");
            }
            else
            {
                arguments.Add("--request");
                arguments.Add(method);
            }

            arguments.Add("--url");
            arguments.Add(request.RequestUri.AbsoluteUri);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, HeaderStringValues> header in
                request.Headers.NonValidated.Concat(request.Content.Headers.NonValidated))
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                names.Add(header.Key);
                string value = header.Value.ToString();

                // "Name;" sends an empty value; "Name:" would remove the header instead.
                arguments.Add("--header");
                arguments.Add(value.Length == 0 ? header.Key + ";" : header.Key + ": " + value);
            }

            if (emptyBody)
            {
                arguments.Add("--header");
                arguments.Add("Content-Length: 0");
            }

            foreach (string name in CurlDefaults)
            {
                if (!names.Contains(name))
                {
                    arguments.Add("--header");
                    arguments.Add(name + ":");
                }
            }

            if (!emptyBody)
            {
                arguments.Add("--data-binary");
                arguments.Add("@-");
            }

            return arguments;
        }

        // ../bodies/00001.bin: relative, plain names separated by /, nothing that a request could have chosen.
        private static bool IsSidecarPath(string path) =>
            path.Length is > 0 and <= 260 && path.Split('/').All(segment => SidecarSegment.IsMatch(segment)) &&
            path.Split('/')[^1] != "..";

        // HEAD as XMAT's sender writes it: head in any case is HEAD.
        private static bool IsHead(string method) =>
            string.Equals(HttpMethod.Parse(method).Method, "HEAD", StringComparison.Ordinal);

        private static void WriteFile(ScriptWriter script, CurlRequest request, string bodySidecar, CurlScriptTestHooks hooks)
        {
            script.Line("#Requires -Version " + MinimumPowerShellVersion);
            script.Line("# Made by xmat (xmat-curl/1). Running it sends one HTTP request with the curl.exe of Windows, and");
            script.Line("# does nothing else. It holds the request's URL, headers and body, which can include credentials,");
            script.Line("# cookies and other secrets: keep it as private as the capture that it came from, and delete it");
            script.Line("# when it is no longer needed. It needs Windows, PowerShell " + MinimumPowerShellVersion +
                " or later and curl.exe " + MinimumCurlVersion + " or");
            script.Line("# later; otherwise it sends nothing.");

            // Until curl's result is known, the result is a failure: a run that is interrupted (Ctrl+C stops it after its
            // finally blocks) leaves 1, never an earlier result.
            script.Line("$global:LASTEXITCODE = 1");
            script.Line("Set-StrictMode -Version 3.0");
            script.Line("$ErrorActionPreference = 'Stop'");
            WriteRun(script, request, bodySidecar, hooks, indent: string.Empty);
            script.Line("exit $xmatExit");
        }

        private static void WritePasteable(ScriptWriter script, CurlRequest request, CurlScriptTestHooks hooks)
        {
            const string Indent = "    ";
            script.Line("& {");
            script.Line(Indent + "# Made by xmat: sends one HTTP request with the curl.exe of Windows. It holds the request's URL,");
            script.Line(Indent + "# headers and body, which can include credentials and cookies. It needs Windows, PowerShell " +
                MinimumPowerShellVersion);
            script.Line(Indent + "# or later and curl.exe " + MinimumCurlVersion +
                " or later; otherwise it sends nothing. Its result is in $LASTEXITCODE.");

            // As in a script file, the result is a failure until curl's is known, so an interrupted command leaves 1.
            script.Line(Indent + "$global:LASTEXITCODE = 1");
            script.Line(Indent + "$xmatVersion = $PSVersionTable.PSVersion");
            script.Line(Indent + "if ([version]('{0}.{1}' -f $xmatVersion.Major, $xmatVersion.Minor) -lt [version]'" +
                MinimumPowerShellVersion + "') {");
            script.Line(Indent + "    [Console]::Error.WriteLine('xmat: this command needs PowerShell " + MinimumPowerShellVersion +
                " or later; nothing was sent.')");
            script.Line(Indent + "    return");
            script.Line(Indent + "}");
            script.Line(Indent + "Set-StrictMode -Version 3.0");
            script.Line(Indent + "$ErrorActionPreference = 'Stop'");
            WriteRun(script, request, null, hooks, Indent);
            script.Line(Indent + "$global:LASTEXITCODE = $xmatExit");
            script.Line("}");
        }

        // The part that both forms share: checks, the body, the version probe and the send, in a try whose finally stops
        // and reaps every curl.exe of the script's own that is still running. $xmatExit is curl's exit code, or 1 when the
        // script refused or failed.
        private static void WriteRun(
            ScriptWriter script, CurlRequest request, string bodySidecar, CurlScriptTestHooks hooks, string indent)
        {
            string i1 = indent + "    ";
            string i2 = i1 + "    ";
            string i3 = i2 + "    ";
            script.Line(indent + "$xmatExit = 1");
            script.Line(indent + "$xmatOwned = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()");
            script.Line(indent + "try {");
            script.Line(i1 + "if (-not $IsWindows) { throw 'This script runs only on Windows; nothing was sent.' }");
            script.Line(i1 + "$xmatCurl = " + (hooks?.CurlPath is string path
                ? Literal(path)
                : "Join-Path ([System.Environment]::SystemDirectory) 'curl.exe'"));
            script.Line(i1 + "if (-not [System.IO.File]::Exists($xmatCurl)) {");
            script.Line(i2 + "throw 'The curl.exe of Windows was not found; nothing was sent.'");
            script.Line(i1 + "}");

            script.Line(i1 + "$xmatArguments = [string[]]@(");
            foreach (string argument in request.Arguments)
            {
                script.Line(i2 + Literal(argument));
            }

            script.Line(i1 + ")");

            // Each curl.exe gets only the environment that Windows needs: the script's own is never changed.
            script.Line(i1 + "function Start-XmatCurl([string[]] $XmatList, [bool] $XmatInput, [bool] $XmatCapture) {");
            script.Line(i2 + "$xmatStart = [System.Diagnostics.ProcessStartInfo]::new($xmatCurl)");
            script.Line(i2 + "$xmatStart.UseShellExecute = $false");
            script.Line(i2 + "foreach ($xmatArgument in $XmatList) { $xmatStart.ArgumentList.Add($xmatArgument) }");
            script.Line(i2 + "$xmatStart.Environment.Clear()");
            script.Line(i2 + "foreach ($xmatName in @(" + string.Join(", ", ChildEnvironment.Select(Literal)) + ")) {");
            script.Line(i3 + "$xmatValue = [System.Environment]::GetEnvironmentVariable($xmatName)");
            script.Line(i3 + "if ($null -ne $xmatValue) { $xmatStart.Environment[$xmatName] = $xmatValue }");
            script.Line(i2 + "}");
            script.Line(i2 + "$xmatStart.RedirectStandardInput = $XmatInput");
            script.Line(i2 + "$xmatStart.RedirectStandardOutput = $XmatCapture");
            script.Line(i2 + "$xmatStart.RedirectStandardError = $XmatCapture");

            // Started and owned in one expression, which an interruption cannot split: the finally finds every curl.exe
            // that has started.
            script.Line(i2 + "$xmatOwned.Add([System.Diagnostics.Process]::Start($xmatStart))");
            script.Line(i2 + "$xmatOwned[$xmatOwned.Count - 1]");
            script.Line(i1 + "}");

            WriteBody(script, request, bodySidecar, i1);

            script.Line(i1 + "$xmatBound = 2 * $xmatCurl.Length + 3");
            script.Line(i1 + "foreach ($xmatArgument in $xmatArguments) { $xmatBound += 2 * $xmatArgument.Length + 3 }");
            script.Line(i1 + "if ($xmatBound -gt " + MaxCommandLine.ToString(CultureInfo.InvariantCulture) + ") {");
            script.Line(i2 + "throw 'The request is too large for a Windows command line; nothing was sent.'");
            script.Line(i1 + "}");

            WriteProbe(script, request, i1);

            script.Line(i1 + "$xmatProcess = Start-XmatCurl $xmatArguments ($xmatBody.Length -gt 0) $false");
            script.Line(i1 + "if ($xmatBody.Length -gt 0) {");
            script.Line(i2 + "try { $xmatProcess.StandardInput.BaseStream.Write($xmatBody, 0, $xmatBody.Length) }");
            script.Line(i2 + "finally { $xmatProcess.StandardInput.Close() }");
            script.Line(i1 + "}");
            script.Line(i1 + "while (-not $xmatProcess.WaitForExit(200)) { }");
            script.Line(i1 + "$xmatExit = $xmatProcess.ExitCode");
            script.Line(indent + "}");
            script.Line(indent + "catch {");
            script.Line(i1 + "[Console]::Error.WriteLine('xmat: ' + $_.Exception.Message)");
            script.Line(i1 + "$xmatExit = 1");
            script.Line(indent + "}");
            script.Line(indent + "finally {");
            script.Line(i1 + "foreach ($xmatOwnedProcess in $xmatOwned) {");
            script.Line(i2 + "if (-not $xmatOwnedProcess.HasExited) {");
            script.Line(i3 + "try { $xmatOwnedProcess.Kill() } catch { }");
            script.Line(i3 + "$xmatOwnedProcess.WaitForExit()");
            script.Line(i2 + "}");
            script.Line(i2 + "$xmatOwnedProcess.Dispose()");
            script.Line(i1 + "}");
            script.Line(indent + "}");
        }

        // $xmatBody: none; the Base64 lines that the script holds, decoded once; or the body file next to the script, read
        // once and at most one byte past its length, and sent only as the bytes whose length and SHA-256 were checked.
        private static void WriteBody(ScriptWriter script, CurlRequest request, string bodySidecar, string indent)
        {
            string i1 = indent + "    ";
            ReadOnlySpan<byte> body = request.Body.Span;
            if (body.IsEmpty)
            {
                script.Line(indent + "$xmatBody = [byte[]]::new(0)");
                return;
            }

            if (bodySidecar == null)
            {
                script.Line(indent + "$xmatBodyStream = [System.IO.MemoryStream]::new()");
                script.Line(indent + "foreach ($xmatChunk in @(");
                for (int offset = 0; offset < body.Length; offset += Base64ChunkBytes)
                {
                    script.Line(i1 + "'" + Convert.ToBase64String(body.Slice(offset, Math.Min(Base64ChunkBytes, body.Length - offset))) + "'");
                }

                script.Line(indent + ")) {");
                script.Line(i1 + "$xmatBytes = [System.Convert]::FromBase64String($xmatChunk)");
                script.Line(i1 + "$xmatBodyStream.Write($xmatBytes, 0, $xmatBytes.Length)");
                script.Line(indent + "}");
                script.Line(indent + "$xmatBody = $xmatBodyStream.ToArray()");
                return;
            }

            string length = body.Length.ToString(CultureInfo.InvariantCulture);
            string sha256 = Convert.ToHexString(SHA256.HashData(body));
            script.Line(indent + "if ([string]::IsNullOrEmpty($PSScriptRoot)) {");
            script.Line(i1 + "throw 'Run this script as a file: its body file is next to it; nothing was sent.'");
            script.Line(indent + "}");
            script.Line(indent + "$xmatBodyPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Join($PSScriptRoot, " +
                Literal(bodySidecar) + "))");
            script.Line(indent + "$xmatBody = [byte[]]::new(" + length + ")");
            script.Line(indent + "$xmatRead = 0");
            script.Line(indent + "$xmatExtra = -1");
            script.Line(indent + "$xmatFile = [System.IO.File]::Open($xmatBodyPath, [System.IO.FileMode]::Open, " +
                "[System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)");
            script.Line(indent + "try {");
            script.Line(i1 + "while ($xmatRead -lt " + length + ") {");
            script.Line(i1 + "    $xmatCount = $xmatFile.Read($xmatBody, $xmatRead, " + length + " - $xmatRead)");
            script.Line(i1 + "    if ($xmatCount -le 0) { break }");
            script.Line(i1 + "    $xmatRead += $xmatCount");
            script.Line(i1 + "}");
            script.Line(i1 + "$xmatExtra = $xmatFile.ReadByte()");
            script.Line(indent + "}");
            script.Line(indent + "finally { $xmatFile.Dispose() }");
            script.Line(indent + "if ($xmatRead -ne " + length + " -or $xmatExtra -ne -1 -or");
            script.Line(i1 + "[System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($xmatBody)) -ne '" +
                sha256 + "') {");
            script.Line(i1 + "throw 'The body file next to this script is not the body that xmat exported; nothing was sent.'");
            script.Line(indent + "}");
        }

        // Runs the curl.exe of the script by itself first, without showing what it prints: its version must be at least
        // MinimumCurlVersion, and when an argument is not ASCII, its Features must list Unicode. The run's finally stops it
        // if the script fails or is interrupted meanwhile.
        private static void WriteProbe(ScriptWriter script, CurlRequest request, string indent)
        {
            string i1 = indent + "    ";
            script.Line(indent + "$xmatProbe = Start-XmatCurl @('-q', '--version') $false $true");
            script.Line(indent + "$xmatProbeErrors = $xmatProbe.StandardError.ReadToEndAsync()");
            script.Line(indent + "$xmatVersionText = $xmatProbe.StandardOutput.ReadToEnd()");
            script.Line(indent + "$xmatProbe.WaitForExit()");
            script.Line(indent + "$null = $xmatProbeErrors.GetAwaiter().GetResult()");
            script.Line(indent + "$xmatLines = @($xmatVersionText -split \"`r?`n\")");
            script.Line(indent + "if ($xmatLines[0] -notmatch '^curl (\\d+)\\.(\\d+)\\.(\\d+)') {");
            script.Line(i1 + "throw 'curl.exe did not tell its version; nothing was sent.'");
            script.Line(indent + "}");
            script.Line(indent + "if ([version]('{0}.{1}.{2}' -f $Matches[1], $Matches[2], $Matches[3]) -lt [version]'" +
                MinimumCurlVersion + "') {");
            script.Line(i1 + "throw 'This script needs curl.exe " + MinimumCurlVersion + " or later; nothing was sent.'");
            script.Line(indent + "}");
            if (request.NeedsUnicodeCurl)
            {
                script.Line(indent + "$xmatFeatures = @($xmatLines | Where-Object { $_.StartsWith('Features:') })");
                script.Line(indent + "if ($xmatFeatures.Count -eq 0 -or @($xmatFeatures[0] -split ' ') -notcontains 'Unicode') {");
                script.Line(i1 + "throw 'This curl.exe cannot pass the request''s non-ASCII header values unchanged; nothing was " +
                    "sent.'");
                script.Line(indent + "}");
            }
        }

        // Writes one line at a time, checking for cancellation between lines: a script is written in bounded pieces.
        private sealed class ScriptWriter
        {
            private readonly StreamWriter _writer;
            private readonly CancellationToken _cancellationToken;

            internal ScriptWriter(StreamWriter writer, CancellationToken cancellationToken)
            {
                _writer = writer;
                _cancellationToken = cancellationToken;
            }

            internal void Line(string text)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _writer.WriteLine(text);
            }
        }
    }
}
