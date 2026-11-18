// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Operations.Replay
{
    // The codes of Raw mode and their fixed messages. A message never holds data: no target, host, field,
    // body or path. The command line reports them as they are, and the GUI's en-us texts mirror them word for word.
    internal static class RawCodes
    {
        internal const string InputTooLarge = "replay.raw.input-too-large";
        internal const string FileUnreadable = "replay.raw.file-unreadable";
        internal const string Invalid = "replay.raw.invalid";
        internal const string Method = "replay.raw.method";
        internal const string Target = "replay.raw.target";
        internal const string Version = "replay.raw.version";
        internal const string DuplicateField = "replay.raw.duplicate-field";
        internal const string Host = "replay.raw.host";
        internal const string Connection = "replay.raw.connection";
        internal const string Expect = "replay.raw.expect";
        internal const string Upgrade = "replay.raw.upgrade";
        internal const string Trailer = "replay.raw.trailer";
        internal const string Framing = "replay.raw.framing";
        internal const string BodyNotAllowed = "replay.raw.body-not-allowed";
        internal const string FixContentLength = "replay.raw.fix-content-length";
        internal const string Changed = "replay.raw.changed";
        internal const string TargetDenied = "replay.raw.target-denied";
        internal const string Proxy = "replay.raw.proxy";
        internal const string ClientCert = "replay.raw.client-cert";
        internal const string WindowsAuth = "replay.raw.windows-auth";
        internal const string HttpVersion = "replay.raw.http-version";
        internal const string Redirects = "replay.raw.redirects";
        internal const string UnsupportedOption = "replay.raw.unsupported-option";
        internal const string PolicyRaw = "replay.policy.raw";
        internal const string PolicyRawUnsafe = "replay.policy.raw-unsafe";
        internal const string Alpn = "replay.raw.alpn";
        internal const string ResponseInvalid = "replay.raw.response-invalid";
        internal const string ResponseCoding = "replay.raw.response-coding";
        internal const string ResponseIncomplete = "replay.raw.response-incomplete";
        internal const string SendCancelled = "replay.send.cancelled";

        // The fixed message of each code above; null for any other code.
        internal static string Message(string code) => code switch
        {
            InputTooLarge =>
                "The raw request is larger than Raw mode allows (head 64 KiB, request line 16 KiB, 256 field " +
                "lines, method 64 characters, body 64 MiB, chunk framing 1 MiB in all, chunk lines 8 KiB); " +
                "nothing was sent.",
            FileUnreadable =>
                "The raw request file could not be read; nothing was sent.",
            Invalid =>
                "The raw request is not a valid HTTP/1.1 request head: it must be ASCII with CRLF line ends, " +
                "no folded lines, single spaces in the request line and NAME: VALUE fields. Nothing was sent.",
            Method =>
                "The method must be a token of 1 to 64 characters, and Raw mode does not send CONNECT, or a " +
                "standard method written in another case; nothing was sent.",
            Target =>
                "The request target must be a path, or an absolute URL of the target origin, without a " +
                "fragment; nothing was sent.",
            Version =>
                "Raw mode sends HTTP/1.1 requests only; nothing was sent.",
            DuplicateField =>
                "A field appears more than once in the raw request, which Raw mode does not send; nothing was " +
                "sent.",
            Host =>
                "The raw request needs one Host field naming the target origin; with absolute-form, its value " +
                "must exactly match the authority written in the request target. Nothing was sent.",
            Connection =>
                "Raw mode allows only Connection: close, and no Keep-Alive, Proxy-Connection or TE field; " +
                "nothing was sent.",
            Expect =>
                "Raw mode does not send an Expect field; nothing was sent.",
            Upgrade =>
                "Raw mode does not send a protocol upgrade or a WebSocket handshake; nothing was sent.",
            Trailer =>
                "Raw mode does not send trailers, a Trailer field or chunk extensions; nothing was sent.",
            Framing =>
                "The raw request's framing does not match its body: use one Content-Length equal to the body, " +
                "or Transfer-Encoding: chunked with complete chunks, never both. Nothing was sent.",
            BodyNotAllowed =>
                "A HEAD or TRACE request cannot have a body in Raw mode; nothing was sent.",
            FixContentLength =>
                "Fix Content-Length needs exactly one Content-Length field and no Transfer-Encoding; nothing " +
                "was changed.",
            Changed =>
                "The prepared raw request changed after it was checked; nothing was sent.",
            TargetDenied =>
                "The target, or every address that its name resolves to, is an unspecified, multicast or " +
                "broadcast address; nothing was sent.",
            Proxy =>
                "Raw mode does not send through a proxy; nothing was sent.",
            ClientCert =>
                "Raw mode does not offer a client certificate; nothing was sent.",
            WindowsAuth =>
                "Raw mode does not use Windows authentication; nothing was sent.",
            HttpVersion =>
                "Raw mode sends HTTP/1.1 only, and the run must ask for exactly HTTP/1.1; nothing was sent.",
            Redirects =>
                "Raw mode does not follow redirects; nothing was sent.",
            UnsupportedOption =>
                "Raw mode sends one prepared request, without verification, sequences or edits; nothing was " +
                "sent.",
            PolicyRaw =>
                "A raw request is sent as its prepared bytes: as checked, after any line-break conversion " +
                "that XMAT showed and any Content-Length fix that was asked for, without the normalization " +
                "that other sends get. A front end and the server behind it can read it differently, and it " +
                "can affect other requests on a connection that they share. Sending this raw request to this " +
                "target was not allowed; nothing was sent.",
            PolicyRawUnsafe =>
                "The raw request uses a method that can change server state, or is a GET or OPTIONS request " +
                "with a body, which a server that does not expect one may read as another request; unsafe " +
                "requests were not allowed, so nothing was sent.",
            Alpn =>
                "The server chose a protocol other than HTTP/1.1 for the connection; nothing was sent.",
            ResponseInvalid =>
                "The response is not a valid HTTP/1.x response within Raw mode's limits; the connection was " +
                "closed.",
            ResponseCoding =>
                "The response uses a transfer coding other than chunked alone, which Raw mode does not read; " +
                "the connection was closed.",
            ResponseIncomplete =>
                "The connection ended before XMAT finished reading the response's body; the response may be " +
                "incomplete.",
            SendCancelled =>
                "The send was cancelled; part of the request may already have been written.",
            _ => null,
        };
    }
}
