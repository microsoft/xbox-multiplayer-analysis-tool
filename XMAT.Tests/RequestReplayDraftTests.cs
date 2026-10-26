// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public class RequestReplayDraftTests
    {
        [Fact]
        public async Task FromCapture_CreatesIndependentEditableSnapshot()
        {
            var capture = Capture("POST", "/sessions?name=one", "original"u8.ToArray(),
                "Content-Type: application/json\r\nX-Test: first\r\nX-Test: second\r\n");
            var draft = RequestReplayDraft.FromCapture(capture);

            Assert.Equal("https://example.com:8443/sessions?name=one", draft.Url);
            Assert.Equal("POST", draft.Method);
            Assert.Equal("original", draft.BodyText);
            Assert.False(draft.BodyIsBase64);

            draft.Url = "https://other.example/edited";
            draft.BodyText = "edited";
            using var request = draft.CreateRequest();

            Assert.Equal(["first", "second"], request.Headers.GetValues("X-Test"));
            Assert.Equal("edited", await request.Content.ReadAsStringAsync());
            Assert.Equal("/sessions?name=one", capture.Path);
            Assert.Equal("original"u8.ToArray(), capture.RequestBody);
            Assert.Equal("second", capture.RequestHeaders["X-Test"]);
        }

        [Theory]
        [InlineData("GET", false)]
        [InlineData("HEAD", false)]
        [InlineData("OPTIONS", false)]
        [InlineData("POST", true)]
        [InlineData("PUT", true)]
        [InlineData("DELETE", true)]
        [InlineData("PATCH", true)]
        [InlineData("CUSTOM", true)]
        public void RequiresConfirmation_OnlyExemptsSafeMethods(string method, bool expected)
        {
            Assert.Equal(expected, new RequestReplayDraft { Method = method }.RequiresConfirmation);
        }

        [Fact]
        public void RequiresConfirmation_AlsoGuardsChangingTheCapturedOrigin()
        {
            var draft = RequestReplayDraft.FromCapture(Capture("GET", "/", []));
            Assert.False(draft.RequiresConfirmation);
            draft.Url = "http://different.example/";
            Assert.True(draft.RequiresConfirmation);
        }

        [Fact]
        public async Task CreateRequest_RebuildsAuthorityAndFramingAndRemovesConnectionHeaders()
        {
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = "https://new.example/path",
                HeadersText = "Host: old.example\r\nContent-Length: 999\r\n" +
                    "Connection: keep-alive, X-Connection-Only\r\nX-Connection-Only: private\r\n" +
                    "Transfer-Encoding: chunked\r\nProxy-Authorization: secret\r\n" +
                    "Content-Type: application/json\r\nAuthorization: Bearer explicit\r\n",
                BodyText = "{\"updated\":true}"
            };

            using var request = draft.CreateRequest();

            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
            Assert.Null(request.Headers.Host);
            Assert.False(request.Headers.Contains("Connection"));
            Assert.False(request.Headers.Contains("X-Connection-Only"));
            Assert.False(request.Headers.Contains("Proxy-Authorization"));
            Assert.False(request.Headers.TransferEncodingChunked ?? false);
            Assert.Equal("Bearer explicit", request.Headers.Authorization.ToString());
            Assert.Equal("application/json", request.Content.Headers.ContentType.MediaType);
            Assert.Equal(Encoding.UTF8.GetByteCount(draft.BodyText), request.Content.Headers.ContentLength);
            Assert.Equal(draft.BodyText, await request.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("not a url")]
        [InlineData("/relative")]
        [InlineData("file:///C:/capture")]
        [InlineData("ftp://example.com/file")]
        [InlineData("https://user:password@example.com/")]
        [InlineData("https://example.com/#fragment")]
        public void CreateRequest_RejectsInvalidOrAmbiguousUrls(string url)
        {
            var draft = new RequestReplayDraft { Url = url };
            Assert.Throws<ArgumentException>(() => draft.CreateRequest());
        }

        [Theory]
        [InlineData("Header without colon")]
        [InlineData("Bad Name: value")]
        [InlineData(": pseudo-header")]
        [InlineData("X-Test: first\rInjected: second")]
        [InlineData("X-Test: value\0bad")]
        [InlineData("Content-Length: not-a-number\nbroken")]
        public void CreateRequest_RejectsMalformedHeaders(string headers)
        {
            var draft = new RequestReplayDraft { Url = "https://example.com/", HeadersText = headers };
            Assert.Throws<FormatException>(() => draft.CreateRequest());
        }

        [Theory]
        [InlineData("CONNECT")]
        [InlineData("GET / HTTP/1.1")]
        [InlineData("")]
        public void CreateRequest_RejectsUnsupportedOrInvalidMethod(string method)
        {
            var draft = new RequestReplayDraft { Method = method, Url = "https://example.com/" };
            Assert.ThrowsAny<ArgumentException>(() => draft.CreateRequest());
        }

        [Fact]
        public void CreateRequest_RejectsWebSocketUpgrade()
        {
            var draft = new RequestReplayDraft
            {
                Url = "https://example.com/",
                HeadersText = "Connection: Upgrade\r\nUpgrade: websocket"
            };
            Assert.Throws<ArgumentException>(() => draft.CreateRequest());
        }

        [Fact]
        public async Task FromCapture_PreservesBinaryBodyUsingBase64()
        {
            byte[] body = [0, 255, 128, 13, 10];
            var draft = RequestReplayDraft.FromCapture(Capture("POST", "/", body));

            Assert.True(draft.BodyIsBase64);
            Assert.Equal(Convert.ToBase64String(body), draft.BodyText);
            using var request = draft.CreateRequest();
            Assert.Equal(body, await request.Content.ReadAsByteArrayAsync());
        }

        [Fact]
        public void FromCapture_UsesBase64ForEncodedBodyEvenWhenBytesAreValidUtf8()
        {
            var draft = RequestReplayDraft.FromCapture(
                Capture("POST", "/", "encoded"u8.ToArray(), "Content-Encoding: custom\r\n"));
            Assert.True(draft.BodyIsBase64);
        }

        [Fact]
        public async Task SetBodyFormat_RoundTripsUtf8WithoutChangingBytes()
        {
            var draft = new RequestReplayDraft { Url = "https://example.com/", BodyText = "caf\u00e9\ntext" };
            draft.SetBodyFormat(true);
            Assert.True(draft.BodyIsBase64);
            using var request = draft.CreateRequest();
            Assert.Equal(Encoding.UTF8.GetBytes("caf\u00e9\ntext"), await request.Content.ReadAsByteArrayAsync());

            draft.SetBodyFormat(false);
            Assert.False(draft.BodyIsBase64);
            Assert.Equal("caf\u00e9\ntext", draft.BodyText);
        }

        [Fact]
        public void SetBodyFormat_RejectsLossyBinaryConversionWithoutChangingDraft()
        {
            var draft = RequestReplayDraft.FromCapture(Capture("POST", "/", [255]));
            string original = draft.BodyText;
            Assert.Throws<DecoderFallbackException>(() => draft.SetBodyFormat(false));
            Assert.True(draft.BodyIsBase64);
            Assert.Equal(original, draft.BodyText);
        }

        [Fact]
        public void CreateRequest_RejectsInvalidBase64()
        {
            var draft = new RequestReplayDraft
            {
                Url = "https://example.com/",
                BodyIsBase64 = true,
                BodyText = "not base64!"
            };
            Assert.Throws<FormatException>(() => draft.CreateRequest());
        }

        [Fact]
        public void CanReplay_DisallowsConnectAndWebSocketsButAllowsImportedHttp()
        {
            Assert.False(RequestReplayDraft.CanReplay(null));
            Assert.False(RequestReplayDraft.CanReplay(Capture("CONNECT", "example.com:443", [])));
            Assert.False(RequestReplayDraft.CanReplay(Capture("GET", "/", [], "Upgrade: websocket\r\n")));
            Assert.True(RequestReplayDraft.CanReplay(Capture("POST", "/", [])));
        }

        [Fact]
        public void FromCapture_PreservesAbsoluteUriAndCreateRequestAcceptsIpv6()
        {
            var absolute = Capture("GET", "https://example.com:8443/absolute?q=1", []);
            Assert.Equal(absolute.Path, RequestReplayDraft.FromCapture(absolute).Url);

            var draft = new RequestReplayDraft { Url = "http://[::1]:8080/test" };
            using var request = draft.CreateRequest();
            Assert.Equal("[::1]", request.RequestUri.Host);
        }

        [Fact]
        public void ToCurlCommand_QuotesPowerShellArgumentsAndUsesBinarySafeBodyFile()
        {
            var draft = new RequestReplayDraft
            {
                Url = "https://example.com/path?name=O'Neil&x=$value",
                Method = "POST",
                HeadersText = "X-Name: O'Neil; $(bad)\r\n",
                BodyIsBase64 = true,
                BodyText = "AP8="
            };

            string command = draft.ToCurlCommand();

            Assert.Contains("curl.exe --globoff --noproxy '*'", command);
            Assert.Contains("--request 'POST'", command);
            Assert.Contains("O''Neil", command);
            Assert.Contains("--header 'X-Name: O''Neil; $(bad)'", command);
            Assert.Contains("[Convert]::FromBase64String('AP8=')", command);
            Assert.Contains("--data-binary \"@$xmatBodyFile\"", command);
            Assert.Contains("finally", command);
            Assert.Contains("[System.IO.File]::Delete($xmatBodyFile)", command);
            Assert.DoesNotContain("--location", command);
            Assert.DoesNotContain("--insecure", command);
        }

        [Fact]
        public void ToCurlCommand_ValidatesDraftBeforeExport()
        {
            var draft = new RequestReplayDraft { Url = "file:///C:/capture" };
            Assert.Throws<ArgumentException>(() => draft.ToCurlCommand());
        }

        [Fact]
        public void ToCurlCommand_HandlesHeadAndEmptyHeaderValues()
        {
            var draft = new RequestReplayDraft
            {
                Method = "HEAD",
                Url = "https://example.com/",
                HeadersText = "X-Empty:\r\n"
            };
            string command = draft.ToCurlCommand();
            Assert.Contains(" --head --request ", command);
            Assert.Contains("--header 'X-Empty;'", command);
            Assert.Contains("--header 'Content-Type:'", command);
        }

        [Fact]
        public async Task ToCurlCommand_PowerShellReceivesExactArgumentsAndCleansUpBinaryFile()
        {
            var draft = new RequestReplayDraft
            {
                Url = "https://example.com/path?a=1&b=2",
                Method = "POST",
                HeadersText = "X-Test: O'Neil; \"quoted\" $(notExecuted)\r\n",
                BodyIsBase64 = true,
                BodyText = "AP8NCg=="
            };
            string script = """
                $ErrorActionPreference = 'Stop'
                function curl.exe {
                    $script:receivedArguments = @($args)
                    $index = [Array]::IndexOf($script:receivedArguments, '--data-binary')
                    $script:receivedPath = $script:receivedArguments[$index + 1].Substring(1)
                    $script:receivedBody = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($script:receivedPath))
                }
                """ + "\r\n" + draft.ToCurlCommand() + """

                [pscustomobject]@{
                    Arguments = $script:receivedArguments
                    Body = $script:receivedBody
                    Exists = [System.IO.File]::Exists($script:receivedPath)
                } | ConvertTo-Json -Compress
                """;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-EncodedCommand");
            process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            process.Start();
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(process.ExitCode == 0, await errors);
                using var document = JsonDocument.Parse(await output);
                var root = document.RootElement;
                Assert.Equal(draft.BodyText, root.GetProperty("Body").GetString());
                Assert.False(root.GetProperty("Exists").GetBoolean());
                var arguments = root.GetProperty("Arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert.Contains(draft.Url, arguments);
                Assert.Contains("X-Test: O'Neil; \"quoted\" $(notExecuted)", arguments);
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }

        internal static ProxyConnectionModel Capture(
            string method, string path, byte[] body, string headers = "")
        {
            return new ProxyConnectionModel(7, "200", DateTime.UtcNow, TimeSpan.FromSeconds(1),
                "https", "example.com:8443", method, path,
                $"{method} {path} HTTP/1.1\r\n{headers}\r\n", body,
                "HTTP/1.1 200 OK\r\n\r\n", "old response"u8.ToArray(), "127.0.0.1");
        }
    }
}
