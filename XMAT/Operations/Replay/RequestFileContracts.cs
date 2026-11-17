// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace XMAT.Operations.Replay
{
    // A request file (xmat-request/1) is data: it never grants consent, carries no transport but the HTTP version, and is
    // read as untrusted whoever wrote it. A template keeps only the request's shape; a content file keeps everything.
    internal enum RequestFileProfile
    {
        Template,
        Content,
    }

    // The parts of a request that a request file left out, and that must be supplied, explicitly, before it can be sent or
    // exported. Immutable: a draft holds one by reference, so every copy of the draft carries the same omissions.
    internal sealed record RequestOmissions(bool Method, bool Url, bool Headers, bool Body)
    {
        internal static RequestOmissions None { get; } = new(false, false, false, false);

        internal bool Any => Method || Url || Headers || Body;
    }

    // A request file as read: Method, Url, Headers and Body are null exactly when Omitted says so.
    internal sealed record RequestFileContent(
        RequestFileProfile Profile,
        string Method,
        string Url,
        ReplayHttpVersion HttpVersion,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        byte[] Body,
        RequestOmissions Omitted);

    internal static class RequestFileCodes
    {
        internal const string NotFound = "replay.request-file.not-found";
        internal const string TooLarge = "replay.request-file.too-large";
        internal const string Unreadable = "replay.request-file.unreadable";
        internal const string Invalid = "replay.request-file.invalid";
        internal const string Unsupported = "replay.request-file.unsupported";
        internal const string Incomplete = "replay.request-file.incomplete";
        internal const string RequestIncomplete = "replay.request.incomplete";
        internal const string RequestInvalid = "replay.request.invalid";
    }

    // The limits of a request file, and of a draft that is saved as one.
    internal static class RequestFileLimits
    {
        internal const string Schema = "xmat-request/1";
        internal const long MaxFileBytes = 96L * 1024 * 1024;
        internal const int MaxBodyBytes = 64 * 1024 * 1024;
        internal const int MaxHeaders = 256;

        // Header names and values together, as decoded UTF-8.
        internal const int MaxHeaderBytes = 256 * 1024;
        internal const int MaxDepth = 8;
        internal const int MaxMethodChars = 64;
        internal const int MaxUrlChars = 64 * 1024;
    }

    // Content is null exactly when Code is not. Sha256 is the SHA-256 of a file that was read, in lowercase hex.
    internal sealed record RequestFileReadResult(RequestFileContent Content, string Code)
    {
        internal string Sha256 { get; init; }

        internal static RequestFileReadResult Failed(string code) => new(null, code);
    }

    // A request file that cannot be read or written, with one of RequestFileCodes. Its message never holds content.
    internal sealed class RequestFileException : Exception
    {
        internal RequestFileException(string code, string message, Exception inner = null)
            : base(message, inner)
        {
            Code = code;
        }

        internal string Code { get; }
    }

    // A draft that still has parts that a request file left out was about to be sent or exported.
    internal sealed class RequestIncompleteException : InvalidOperationException
    {
        internal RequestIncompleteException()
            : base("The request has parts that were left out of its request file (its method, URL, headers or body); " +
                "supply them first.")
        {
        }
    }

    // What a content save would include that the shared secret names recognize: names only, never values. A body is
    // never inspected; its size is reported instead.
    internal sealed record RequestFileRecognized(
        IReadOnlyList<string> HeaderNames,
        IReadOnlyList<string> QueryParameterNames,
        long BodyBytesNotInspected);
}
