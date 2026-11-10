// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // Request files (xmat-request/1): strict reading within limits, canonical writing, the template and content profiles,
    // omissions that stay until they are completed explicitly, and an operation that never sends what a file left out.
    // The network is loopback only.
    public sealed class RequestFileTests
    {
        private const string Valid =
            "{\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\"," +
            "\"headers\":[],\"body\":\"\",\"omitted\":[\"url\"]}";

        private const string ValidContent =
            "{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"PUT\"," +
            "\"url\":\"https://example.test/item/#?page=#\",\"httpVersion\":\"2\",\"headers\":[[\"A\",\"b\"],[\"A\",\"c\"]]," +
            "\"body\":\"AAEC\",\"omitted\":[]}";

        [Fact]
        public void ContentFiles_KeepEveryPart_AndWriteCanonically()
        {
            RequestReplayDraft draft = Draft(body: new byte[] { 0, 1, 2, 255 });

            byte[] first = Write(draft, RequestFileProfile.Content);
            RequestFileContent content = Read(first);
            byte[] second = Write(RequestReplayDraft.FromRequestFile(content), RequestFileProfile.Content, content.HttpVersion);

            Assert.Equal(("POST", draft.Url, ReplayHttpVersion.Http11), (content.Method, content.Url, content.HttpVersion));
            Assert.Equal(
                new KeyValuePair<string, string>[] { new("Authorization", "Bearer SECRET"), new("X-Note", "caf\u00e9"), new("X-Note", "two") },
                content.Headers);
            Assert.Equal(new byte[] { 0, 1, 2, 255 }, content.Body);
            Assert.False(content.Omitted.Any);
            Assert.Equal(first, second);
            Assert.False(Encoding.UTF8.GetString(first).StartsWith('\uFEFF'));
        }

        [Fact]
        public void Templates_KeepNoCapturedValueOrStructure()
        {
            var draft = new RequestReplayDraft
            {
                Method = "CANARYMETHOD",
                Url = "https://canary-host.test/canary-path?canary-query=canary-value",
                HeadersText = "X-Canary-Name: canary-header-value",
            };
            draft.SetBodyBytes(Encoding.UTF8.GetBytes("canary-body"));

            byte[] file = Write(draft, RequestFileProfile.Template);

            Assert.DoesNotContain("canary", Encoding.UTF8.GetString(file), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new RequestOmissions(true, true, true, true), Read(file).Omitted);
        }

        [Fact]
        public void Templates_KeepCanonicalMethods_AndWhatIsGenuinelyEmpty()
        {
            RequestFileContent content = Read(
                Write(new RequestReplayDraft { Method = "DELETE", Url = "https://example.test/x" }, RequestFileProfile.Template));

            Assert.Equal(("DELETE", null, RequestFileProfile.Template), (content.Method, content.Url, content.Profile));
            Assert.Empty(content.Headers);
            Assert.Empty(content.Body);
            Assert.Equal(new RequestOmissions(false, true, false, false), content.Omitted);
        }

        [Fact]
        public void Omissions_StayUntilEachPartIsCompletedExplicitly()
        {
            RequestReplayDraft loaded = RequestReplayDraft.FromRequestFile(Read(Write(Draft(), RequestFileProfile.Template)));
            var pending = new RequestOmissions(false, true, true, true);

            // A template's placeholders are not data: it saves again as the same template.
            Assert.Equal(pending, Read(Write(loaded, RequestFileProfile.Template)).Omitted);

            // Editing the text never completes a part.
            loaded.Method = "GET";
            loaded.Url = "https://example.test/";
            loaded.HeadersText = "Accept: x";
            loaded.SetHeader("X-A", "b");
            loaded.RemoveConditionalHeaders();
            loaded.BodyText = "";
            loaded.SetBodyBytes(Array.Empty<byte>());

            Assert.Equal(pending, loaded.Omissions);
            Assert.Equal(pending, loaded.WithUrl("https://other.test/").Omissions);
            Assert.Throws<RequestIncompleteException>(() => RequestCurlExporter.Prepare(loaded, null, 0, CancellationToken.None));
            Assert.Equal(pending, Read(Write(loaded, RequestFileProfile.Template)).Omitted);
            Assert.Equal(
                RequestFileCodes.Incomplete,
                Assert.Throws<RequestFileException>(() => Write(loaded, RequestFileProfile.Content)).Code);

            loaded.CompleteUrl("https://example.test/done");
            Assert.Equal(new RequestOmissions(false, false, true, true), loaded.Omissions);
            loaded.ReplaceHeaders("");
            loaded.CompleteBody(Array.Empty<byte>());
            Assert.False(loaded.Omissions.Any);
            Assert.Equal("-q", RequestCurlExporter.Prepare(loaded, null, 0, CancellationToken.None).Arguments[0]);
        }

        // The file's URL is the origin that a change needs consent for: supplying a URL never replaces it.
        [Fact]
        public void Completion_NeverReplacesTheOriginOfAFile()
        {
            RequestReplayDraft loaded = RequestReplayDraft.FromRequestFile(Read(Write(Draft(), RequestFileProfile.Content)));

            loaded.CompleteUrl("https://elsewhere.test/a");

            Assert.True(loaded.ChangesOrigin);
        }

        // A URL supplied for one that a file left out is the request's first origin: moving it to another needs consent. An
        // invalid URL supplies nothing.
        [Fact]
        public void CompletingAnOmittedUrl_SetsTheFirstOrigin()
        {
            RequestReplayDraft loaded = RequestReplayDraft.FromRequestFile(Read(Write(Draft(), RequestFileProfile.Template)));

            // Refused before anything changes: an absolute URL that a request cannot use, as well as one that is not a URL.
            Assert.Throws<ArgumentException>(() => loaded.CompleteUrl("not a url"));
            Assert.Throws<ArgumentException>(() => loaded.CompleteUrl("ftp://first.test/a"));
            Assert.Equal(("", true), (loaded.Url, loaded.Omissions.Url));
            loaded.CompleteUrl("https://first.test/a");
            Assert.False(loaded.ChangesOrigin);
            loaded.Url = "https://second.test/a";

            Assert.True(loaded.ChangesOrigin);
        }

        [Fact]
        public void ValidFiles_AreRead_WithRepeatedHeadersAndSequenceTemplates()
        {
            RequestFileContent template = Read(Encoding.UTF8.GetBytes(Valid));
            RequestFileContent content = Read(Encoding.UTF8.GetBytes(ValidContent));

            Assert.Equal(new RequestOmissions(false, true, false, false), template.Omitted);
            Assert.Equal("https://example.test/item/#?page=#", content.Url);
            Assert.Equal(new[] { "b", "c" }, content.Headers.Select(header => header.Value));
            Assert.Equal(new byte[] { 0, 1, 2 }, content.Body);
            Assert.Equal(ReplayHttpVersion.Http2, content.HttpVersion);
        }

        [Theory]
        [InlineData("\uFEFF" + Valid, "invalid")]
        [InlineData("{\"extra\":1," + "\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[\"url\"]}", "invalid")]
        [InlineData("{\"method\":\"GET\"," + "\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[\"url\"]}", "invalid")]
        [InlineData("{\"sch\\u0065ma\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[\"url\"]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",/* note */\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[\"url\"]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[\"url\"],}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\"}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/2\",\"headers\":\"newer\"}", "unsupported")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"https://e.test/\",\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\\u0041AAA\",\"omitted\":[]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"https://e.test/\",\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"AAA AAA=\",\"omitted\":[]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":\"https://e.test/\",\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[\"url\"]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"PURGE\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[\"url\"]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":[[\"A\",\"b\"]],\"body\":\"\",\"omitted\":[\"url\"]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":null,\"body\":null,\"omitted\":[\"url\",\"body\",\"headers\"]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"template\",\"method\":\"GET\",\"url\":null,\"httpVersion\":\"auto\",\"headers\":null,\"body\":\"\",\"omitted\":[\"url\"]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"https://e.test/\",\"httpVersion\":\"auto\",\"headers\":[[\"A\",\" b\"]],\"body\":\"\",\"omitted\":[]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"https://e.test/\",\"httpVersion\":\"auto\",\"headers\":[[\"A\",\"b\\r\\nX: y\"]],\"body\":\"\",\"omitted\":[]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"https://e#.test/\",\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"CONNECT\",\"url\":\"https://e.test/\",\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[]}", "invalid")]
        [InlineData("{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"https://e.test/\",\"httpVersion\":\"3\",\"headers\":[],\"body\":\"\",\"omitted\":[]}", "invalid")]
        public void InvalidFiles_AreRefused(string json, string reason)
        {
            Assert.Equal("replay.request-file." + reason, RequestFileReader.Read(Encoding.UTF8.GetBytes(json)).Code);
        }

        // A value's JSON escapes are only its spelling: fully escaped, each value reads as itself, also when its escaped
        // spelling is longer than the value's limit, and an unknown value stays unknown however it is spelled. Member names
        // are never escaped (InvalidFiles_AreRefused).
        [Fact]
        public void EscapedValues_ReadAsWhatTheyDecodeTo()
        {
            string template =
                "{\"schema\":\"" + Escaped("xmat-request/1") + "\",\"profile\":\"" + Escaped("template") + "\"," +
                "\"method\":null,\"url\":null,\"httpVersion\":\"" + Escaped("1.1") + "\",\"headers\":null,\"body\":null," +
                "\"omitted\":[\"" + Escaped("method") + "\",\"" + Escaped("url") + "\",\"" + Escaped("headers") + "\",\"" +
                Escaped("body") + "\"]}";
            string content =
                "{\"schema\":\"xmat-request/1\",\"profile\":\"" + Escaped("content") + "\",\"method\":\"GET\"," +
                "\"url\":\"https://e.test/\",\"httpVersion\":\"" + Escaped("auto") + "\",\"headers\":[],\"body\":\"\"," +
                "\"omitted\":[]}";

            RequestFileContent read = Read(Encoding.UTF8.GetBytes(template));

            Assert.Equal(RequestFileProfile.Template, read.Profile);
            Assert.Equal(ReplayHttpVersion.Http11, read.HttpVersion);
            Assert.Equal(new RequestOmissions(true, true, true, true), read.Omitted);
            Assert.Equal(ReplayHttpVersion.Auto, Read(Encoding.UTF8.GetBytes(content)).HttpVersion);
            Assert.Equal(
                RequestFileCodes.Invalid,
                RequestFileReader.Read(Encoding.UTF8.GetBytes(template.Replace(Escaped("url"), Escaped("urls")))).Code);
            Assert.Equal(
                RequestFileCodes.Unsupported,
                RequestFileReader.Read(Encoding.UTF8.GetBytes(
                    template.Replace(Escaped("xmat-request/1"), Escaped("xmat-request/2")))).Code);
        }

        // A value's length is counted decoded before the value is made, escaped or not: a megabyte of either is refused
        // without a string of its size.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ALongValue_IsRefusedBeforeItIsMade(bool escaped)
        {
            string value = escaped
                ? string.Concat(Enumerable.Repeat("\\u0074", 1024 * 1024 / 6))
                : new string('t', 1024 * 1024);
            byte[] json = Encoding.UTF8.GetBytes(Valid.Replace("\"template\"", "\"" + value + "\""));

            long before = GC.GetAllocatedBytesForCurrentThread();
            RequestFileReadResult refused = RequestFileReader.Read(json);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(RequestFileCodes.Invalid, refused.Code);
            Assert.InRange(allocated, 0, 64 * 1024);
        }

        // A header's size is its decoded UTF-8, never its escaped length: 256 KiB written as \u0041 escapes (six times
        // larger) still fits, and one byte more does not.
        [Fact]
        public void HeaderText_IsCountedDecoded_BeforeItIsMade()
        {
            static string Json(int count) =>
                "{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"https://e.test/\"," +
                "\"httpVersion\":\"auto\",\"headers\":[[\"A\",\"" + string.Concat(Enumerable.Repeat("\\u0041", count)) +
                "\"]],\"body\":\"\",\"omitted\":[]}";

            Assert.Null(RequestFileReader.Read(Encoding.UTF8.GetBytes(Json(RequestFileLimits.MaxHeaderBytes - 1))).Code);
            Assert.Equal(
                RequestFileCodes.Invalid,
                RequestFileReader.Read(Encoding.UTF8.GetBytes(Json(RequestFileLimits.MaxHeaderBytes))).Code);
            Assert.Equal(4, RequestFileReader.DecodedUtf8Length("\\ud83d\\ude00"u8));
            Assert.Equal(3, RequestFileReader.DecodedUtf8Length("\\u20ac"u8));
        }

        [Fact]
        public void Headers_AreAtMost256()
        {
            static string Json(int count) =>
                "{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"https://e.test/\"," +
                "\"httpVersion\":\"auto\",\"headers\":[" + string.Join(",", Enumerable.Repeat("[\"A\",\"b\"]", count)) +
                "],\"body\":\"\",\"omitted\":[]}";

            Assert.Null(RequestFileReader.Read(Encoding.UTF8.GetBytes(Json(RequestFileLimits.MaxHeaders))).Code);
            Assert.Equal(
                RequestFileCodes.Invalid,
                RequestFileReader.Read(Encoding.UTF8.GetBytes(Json(RequestFileLimits.MaxHeaders + 1))).Code);
        }

        // A URL's length is counted in characters from its raw value, escapes decoded, before the string is made: 64 Ki
        // characters written as \u escapes fit, and one more is refused without making the string.
        [Fact]
        public void UrlLength_IsCountedDecoded_BeforeItIsMade()
        {
            const string Prefix = "https://e.test/";
            static byte[] Json(int characters) => Encoding.UTF8.GetBytes(
                "{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"" + Prefix +
                string.Concat(Enumerable.Repeat("\\u0061", characters - Prefix.Length)) +
                "\",\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[]}");
            byte[] fits = Json(RequestFileLimits.MaxUrlChars);
            byte[] over = Json(RequestFileLimits.MaxUrlChars + 1);

            long before = GC.GetAllocatedBytesForCurrentThread();
            RequestFileReadResult refused = RequestFileReader.Read(over);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(RequestFileLimits.MaxUrlChars, RequestFileReader.Read(fits).Content.Url.Length);
            Assert.Equal(RequestFileCodes.Invalid, refused.Code);
            Assert.InRange(allocated, 0, 64 * 1024);
            Assert.Equal(3, RequestFileReader.DecodedCharLength("\\ud83d\\ude00a"u8));
            Assert.Equal(3, RequestFileReader.DecodedCharLength("\u00e9\U0001F600"u8));
        }

        // Saving checks the header limits in the text itself, before any header is made, and counts the body without
        // decoding it.
        [Fact]
        public void SavingChecksHeaderAndBodyLimits_BeforeMakingThem()
        {
            var many = new RequestReplayDraft
            {
                Method = "GET",
                Url = "https://e.test/",
                HeadersText = string.Join("\r\n", Enumerable.Repeat("A: b", RequestFileLimits.MaxHeaders + 1)),
            };
            var large = new RequestReplayDraft
            {
                Method = "GET",
                Url = "https://e.test/",
                HeadersText = "A: " + new string('b', RequestFileLimits.MaxHeaderBytes),
            };
            var base64 = new RequestReplayDraft { BodyIsBase64 = true, BodyText = " AAEC\r\nAw== " };

            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<FormatException>(large.ValidateStructure);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.InRange(allocated, 0, 64 * 1024);
            Assert.Equal(
                RequestFileCodes.RequestInvalid,
                Assert.Throws<RequestFileException>(() => Write(many, RequestFileProfile.Content)).Code);
            Assert.Equal(4, base64.GetBodyLength());
            Assert.Equal(4, base64.GetBodyBytes().Length);
            Assert.Equal(3, new RequestReplayDraft { BodyText = "\u00e9a" }.GetBodyLength());

            // Base64 that does not decode is invalid however the file would keep it, a template included.
            foreach (string invalid in new[] { "!!!!", "AB=C", "AAE", "A===" })
            {
                var draft = new RequestReplayDraft { Method = "GET", Url = "https://e.test/", BodyIsBase64 = true, BodyText = invalid };
                Assert.Throws<FormatException>(() => Convert.FromBase64String(invalid));
                Assert.Equal(
                    RequestFileCodes.RequestInvalid,
                    Assert.Throws<RequestFileException>(() => Write(draft, RequestFileProfile.Template)).Code);
            }
        }

        // A body one byte over 64 MiB is refused from its Base64 length, before anything of its size is allocated. The Base64
        // is padded to decode to exactly 64 MiB + 1 bytes, so a limit one byte larger would take it.
        [Fact]
        public void ABodyBeyondTheLimit_IsRefusedBeforeItIsDecoded()
        {
            byte[] head = Encoding.UTF8.GetBytes(
                "{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"PUT\",\"url\":\"https://e.test/\"," +
                "\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"");
            byte[] tail = Encoding.UTF8.GetBytes("\",\"omitted\":[]}");
            const int Decoded = RequestFileLimits.MaxBodyBytes + 1;
            int length = 4 * ((Decoded + 2) / 3);
            int padding = (3 - (Decoded % 3)) % 3;
            var json = new byte[head.Length + length + tail.Length];
            head.CopyTo(json, 0);
            json.AsSpan(head.Length, length - padding).Fill((byte)'A');
            json.AsSpan(head.Length + length - padding, padding).Fill((byte)'=');
            tail.CopyTo(json, head.Length + length);

            long before = GC.GetAllocatedBytesForCurrentThread();
            RequestFileReadResult result = RequestFileReader.Read(json);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(Decoded, (length / 4 * 3) - padding);
            Assert.Equal(RequestFileCodes.Invalid, result.Code);
            Assert.InRange(allocated, 0, 8L * 1024 * 1024);
        }

        // The body's Base64 is written in chunks of whole 3-byte groups: it is the body's one Base64, padded at its end only.
        // 64 MiB itself round-trips in RequestFileIsolatedTests.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(49151)]
        [InlineData(49152)]
        [InlineData(49153)]
        [InlineData(65535)]
        [InlineData(65536)]
        [InlineData(65537)]
        [InlineData(98305)]
        public void Bodies_RoundTrip_PaddedOnlyAtTheEnd(int size)
        {
            byte[] body = Enumerable.Range(0, size).Select(index => (byte)(index * 7)).ToArray();
            var draft = new RequestReplayDraft { Method = "PUT", Url = "https://example.test/" };
            draft.SetBodyBytes(body);

            byte[] file = Write(draft, RequestFileProfile.Content);
            string text = Encoding.UTF8.GetString(file);
            int start = text.IndexOf("\"body\": \"", StringComparison.Ordinal) + 9;

            Assert.Equal(Convert.ToBase64String(body), text[start..text.IndexOf('"', start)]);
            Assert.Equal(body, Read(file).Body);
        }

        // Fiddler's <@INCLUDE *path*@> is text like any other: no file is ever read for it.
        [Fact]
        public void IncludeTokens_StayLiteral()
        {
            string canary = Path.Join(Path.GetTempPath(), "xmat-include-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(canary, "CANARY-CONTENT");
            try
            {
                byte[] body = Encoding.UTF8.GetBytes("<@INCLUDE *" + canary + "*@>");
                var draft = new RequestReplayDraft { Method = "POST", Url = "https://example.test/" };
                draft.SetBodyBytes(body);

                RequestReplayDraft loaded = RequestReplayDraft.FromRequestFile(Read(Write(draft, RequestFileProfile.Content)));

                Assert.Equal(body, loaded.GetBodyBytes());
            }
            finally
            {
                File.Delete(canary);
            }
        }

        [Fact]
        public void Recognize_NamesLikelySecrets_NeverValues()
        {
            RequestFileRecognized recognized = RequestFileWriter.Recognize(Draft());

            Assert.Equal(new[] { "Authorization" }, recognized.HeaderNames);
            Assert.Equal(new[] { "access_token" }, recognized.QueryParameterNames);
            Assert.Equal(Encoding.UTF8.GetByteCount("{\"password\":\"hunter2\"}"), recognized.BodyBytesNotInspected);
        }

        [Fact]
        public async Task ATemplate_IsNeverSent_UntilCompleted()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            RequestFileContent template = Read(Write(
                new RequestReplayDraft { Method = "GET", Url = server.Origin + "/", HeadersText = "Accept: */*" },
                RequestFileProfile.Template));
            var operation = new ReplayOperation();

            OperationResult<ReplayPlanPreview> preview =
                operation.Preview(ReplaySource.FromDraft(RequestReplayDraft.FromRequestFile(template)));
            OperationResult<ReplayResult> refused = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(RequestReplayDraft.FromRequestFile(template)),
                    Edits: new ReplayEdits(Url: server.Origin + "/", SetHeaders: new KeyValuePair<string, string>[] { new("Accept", "*/*") })),
                OperationContext.None,
                CancellationToken.None);
            int sentWhenRefused = server.Requests.Count;
            OperationResult<ReplayResult> sent = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(RequestReplayDraft.FromRequestFile(template)),
                    Edits: new ReplayEdits(Url: server.Origin + "/", ClearHeaders: true)),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(RequestFileCodes.RequestIncomplete, Assert.Single(preview.Diagnostics).Code);
            Assert.Equal(RequestFileCodes.RequestIncomplete, Assert.Single(refused.Diagnostics).Code);
            Assert.Equal(0, sentWhenRefused);
            Assert.True(sent.Succeeded);
            Assert.Single(server.Requests);
        }

        // A file's URL is its request's origin, and a file grants nothing: an unsafe method still needs consent, and so does
        // another origin.
        [Fact]
        public async Task AFilesUrl_IsItsOrigin_AndAFileGrantsNothing()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var other = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            RequestFileContent file = Read(
                Write(new RequestReplayDraft { Method = "DELETE", Url = server.Origin + "/item" }, RequestFileProfile.Content));
            var operation = new ReplayOperation();
            Task<OperationResult<ReplayResult>> Run(ReplayEdits edits, ReplayOptions options) =>
                operation.ExecuteAsync(
                    new ReplayRequest(ReplaySource.FromDraft(RequestReplayDraft.FromRequestFile(file)), options, edits),
                    OperationContext.None,
                    CancellationToken.None);

            OperationResult<ReplayResult> unsafeMethod = await Run(ReplayEdits.None, ReplayOptions.Default);
            OperationResult<ReplayResult> moved =
                await Run(new ReplayEdits(Url: other.Origin + "/item"), new ReplayOptions(AllowUnsafeMethods: true));
            OperationResult<ReplayResult> allowed = await Run(
                new ReplayEdits(Url: other.Origin + "/item"),
                new ReplayOptions(AllowUnsafeMethods: true, AllowOriginChange: true));

            Assert.Equal("replay.policy.unsafe-method", Assert.Single(unsafeMethod.Diagnostics).Code);
            Assert.Equal("replay.policy.origin-change", Assert.Single(moved.Diagnostics).Code);
            Assert.True(allowed.Succeeded);
            Assert.Empty(server.Requests);
            Assert.Single(other.Requests);
        }

        // Saving and exporting prepare requests without any sender, and need no consent: nothing is sent.
        [Fact]
        public void PrepareForExport_NeverMakesASender()
        {
            var operation = new ReplayOperation
            {
                SenderFactory = () => throw new InvalidOperationException("No sender."),
                ConnectToSenderFactory = _ => throw new InvalidOperationException("No sender."),
            };

            OperationResult<ReplayExport> prepared = operation.PrepareForExport(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft()),
                    Edits: new ReplayEdits(Url: "https://example.test/x", Method: "DELETE")),
                CancellationToken.None);

            Assert.True(prepared.Succeeded);
            Assert.Equal(("DELETE", "https://example.test/x"), (prepared.Value.Items[0].Draft.Method, prepared.Value.Items[0].Draft.Url));
        }

        // Each prepared request is checked as data: a method that no request file can hold is refused.
        [Fact]
        public void PrepareForExport_ChecksEachRequestAsData()
        {
            OperationResult<ReplayExport> prepared = new ReplayOperation().PrepareForExport(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft()),
                    Edits: new ReplayEdits(Url: "https://example.test/x", Method: "BAD METHOD")),
                CancellationToken.None);

            Assert.Equal(RequestFileCodes.RequestInvalid, Assert.Single(prepared.Diagnostics).Code);
        }

        // A file larger than the limit is refused from its length, before anything of its size is read or allocated. The
        // allocation is measured across the process, so the test runs alone (RequestFileIsolatedTests).

        // More than MaxSends requests are refused before any draft is built: none of these could be, so building them first
        // would report that the selection has no request instead.
        [Fact]
        public void PrepareForExport_RefusesMoreThanMaxSends_BeforeBuildingAnyDraft()
        {
            var connect = new ProxyConnectionModel(
                1, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:1", "CONNECT", "/", "CONNECT / HTTP/1.1\r\n", [],
                "HTTP/1.1 200 X\r\n", [], "127.0.0.1");
            List<ProxyConnectionModel> captures = Enumerable.Repeat(connect, ReplayOptions.MaxSends + 1).ToList();

            OperationResult<ReplayExport> prepared =
                new ReplayOperation().PrepareForExport(new ReplayRequest(ReplaySource.FromCaptures(captures)), CancellationToken.None);

            Assert.Equal("replay.plan.too-large", Assert.Single(prepared.Diagnostics).Code);
        }

        private static RequestReplayDraft Draft(byte[] body = null)
        {
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = "https://example.test/a?access_token=T0K3N&page=1",
                HeadersText = "Authorization: Bearer SECRET\r\nX-Note: caf\u00e9\r\nX-Note: two",
            };
            draft.SetBodyBytes(body ?? Encoding.UTF8.GetBytes("{\"password\":\"hunter2\"}"));
            return draft;
        }

        private static byte[] Write(
            RequestReplayDraft draft, RequestFileProfile profile, ReplayHttpVersion version = ReplayHttpVersion.Http11)
        {
            using var stream = new MemoryStream();
            RequestFileWriter.Write(draft, version, profile, stream, CancellationToken.None);
            return stream.ToArray();
        }

        private static RequestFileContent Read(byte[] file)
        {
            RequestFileReadResult result = RequestFileReader.Read(file);
            Assert.Null(result.Code);
            return result.Content;
        }

        // The JSON spelling of value with every character as a \u escape.
        private static string Escaped(string value) =>
            string.Concat(value.Select(character => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture)));
    }
}
