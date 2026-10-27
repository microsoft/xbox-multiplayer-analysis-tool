// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using Xunit.Abstractions;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class SanitizedExportFormatTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _directory = Path.Join(Path.GetTempPath(), "XMAT-export-formats-" + Guid.NewGuid().ToString("N"));

        public SanitizedExportFormatTests(ITestOutputHelper output)
        {
            _output = output;
            Directory.CreateDirectory(_directory);
            Localization.LoadLanguage("en-us");
        }

        [Theory]
        [InlineData("{\"profileUsers\":[{\"id\":\"2533274900000027\",\"settings\":[]}]}", "2533274900000027")]
        [InlineData("{\"profileUsers\":[{\"id\":2533274900000027}],\"message\":\"2533274900000027\"}", "2533274900000027")]
        [InlineData("{\"profileUsers\":[{\"settings\":[{\"id\":\"AppDisplayName\",\"value\":\"DisplayCanary\"}]}]}", "DisplayCanary")]
        [InlineData("{\"profileUsers\":[{\"id\":\"2533274900000027\",\"settings\":[]}],\"xuid\":\"2533274900000027\"}", "2533274900000027")]
        [InlineData("{\"profileUsers\":[{\"settings\":[{\"id\":\"AppDisplayName\",\"value\":\"DisplayCanary\"}]}],\"Gamertag\":\"DisplayCanary\",\"GameDisplayName\":\"DisplayCanary\"}", "DisplayCanary")]
        public void XboxProfileIdentities_AreAbsentFromWrittenArchives(string json, string canary)
        {
            var evidence = Export("application/json", json);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void GenuineChunkFraming_CannotHideASplitPasswordAssignment(bool requestBody)
        {
            var body = Chunk("pass", "word=chunk-canary");
            var evidence = Export("text/plain", body, requestBody: requestBody,
                extraRequestHeaders: requestBody ? "Transfer-Encoding: chunked\r\n" : "",
                extraResponseHeaders: requestBody ? "" : "Transfer-Encoding: chunked\r\n");

            Assert.DoesNotContain("Transfer-Encoding:", Encoding.ASCII.GetString(evidence.Entries["raw/1_c.txt"]));
            Assert.DoesNotContain("chunk-canary", evidence.DecodedContent);
        }

        [Fact]
        public void AlreadyDechunkedCapture_StillSanitizesWithoutBeingOmitted()
        {
            var evidence = Export("text/plain", "password=dechunked-canary", requestBody: true,
                extraRequestHeaders: "Transfer-Encoding: chunked\r\n");

            Assert.DoesNotContain("dechunked-canary", evidence.DecodedContent);
            Assert.Equal(0, evidence.Summary.OmittedBodies);
        }

        [Theory]
        [InlineData("https://profile.test/users/%78uid%282533274900000027%29", "2533274900000027")]
        [InlineData("https://login.test/userrealm/dana%2Ecanary%40example%2Etest", "dana.canary@example.test")]
        public void PercentEncodedIdentityPaths_AreAbsentAfterDecodingTheArchive(string target, string canary)
        {
            var evidence = Export("text/plain", string.Empty, target: target);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        [Fact]
        public void RepeatedlyEncodedNestedUriCredentials_AreAbsentAfterDecoding()
        {
            var nested = Uri.EscapeDataString(Uri.EscapeDataString(
                "https://login.test/callback?access_token=nested-canary"));
            var evidence = Export("application/x-www-form-urlencoded", "next=" + nested, requestBody: true);

            Assert.DoesNotContain("nested-canary", evidence.DecodedContent);
        }

        [Theory]
        [InlineData("4\r\npass\r\n10\r\nword=chunk-canary\r\n0\r\n")]
        [InlineData("Z\r\npass\r\n10\r\nword=chunk-canary\r\n0\r\n\r\n")]
        [InlineData(" 4\r\npass\r\n10\r\nword=chunk-canary\r\n0\r\n\r\n")]
        [InlineData("4\r\npass\r\n10\r\nword=chunk-canary\r\n0\r\n\r\nextra")]
        [InlineData("4\r\npass\r\n10\r\nword=chunk-canary\r\n0\r\nMalformedTrailer\r\n\r\n")]
        public void AmbiguousOrMalformedChunkFraming_IsOmitted(string body)
        {
            var evidence = Export("text/plain", body, extraResponseHeaders: "Transfer-Encoding: chunked\r\n");

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("chunk-canary", evidence.DecodedContent);
            Assert.Empty(Body(evidence));
        }

        [Fact]
        public void ChunkExtensionsAndTrailers_AreRemovedBeforeInspectingThePayload()
        {
            var body = Chunk("pass", "word=chunk-canary")
                .Replace("4\r\n", "4;extension=extension-canary\r\n", StringComparison.Ordinal)
                .Replace("0\r\n\r\n", "0\r\nAuthorization: trailer-canary\r\n\r\n", StringComparison.Ordinal);
            var evidence = Export("text/plain", body, extraResponseHeaders: "Transfer-Encoding: chunked\r\nTrailer: Authorization\r\n");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("chunk-canary", evidence.DecodedContent);
            Assert.DoesNotContain("extension-canary", evidence.DecodedContent);
            Assert.DoesNotContain("trailer-canary", evidence.DecodedContent);
            Assert.Contains(SazSanitizer.Redacted, Encoding.UTF8.GetString(Body(evidence)));
        }

        [Fact]
        public void ChunkedCompression_IsDecodedInTransferThenContentOrder()
        {
            using var compressed = new MemoryStream();
            using (var encoder = new GZipStream(compressed, CompressionLevel.Fastest, true))
            {
                encoder.Write(Encoding.UTF8.GetBytes("password=compressed-chunk-canary;keep=visible"));
            }
            var bytes = compressed.ToArray();
            using var framed = new MemoryStream();
            foreach (var chunk in bytes.Chunk(5))
            {
                framed.Write(Encoding.ASCII.GetBytes(chunk.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n"));
                framed.Write(chunk);
                framed.Write("\r\n"u8);
            }
            framed.Write("0\r\n\r\n"u8);
            var evidence = Export("text/plain", framed.ToArray(),
                extraResponseHeaders: "Transfer-Encoding: chunked\r\nContent-Encoding: gzip\r\n");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("compressed-chunk-canary", evidence.DecodedContent);
            Assert.Contains("keep=visible", evidence.DecodedContent);
        }

        [Theory]
        [InlineData("0\r\n\r\n")]
        [InlineData("{\r\n\"visible\":\"keep\"\r\n}")]
        public void EmptyChunksAndAlreadyDechunkedJson_DoNotCauseAnOmission(string body)
        {
            var evidence = Export("application/json", body, extraResponseHeaders: "Transfer-Encoding: chunked\r\n");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            if (body[0] == '{')
            {
                using var document = JsonDocument.Parse(Body(evidence));
                Assert.Equal("keep", document.RootElement.GetProperty("visible").GetString());
            }
            else
            {
                Assert.Empty(Body(evidence));
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(8)]
        public void RepeatedlyEncodedParameterNamesAndCredentials_AreInspected(int depth)
        {
            var name = "access%5Ftoken";
            var value = "https://login.test/?password=encoded-canary";
            for (var index = 0; index < depth; index++)
            {
                if (index > 0)
                {
                    name = Uri.EscapeDataString(name);
                }
                value = Uri.EscapeDataString(value);
            }
            var evidence = Export("application/x-www-form-urlencoded",
                name + "=" + value + "&keep=visible", requestBody: true);

            Assert.DoesNotContain("encoded-canary", evidence.DecodedContent);
            Assert.Contains("keep=visible", evidence.DecodedContent);
        }

        [Fact]
        public void ExcessiveEncodingNesting_IsOmittedInsteadOfCopiedUnchecked()
        {
            var value = "https://login.test/?password=overencoded-canary";
            for (var index = 0; index < 12; index++)
            {
                value = Uri.EscapeDataString(value);
            }
            var evidence = Export("application/x-www-form-urlencoded", "next=" + value, requestBody: true);

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("overencoded-canary", evidence.DecodedContent);
        }

        [Fact]
        public void UntouchedEncodedValuesAndLiteralPluses_KeepTheirOriginalRepresentation()
        {
            const string body = "next=https%253A%252F%252Fpublic.test%252Fview%253Fkeep%253DA%25252BB&keep=A%2BB+C";
            var evidence = Export("application/x-www-form-urlencoded", body, requestBody: true,
                target: "/public/%41%2Bpath?keep=A%2BB+C");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(body, Encoding.UTF8.GetString(Body(evidence, true)));
            Assert.StartsWith("POST /public/%41%2Bpath?keep=A%2BB+C HTTP/1.1",
                Encoding.ASCII.GetString(evidence.Entries["raw/1_c.txt"]));
        }

        [Fact]
        public void Xhtml_IsOmittedLikeTheDocumentedHtmlPolicy()
        {
            var evidence = Export("application/xhtml+xml",
                "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><input type=\"password\" value=\"xhtml-canary\"/></body></html>");

            Assert.DoesNotContain("xhtml-canary", evidence.DecodedContent);
            Assert.Equal(1, evidence.Summary.OmittedBodies);
        }

        [Theory]
        [InlineData("<html><input type=\"password\" value=\"html-canary\"/></html>")]
        [InlineData("<div xmlns=\"http://www.w3.org/1999/xhtml\"><input type=\"password\" value=\"html-canary\"/></div>")]
        public void HtmlDisguisedAsGenericXml_IsAlsoOmitted(string body)
        {
            var evidence = Export("application/xml", body);

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("html-canary", evidence.DecodedContent);
        }

        [Fact]
        public void ValidJwtWithNonEyJHeader_IsAbsentFromWrittenArchives()
        {
            var token = Jwt();
            Assert.False(token.StartsWith("eyJ", StringComparison.Ordinal));
            var evidence = Export("application/json", JsonSerializer.Serialize(new { message = token }));

            Assert.DoesNotContain(token, evidence.DecodedContent);
        }

        [Fact]
        public void StandaloneXboxCredential_IsAbsentFromWrittenArchives()
        {
            var token = Jwt();
            const string hash = "2533274900000027";
            var evidence = Export("text/plain", "XBL3.0 x=" + hash + ";" + token);

            Assert.DoesNotContain(token, evidence.DecodedContent);
            Assert.DoesNotContain(hash, evidence.DecodedContent);
        }

        [Fact]
        public void JwtPunctuationAndUnsecuredJwt_AreRecognizedWithoutMaskingOrdinaryDottedText()
        {
            var signed = Jwt();
            var unsigned = Convert.ToBase64String(Encoding.UTF8.GetBytes(" {\"alg\":\"none\"}")).TrimEnd('=') +
                "." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"unsigned-canary\"}")).TrimEnd('=') + ".";
            var evidence = Export("text/plain", signed + ". " + unsigned + " public.example.test version.1.2");

            Assert.DoesNotContain(signed, evidence.DecodedContent);
            Assert.DoesNotContain(unsigned, evidence.DecodedContent);
            Assert.Contains("public.example.test version.1.2", evidence.DecodedContent);
        }

        [Fact]
        public void AuthorizationHeader_RemovesTheSameCredential()
        {
            var token = Jwt();
            var evidence = Export("text/plain", string.Empty,
                extraRequestHeaders: "Authorization: XBL3.0 x=2533274900000027;" + token + "\r\n");

            Assert.DoesNotContain(token, evidence.DecodedContent);
            Assert.DoesNotContain("2533274900000027", evidence.DecodedContent);
        }

        [Fact]
        public void XmlNamespaceUri_CannotRetainAnAccessToken()
        {
            var evidence = Export("application/xml",
                "<s:root xmlns:s=\"https://schema.test/model?access_token=namespace-canary\"><s:keep>visible</s:keep></s:root>");

            Assert.DoesNotContain("namespace-canary", evidence.DecodedContent);
        }

        [Fact]
        public void NamespaceRemapping_PreservesBoundElementAndAttributeNamesAndPublicNamespaces()
        {
            var evidence = Export("application/xml",
                "<root xmlns=\"https://schema.test/?access_token=default-canary\" " +
                "xmlns:p=\"https://schema.test/?access_token=prefix-canary\" xmlns:q=\"urn:public\">" +
                "<p:item p:label=\"visible\" q:label=\"public\"><q:keep>keep</q:keep></p:item></root>");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("default-canary", evidence.DecodedContent);
            Assert.DoesNotContain("prefix-canary", evidence.DecodedContent);
            var root = XDocument.Parse(Encoding.UTF8.GetString(Body(evidence))).Root;
            var item = Assert.Single(root.Elements());
            Assert.StartsWith("urn:xmat:sanitized-namespace:", root.Name.NamespaceName);
            Assert.Equal(item.Name.Namespace, item.GetNamespaceOfPrefix("p"));
            Assert.Equal("visible", (string)item.Attribute(item.Name.Namespace + "label"));
            Assert.Equal("public", (string)item.Attribute(XNamespace.Get("urn:public") + "label"));
            Assert.Equal("keep", item.Element(XNamespace.Get("urn:public") + "keep").Value);
        }

        [Fact]
        public void NamespaceUri_AlsoRedactsKnownStructuredCredentialEchoes()
        {
            var evidence = Export("application/xml",
                "<root xmlns=\"https://schema.test/ECHO-ABCD\"><user_code>ECHO-ABCD</user_code><keep>visible</keep></root>");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("ECHO-ABCD", evidence.DecodedContent);
            Assert.Contains("visible", evidence.DecodedContent);
            Assert.StartsWith("urn:xmat:sanitized-namespace:",
                XDocument.Parse(Encoding.UTF8.GetString(Body(evidence))).Root.Name.NamespaceName);
        }

        [Fact]
        public void BracedOdbcPassword_DoesNotLeaveTheTailAfterASemicolon()
        {
            var evidence = Export("text/plain", "Driver={SQL Server};Server=db.test;PWD={first;odbc-canary}");
            Assert.DoesNotContain("odbc-canary", evidence.DecodedContent);
        }

        [Theory]
        [InlineData("PWD={first}};odbc-canary};keep=visible")]
        [InlineData("Password=\n multiline-canary\nkeep=visible")]
        [InlineData("password:\r\n\tmultiline-canary\r\nkeep=visible")]
        public void EscapedBracesAndMultilineAssignments_RedactTheWholeValue(string body)
        {
            var evidence = Export("text/plain", body);

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("odbc-canary", evidence.DecodedContent);
            Assert.DoesNotContain("multiline-canary", evidence.DecodedContent);
            Assert.Contains("keep=visible", evidence.DecodedContent);
        }

        [Fact]
        public void PasswordValueOnTheFollowingLine_IsAbsentFromWrittenArchives()
        {
            var evidence = Export("text/plain", "password=\r\nmultiline-canary\r\nkeep: visible");
            Assert.DoesNotContain("multiline-canary", evidence.DecodedContent);
        }

        [Fact]
        public void DroppedMalformedAuthorization_DoesNotReattachItsContinuationToHost()
        {
            var evidence = Export("text/plain", string.Empty,
                extraRequestHeaders: "Authorization\r\n folded-canary\r\nX-Keep: visible\r\n");

            Assert.DoesNotContain("folded-canary", evidence.DecodedContent);
            Assert.Equal(2, evidence.Summary.OmittedHeaders);
            Assert.Contains("Host: example.test\r\nX-Keep: visible", evidence.DecodedContent);
        }

        [Fact]
        public void RecognizedOAuthUserCode_IsAlsoRemovedFromItsHumanReadableEcho()
        {
            const string code = "ABCD-EFGH";
            var evidence = Export("application/json", JsonSerializer.Serialize(new
            {
                user_code = code,
                message = "To sign in, open the login page and enter the code " + code + " to authenticate."
            }));

            Assert.DoesNotContain(code, evidence.DecodedContent);
        }

        [Theory]
        [InlineData("application/json", "{\"message\":\"Enter ECHO-ABCD to authenticate\",\"user_code\":\"ECHO-ABCD\",\"keep\":\"visible\"}")]
        [InlineData("application/xml", "<root><message>Enter ECHO-ABCD to authenticate</message><user_code>ECHO-ABCD</user_code><keep>visible</keep></root>")]
        [InlineData("application/xml", "<root><message>Enter ECHO-ABCD to authenticate</message><setting><name>user_code</name><value>ECHO-ABCD</value></setting><keep>visible</keep></root>")]
        public void KnownCredentialEchoes_AreRedactedEvenBeforeTheirSensitiveField(string type, string body)
        {
            var evidence = Export(type, body);

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("ECHO-ABCD", evidence.DecodedContent);
            Assert.Contains("visible", evidence.DecodedContent);
        }

        [Fact]
        public void ExcessiveDistinctCredentialValues_AreOmittedUnderTheInspectionLimit()
        {
            var values = Enumerable.Range(0, 65).Select(index => new { user_code = $"CODE-{index:D4}" });
            var evidence = Export("application/json", JsonSerializer.Serialize(values));

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("CODE-", evidence.DecodedContent);
        }

        [Fact]
        public void PgpPrivateKeyArmor_IsAbsentFromWrittenArchives()
        {
            var armor = "-----BEGIN " + "PGP PRIVATE KEY BLOCK-----\r\n\r\n" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes("private-pgp-canary")) +
                "\r\n-----END " + "PGP PRIVATE KEY BLOCK-----";
            var evidence = Export("text/plain", armor);

            Assert.DoesNotContain("PGP PRIVATE KEY BLOCK", evidence.DecodedContent);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("private-pgp-canary")), evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, Encoding.UTF8.GetString(Body(evidence)));
        }

        [Fact]
        public void RepeatedOverlappingAssignments_AreInspectedWithinTheWorkBudget()
        {
            Export("text/plain", "password=warmup");
            foreach (var count in new[] { 4096, 8192, 16384 })
            {
                var body = string.Concat(Enumerable.Repeat("password=", count)) + "cost-canary";
                var watch = Stopwatch.StartNew();
                var evidence = Export("text/plain", body);
                watch.Stop();
                _output.WriteLine($"{count} assignments, {Encoding.UTF8.GetByteCount(body)} bytes: {watch.Elapsed.TotalMilliseconds:F1} ms");
                Assert.DoesNotContain("cost-canary", evidence.DecodedContent);
                Assert.Equal(0, evidence.Summary.OmittedBodies);
            }
        }

        [Theory]
        [InlineData("before-first")]
        [InlineData("between-records")]
        [InlineData("after-last")]
        public void CancelledArchiveExport_DisposesTheSourceAndNeverReplacesTheDestination(string point)
        {
            Export("text/plain", "visible");
            var path = Assert.Single(Directory.GetFiles(_directory, "*.saz"));
            var original = File.ReadAllBytes(path);
            var record = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            var disposed = false;
            IEnumerable<ProxyConnectionModel> Records()
            {
                try
                {
                    if (point == "before-first")
                    {
                        cancellation.Cancel();
                    }
                    yield return record;
                    cancellation.Cancel();
                    if (point == "between-records")
                    {
                        yield return record;
                    }
                }
                finally
                {
                    disposed = true;
                }
            }

            Assert.ThrowsAny<OperationCanceledException>(() =>
                FiddlerSazHandler.CreateSanitizedSazFile(path, Records(), cancellation.Token));

            Assert.True(disposed);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
            var retained = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal("visible", Encoding.UTF8.GetString(retained.ResponseBody));
        }

        [Fact]
        public void AlreadyCancelledExport_DoesNotOpenTheSourceOrCreateATemporaryArchive()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var enumerated = false;
            IEnumerable<ProxyConnectionModel> Records()
            {
                enumerated = true;
                yield break;
            }

            Assert.ThrowsAny<OperationCanceledException>(() => FiddlerSazHandler.CreateSanitizedSazFile(
                Path.Join(_directory, "cancelled.saz"), Records(), cancellation.Token));

            Assert.False(enumerated);
            Assert.Empty(Directory.GetFiles(_directory));
        }

        private Evidence Export(string type, string body, bool requestBody = false, string target = "/",
            string extraRequestHeaders = "", string extraResponseHeaders = "") =>
            Export(type, Encoding.UTF8.GetBytes(body), requestBody, target, extraRequestHeaders, extraResponseHeaders);

        private Evidence Export(string type, byte[] body, bool requestBody = false, string target = "/",
            string extraRequestHeaders = "", string extraResponseHeaders = "")
        {
            var path = Path.Join(_directory, Guid.NewGuid().ToString("N") + ".saz");
            var method = requestBody ? "POST" : "GET";
            var record = new ProxyConnectionModel(1, "200",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMilliseconds(20),
                "https", "example.test:443", method, target,
                method + " " + target + " HTTP/1.1\r\nHost: example.test\r\n" + extraRequestHeaders +
                (requestBody ? "Content-Type: " + type + "\r\n" : string.Empty) + "\r\n",
                requestBody ? body : Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\n" + extraResponseHeaders + "\r\n",
                requestBody ? Array.Empty<byte>() : body, "192.0.2.27")
            {
                RecordRowId = 101
            };
            var original = body.ToArray();
            var summary = FiddlerSazHandler.CreateSanitizedSazFile(path, new[] { record });
            Assert.Equal(original, requestBody ? record.RequestBody : record.ResponseBody);
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries.ToDictionary(entry => entry.FullName, entry =>
            {
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                return buffer.ToArray();
            });
            Assert.Equal(new[] { "raw/1_c.txt", "raw/1_m.xml", "raw/1_s.txt" },
                entries.Keys.OrderBy(name => name, StringComparer.Ordinal));
            var bodies = new Dictionary<string, byte[]>();
            foreach (var entry in entries.Where(item => item.Key.EndsWith(".txt", StringComparison.Ordinal)))
            {
                var separator = entry.Value.AsSpan().IndexOf(new byte[] { 13, 10, 13, 10 });
                Assert.True(separator >= 0);
                var headers = Encoding.ASCII.GetString(entry.Value, 0, separator);
                Assert.DoesNotContain("Transfer-Encoding:", headers, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Content-Encoding:", headers, StringComparison.OrdinalIgnoreCase);
                var length = headers.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.Ordinal));
                Assert.Equal(entry.Value.Length - separator - 4,
                    int.Parse(length.Substring("Content-Length:".Length), CultureInfo.InvariantCulture));
                bodies.Add(entry.Key, entry.Value.AsSpan(separator + 4).ToArray());
            }
            var metadata = XDocument.Parse(Encoding.UTF8.GetString(entries["raw/1_m.xml"]));
            Assert.Equal("101", (string)metadata.Root.Attribute("SID"));
            Assert.Equal("2100", (string)metadata.Root.Attribute("BitFlags"));
            Assert.Equal(16, metadata.Root.Element("SessionTimers").Attributes().Count());
            Assert.Contains(metadata.Descendants("SessionFlag"), flag =>
                (string)flag.Attribute("N") == "x-clientip" && (string)flag.Attribute("V") == "0.0.0.0");
            var flags = metadata.Descendants("SessionFlag").ToArray();
            Assert.Equal(summary.OmittedBodies,
                flags.Count(flag => ((string)flag.Attribute("N")).EndsWith("-body-omitted", StringComparison.Ordinal)));
            Assert.All(flags, flag => Assert.Contains((string)flag.Attribute("N"), new[]
            {
                "x-clientip", "x-xmat-request-body-omitted", "x-xmat-response-body-omitted"
            }));
            var reimported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal("200", reimported.Status);
            Assert.Equal(method, reimported.Method);
            Assert.Equal("0.0.0.0", reimported.ClientIP);
            Assert.Equal(record.RequestTime, reimported.RequestTime.ToUniversalTime());
            Assert.Equal(record.Duration, reimported.Duration);
            Assert.Equal(bodies["raw/1_c.txt"], reimported.RequestBody);
            Assert.Equal(bodies["raw/1_s.txt"], reimported.ResponseBody);
            var all = string.Join("\n", entries.Values.Select(Encoding.UTF8.GetString));
            for (var pass = 0; pass < 12; pass++)
            {
                var decoded = WebUtility.UrlDecode(WebUtility.HtmlDecode(all));
                if (decoded == all)
                {
                    break;
                }
                all = decoded;
            }
            return new(all, summary, entries);
        }

        private static byte[] Body(Evidence evidence, bool request = false)
        {
            var message = evidence.Entries[request ? "raw/1_c.txt" : "raw/1_s.txt"];
            return message.AsSpan(message.AsSpan().IndexOf("\r\n\r\n"u8) + 4).ToArray();
        }

        private static string Jwt()
        {
            static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var header = Encode(Encoding.UTF8.GetBytes(" {\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            var claims = Encode(Encoding.UTF8.GetBytes("{\"sub\":\"synthetic-subject\"}"));
            var input = header + "." + claims;
            var signature = HMACSHA256.HashData(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
                Encoding.ASCII.GetBytes(input));
            return input + "." + Encode(signature);
        }

        private sealed record Evidence(string DecodedContent, SanitizedExportSummary Summary, Dictionary<string, byte[]> Entries);

        private static string Chunk(params string[] values)
        {
            var body = new StringBuilder();
            foreach (var value in values)
            {
                body.Append(Encoding.UTF8.GetByteCount(value).ToString("X", CultureInfo.InvariantCulture))
                    .Append("\r\n").Append(value).Append("\r\n");
            }
            return body.Append("0\r\n\r\n").ToString();
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
