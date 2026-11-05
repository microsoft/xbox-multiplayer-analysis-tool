// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using XMAT.Operations.Privacy;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // Writes a draft as a request file (xmat-request/1).
    // - A template keeps only the request's shape: a canonical method, the HTTP version, and an empty header block or body
    //   only when there is none. It leaves out the URL, every header name and value, any other method and the body, and
    //   it keeps what the draft still leaves out.
    // - A content file keeps everything, and needs a complete draft.
    // The output is canonical: indented with CRLF, no byte order mark, strings escaped one way, and the body's Base64
    // written in chunks of whole 3-byte groups, so only its end is padded. A file that this writer wrote reads back and
    // writes again to the same bytes.
    internal static class RequestFileWriter
    {
        // A multiple of 3, so every chunk but the last encodes without padding.
        private const int ChunkBytes = 48 * 1024;

        private static readonly string[] CanonicalMethods =
        {
            "GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE", "TRACE",
        };

        private static readonly JavaScriptEncoder Escaping = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

        // A standard method, in its exact case: the only methods that a template keeps, since any other token can be
        // anything at all, a secret included.
        internal static bool IsCanonicalMethod(string method) => Array.IndexOf(CanonicalMethods, method) >= 0;

        // Checks the draft (RequestReplayDraft.ValidateStructure, which skips what it still leaves out, and checks the
        // header and body limits before anything of their size is made) before it writes anything, then writes it as
        // profile says, leaving out what Omitted lists. Cancellation is checked between the body's chunks.
        internal static void Write(
            RequestReplayDraft draft,
            ReplayHttpVersion httpVersion,
            RequestFileProfile profile,
            Stream stream,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(stream);
            if (profile == RequestFileProfile.Content && draft.Omissions.Any)
            {
                throw new RequestFileException(
                    RequestFileCodes.Incomplete,
                    "A request with parts that its request file left out can be saved only as a template.");
            }

            IReadOnlyList<string> omitted;
            IReadOnlyList<KeyValuePair<string, string>> headers;
            byte[] body;
            try
            {
                draft.ValidateStructure();
                omitted = Omitted(draft, profile);
                headers = omitted.Contains("headers") ? null : draft.GetHeaders();
                body = omitted.Contains("body") ? null : draft.GetBodyBytes();
            }
            catch (FormatException ex)
            {
                throw Invalid(ex);
            }

            string method = omitted.Contains("method") ? null : draft.Method;
            string url = omitted.Contains("url") ? null : draft.Url;

            // Every string is encoded before anything is written, so a value that JSON cannot hold (a lone surrogate)
            // writes nothing.
            byte[] head;
            try
            {
                head = Head(profile == RequestFileProfile.Template, method, url, httpVersion, headers);
            }
            catch (ArgumentException ex)
            {
                throw Invalid(ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            stream.Write(head);
            if (body == null)
            {
                stream.Write("null"u8);
            }
            else
            {
                stream.WriteByte((byte)'"');
                WriteBase64(stream, body, cancellationToken);
                stream.WriteByte((byte)'"');
            }

            stream.Write(",\r\n  \"omitted\": ["u8);
            for (int index = 0; index < omitted.Count; index++)
            {
                stream.Write(Encoding.ASCII.GetBytes((index == 0 ? "\"" : ", \"") + omitted[index] + "\""));
            }

            stream.Write("]\r\n}\r\n"u8);
        }

        // The kinds that the draft's file leaves out as profile says, in order: what the draft still leaves out, and for a
        // template its URL, any method that is not canonical, and the header lines and body that it has. A content file
        // leaves out nothing (Write refuses an incomplete draft).
        internal static IReadOnlyList<string> Omitted(RequestReplayDraft draft, RequestFileProfile profile)
        {
            ArgumentNullException.ThrowIfNull(draft);
            RequestOmissions pending = draft.Omissions;
            bool template = profile == RequestFileProfile.Template;
            var omitted = new List<string>(4);
            AddIf(omitted, pending.Method || (template && !IsCanonicalMethod(draft.Method)), "method");
            AddIf(omitted, pending.Url || template, "url");
            AddIf(omitted, pending.Headers || (template && !string.IsNullOrWhiteSpace(draft.HeadersText)), "headers");
            AddIf(omitted, pending.Body || (template && draft.GetBodyLength() > 0), "body");
            return omitted.AsReadOnly();
        }

        // What a content save would include that the shared secret names recognize: the names of headers and of query
        // parameters, one per occurrence, never their values, and the size of a body, which is never inspected. Parts
        // that the draft still leaves out are not counted.
        internal static RequestFileRecognized Recognize(RequestReplayDraft draft)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var headerNames = new List<string>();
            var parameterNames = new List<string>();
            try
            {
                if (!draft.Omissions.Headers)
                {
                    foreach (KeyValuePair<string, string> header in draft.GetHeaders())
                    {
                        if (SecretNames.IsSensitiveHeaderName(header.Key))
                        {
                            headerNames.Add(header.Key);
                        }
                    }
                }

                if (!draft.Omissions.Url)
                {
                    foreach (string name in QueryParameterNames(draft.Url ?? ""))
                    {
                        if (SecretNames.IsSensitiveParameterName(name))
                        {
                            parameterNames.Add(name);
                        }
                    }
                }

                long bodyBytes = draft.Omissions.Body ? 0 : draft.GetBodyBytes().LongLength;
                return new RequestFileRecognized(headerNames.AsReadOnly(), parameterNames.AsReadOnly(), bodyBytes);
            }
            catch (FormatException ex)
            {
                throw Invalid(ex);
            }
        }

        // The names of the URL's query parameters, split on '&' and ';' and percent-decoded, in order. A request file's
        // URL has no fragment: a '#' there is a sequence placeholder, so everything after the '?' is the query.
        private static IEnumerable<string> QueryParameterNames(string url)
        {
            int query = url.IndexOf('?');
            if (query < 0)
            {
                yield break;
            }

            foreach (string parameter in url[(query + 1)..].Split('&', ';'))
            {
                int equals = parameter.IndexOf('=');
                string name = Uri.UnescapeDataString(equals < 0 ? parameter : parameter[..equals]);
                if (name.Length > 0)
                {
                    yield return name;
                }
            }
        }

        // Everything before the body's value, as UTF-8.
        private static byte[] Head(
            bool template,
            string method,
            string url,
            ReplayHttpVersion httpVersion,
            IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            using var head = new MemoryStream();
            head.Write("{\r\n  \"schema\": "u8);
            WriteString(head, RequestFileLimits.Schema);
            head.Write(",\r\n  \"profile\": "u8);
            WriteString(head, template ? "template" : "content");
            head.Write(",\r\n  \"method\": "u8);
            WriteNullable(head, method);
            head.Write(",\r\n  \"url\": "u8);
            WriteNullable(head, url);
            head.Write(",\r\n  \"httpVersion\": "u8);
            WriteString(head, httpVersion switch
            {
                ReplayHttpVersion.Http11 => "1.1",
                ReplayHttpVersion.Http2 => "2",
                _ => "auto",
            });
            head.Write(",\r\n  \"headers\": "u8);
            if (headers == null)
            {
                head.Write("null"u8);
            }
            else if (headers.Count == 0)
            {
                head.Write("[]"u8);
            }
            else
            {
                head.Write("["u8);
                for (int index = 0; index < headers.Count; index++)
                {
                    head.Write(index == 0 ? "\r\n    ["u8 : ",\r\n    ["u8);
                    WriteString(head, headers[index].Key);
                    head.Write(", "u8);
                    WriteString(head, headers[index].Value);
                    head.Write("]"u8);
                }

                head.Write("\r\n  ]"u8);
            }

            head.Write(",\r\n  \"body\": "u8);
            return head.ToArray();
        }

        private static void WriteNullable(Stream stream, string value)
        {
            if (value == null)
            {
                stream.Write("null"u8);
            }
            else
            {
                WriteString(stream, value);
            }
        }

        private static void WriteString(Stream stream, string value)
        {
            stream.WriteByte((byte)'"');
            stream.Write(JsonEncodedText.Encode(value, Escaping).EncodedUtf8Bytes);
            stream.WriteByte((byte)'"');
        }

        // The body's Base64 in chunks of ChunkBytes input bytes, so it is never held twice; only the last chunk can end
        // with padding.
        private static void WriteBase64(Stream stream, byte[] body, CancellationToken cancellationToken)
        {
            byte[] encoded = ArrayPool<byte>.Shared.Rent(Base64.GetMaxEncodedToUtf8Length(ChunkBytes));
            try
            {
                for (int offset = 0; offset < body.Length; offset += ChunkBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(ChunkBytes, body.Length - offset);
                    bool last = offset + count == body.Length;
                    if (Base64.EncodeToUtf8(body.AsSpan(offset, count), encoded, out int consumed, out int written, last) !=
                        OperationStatus.Done || consumed != count)
                    {
                        throw new InvalidOperationException("A body chunk did not encode whole.");
                    }

                    stream.Write(encoded, 0, written);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(encoded);
            }
        }

        private static void AddIf(List<string> kinds, bool condition, string kind)
        {
            if (condition)
            {
                kinds.Add(kind);
            }
        }

        private static RequestFileException Invalid(Exception inner) =>
            new(
                RequestFileCodes.RequestInvalid,
                "The request is not valid as a request file, such as an invalid method, URL, header or body.",
                inner);
    }
}
