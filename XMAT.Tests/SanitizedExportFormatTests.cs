// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using XMAT.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;
using XMAT.Tests.Operations.Analysis;
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
        [InlineData("{\r\n\"visible\":\"keep\",\r\n\"count\":\r\n0\r\n}")]
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
        public void ExcessiveEncodingNesting_RedactsOnlyItsComponent()
        {
            var value = "https://login.test/?password=overencoded-canary";
            for (var index = 0; index < 12; index++)
            {
                value = Uri.EscapeDataString(value);
            }
            var evidence = Export("application/x-www-form-urlencoded", "next=" + value + "&keep=visible", requestBody: true);

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("overencoded-canary", evidence.DecodedContent);
            Assert.Contains(SazSanitizer.Redacted, evidence.DecodedContent);
            Assert.Contains("keep=visible", evidence.DecodedContent);
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

        // A malformed header line can no longer be dropped unread: with its continuation, it leaves the whole pair out.
        [Fact]
        public void MalformedAuthorizationAndItsContinuation_MakeThePairOpaque()
        {
            var evidence = Export("text/plain", string.Empty,
                extraRequestHeaders: "Authorization\r\n folded-canary\r\nX-Keep: visible\r\n");

            Assert.DoesNotContain("folded-canary", evidence.DecodedContent);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.DoesNotContain("X-Keep: visible", evidence.DecodedContent);
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
                Assert.Equal(1, evidence.Summary.OmittedBodies);
                Assert.Equal(0, evidence.Summary.WorkBudgetOmissions);
            }
        }

        [Fact]
        public void RequestCredentials_AreRemovedFromThePairedResponseButNotTheNextSession()
        {
            const string canary = "PAIR-CREDENTIAL-CANARY";
            var first = PairRecord(1, "{\"password\":\"" + canary + "\"}",
                "{\"error\":\"invalid credential " + canary + "\"}");
            var second = PairRecord(2, "{\"diagnostic\":\"" + canary + "\"}", "{\"keep\":\"visible\"}");
            var evidence = ExportRecords(first, second);

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, Encoding.UTF8.GetString(Body(evidence, true)));
            Assert.DoesNotContain(canary, Encoding.UTF8.GetString(Body(evidence)));
            Assert.Contains(canary, Encoding.UTF8.GetString(Body(evidence, true, 2)));
            Assert.Contains("visible", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        [Theory]
        [InlineData("Q7Z")]
        [InlineData("1")]
        [InlineData("200")]
        public void ShortCredentialEchoes_UseBoundariesWithoutChangingProtocolOrUnrelatedValues(string code)
        {
            var evidence = Export("application/json",
                JsonSerializer.Serialize(new { pin = code, message = "Enter PIN " + code + " now", port = "12345" }),
                requestBody: true,
                pairedResponse: JsonSerializer.Serialize(new { message = "Invalid PIN " + code + " supplied" }));
            using var request = JsonDocument.Parse(Body(evidence, true));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(SazSanitizer.Redacted, request.RootElement.GetProperty("pin").GetString());
            Assert.Equal("Enter PIN " + SazSanitizer.Redacted + " now", request.RootElement.GetProperty("message").GetString());
            Assert.Equal("Invalid PIN " + SazSanitizer.Redacted + " supplied", response.RootElement.GetProperty("message").GetString());
            Assert.Equal("12345", request.RootElement.GetProperty("port").GetString());
            Assert.StartsWith("HTTP/1.1 200 ", Encoding.ASCII.GetString(evidence.Entries["raw/1_s.txt"]));
        }

        [Theory]
        [InlineData("1234", "12345")]
        [InlineData("data", "metadata")]
        [InlineData("a", "cat")]
        public void LearnedSecrets_DoNotChangeLongerTokensOrJsonPropertyNames(string secret, string unrelated)
        {
            var evidence = Export("application/json",
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["secret"] = secret,
                    ["metadata"] = 1,
                    ["copy_" + unrelated] = unrelated,
                    ["message"] = "value " + secret + " supplied"
                }), requestBody: true);
            using var document = JsonDocument.Parse(Body(evidence, true));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(1, document.RootElement.GetProperty("metadata").GetInt32());
            Assert.Equal(unrelated, document.RootElement.GetProperty("copy_" + unrelated).GetString());
            Assert.Equal("value " + SazSanitizer.Redacted + " supplied", document.RootElement.GetProperty("message").GetString());
        }

        [Theory]
        [InlineData(32, 0)]
        [InlineData(33, 2)]
        public void RequestAndResponse_ShareTheBoundedCredentialCapacity(int responseCount, int omissions)
        {
            var request = JsonSerializer.Serialize(Enumerable.Range(0, 32).Select(index => new { secret = $"REQ-{index:D2}" }));
            var response = JsonSerializer.Serialize(Enumerable.Range(0, responseCount).Select(index => new { secret = $"RESP-{index:D2}" }));
            var evidence = Export("application/json", request, requestBody: true, pairedResponse: response);

            Assert.Equal(omissions, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("REQ-", evidence.DecodedContent);
            Assert.DoesNotContain("RESP-", evidence.DecodedContent);
            if (omissions == 0)
            {
                using var document = JsonDocument.Parse(Body(evidence));
                Assert.Equal(responseCount, document.RootElement.GetArrayLength());
            }
            else
            {
                Assert.Empty(Body(evidence, true));
                Assert.Empty(Body(evidence));
            }
        }

        [Fact]
        public void RequestCredentialOverflow_OmitsThePairedEchoAndResetsBeforeTheNextSession()
        {
            var request = JsonSerializer.Serialize(Enumerable.Range(0, 65).Select(index => new { secret = $"CAP-{index:D3}" }));
            var evidence = ExportRecords(
                PairRecord(1, request, "{\"message\":\"CAP-064\"}"),
                PairRecord(2, "{\"keep\":\"visible\"}", "{\"keep\":\"visible\"}"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("CAP-", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.Contains("visible", Encoding.UTF8.GetString(Body(evidence, true, 2)));
            Assert.Contains("visible", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        [Fact]
        public void AnOmittedResponseBody_DoesNotForgetRequestSecretsInResponseHeaders()
        {
            var evidence = Export("application/json", "{\"password\":\"REQUEST-ALIAS-CANARY\"}",
                requestBody: true, pairedResponse: "{malformed",
                extraResponseHeaders: "X-Echo: REQUEST-ALIAS-CANARY\r\n");

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("REQUEST-ALIAS-CANARY", evidence.DecodedContent);
            Assert.Contains("X-Echo: " + SazSanitizer.Redacted, evidence.DecodedContent);
        }

        [Theory]
        [InlineData("0;x=y\r\n\r\n")]
        [InlineData("00;x=y\r\n\r\n")]
        [InlineData("0;x=y")]
        public void MalformedChunkingWithExtendedZeroTerminators_IsOmitted(string terminator)
        {
            var evidence = Export("text/plain",
                "Z\r\n4\r\npass\r\n11\r\nword=extended-chunk-canary\r\n" + terminator,
                requestBody: true, extraRequestHeaders: "Transfer-Encoding: chunked\r\n");

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("extended-chunk-canary", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
        }

        [Theory]
        [InlineData("form")]
        [InlineData("path")]
        [InlineData("query")]
        [InlineData("referer")]
        [InlineData("location")]
        public void MixedValidAndInvalidEscapes_AreFullyInspectedWithinTheirComponent(string component)
        {
            const string encoded = "private%2540unit.example%ZZ";
            var evidence = Export(
                component == "form" ? "application/x-www-form-urlencoded" : "text/plain",
                component == "form" ? "message=" + encoded + "&keep=visible" : "keep=visible",
                requestBody: true,
                target: component == "path" ? "/person/" + encoded + "?keep=visible" :
                    component == "query" ? "/?message=" + encoded + "&keep=visible" : "/",
                extraRequestHeaders: component == "referer" ? "Referer: https://example.test/?message=" + encoded + "&keep=visible\r\n" : "",
                extraResponseHeaders: component == "location" ? "Location: https://example.test/?message=" + encoded + "&keep=visible\r\n" : "");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("private@unit.example", evidence.DecodedContent);
            Assert.Contains("keep=visible", evidence.DecodedContent);
        }

        [Theory]
        [InlineData("form")]
        [InlineData("path")]
        [InlineData("query")]
        [InlineData("name")]
        [InlineData("flag")]
        [InlineData("referer")]
        [InlineData("location")]
        public void InvalidUtf8Escapes_RedactTheirComponentWithoutAbortingTheArchive(string component)
        {
            var evidence = Export(
                component == "form" ? "application/x-www-form-urlencoded" : "text/plain",
                component == "form" ? "message=caf%E9&keep=visible" : "keep=visible",
                requestBody: true,
                target: component == "path" ? "/person/caf%E9?keep=visible" :
                    component == "query" ? "/?message=caf%E9&keep=visible" :
                    component == "name" ? "/?name%FF=invalid&keep=visible" :
                    component == "flag" ? "/?caf%E9&keep=visible" : "/",
                extraRequestHeaders: component == "referer" ? "Referer: https://example.test/?message=%FF&keep=visible\r\n" : "",
                extraResponseHeaders: component == "location" ? "Location: https://example.test/?message=%25FF&keep=visible\r\n" : "");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("%E9", evidence.DecodedContent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%FF", evidence.DecodedContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(SazSanitizer.Redacted, evidence.DecodedContent);
            Assert.Contains("keep=visible", evidence.DecodedContent);
        }

        [Fact]
        public void XmlPasswordInputs_RedactOnlyTheirValueAndItsEcho()
        {
            var evidence = Export("application/xml",
                "<form><message>Invalid XML-INPUT-CANARY supplied</message>" +
                "<input type=\"password\" value=\"XML-INPUT-CANARY\" id=\"login\"/>" +
                "<input type=\"text\" value=\"visible\"/></form>", requestBody: true);
            var root = XDocument.Parse(Encoding.UTF8.GetString(Body(evidence, true))).Root;
            var inputs = root.Elements("input").ToArray();

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("XML-INPUT-CANARY", evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, (string)inputs[0].Attribute("value"));
            Assert.Equal("password", (string)inputs[0].Attribute("type"));
            Assert.Equal("login", (string)inputs[0].Attribute("id"));
            Assert.Equal("visible", (string)inputs[1].Attribute("value"));
        }

        [Theory]
        [InlineData("cache.")]
        [InlineData("cache.prefix.")]
        [InlineData("a.b.c.")]
        [InlineData("longordinaryprefix.anotherlongprefix.")]
        public void PrefixedJwtWindows_AreRedactedWithoutChangingSurroundingDottedText(string prefix)
        {
            var token = Jwt();
            var evidence = Export("text/plain", prefix + token + ".tail a.b.c ab.cd.ef");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(token, evidence.DecodedContent);
            Assert.Equal(prefix + SazSanitizer.Redacted + ".tail a.b.c ab.cd.ef",
                Encoding.UTF8.GetString(Body(evidence)));
        }

        [Fact]
        public void PrefixedEncryptedJwtWindows_AreAlsoRecognized()
        {
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"dir\",\"enc\":\"A256GCM\"}"))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var token = header + "..aabbccdd.ee001122.ffeeddcc";
            var evidence = Export("text/plain", "cache.prefix." + token + ".tail a.b.c");

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(token, evidence.DecodedContent);
            Assert.Equal("cache.prefix." + SazSanitizer.Redacted + ".tail a.b.c",
                Encoding.UTF8.GetString(Body(evidence)));
        }

        [Fact]
        public void OrdinaryDottedText_UsesTheFastNegativeJwtPath()
        {
            const string pattern = "ab.cd.ef ";
            var body = string.Concat(Enumerable.Repeat(pattern, SazSanitizer.MaximumBodyBytes / pattern.Length));
            var watch = Stopwatch.StartNew();
            var evidence = Export("text/plain", body);
            watch.Stop();

            _output.WriteLine($"Dotted text, {body.Length} bytes: {watch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.True(body == Encoding.UTF8.GetString(Body(evidence)), "Ordinary dotted text changed.");
        }

        [Fact]
        public void ExhaustedInspectionWorkBudget_OmitsTheBodyAndMarksTheActualArchive()
        {
            var nested = "https://public.test/?next=https://public.test/path/" +
                new string('a', SazSanitizer.MaximumBodyBytes - 1024);
            for (var pass = 0; pass < 7; pass++)
            {
                nested = Uri.EscapeDataString(nested);
            }
            var body = "next=" + nested;
            Assert.True(Encoding.UTF8.GetByteCount(body) <= SazSanitizer.MaximumBodyBytes);
            var evidence = Export("application/x-www-form-urlencoded", body, requestBody: true);

            Assert.Equal(1, evidence.Summary.WorkBudgetOmissions);
            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.Empty(Body(evidence, true));
        }

        [Theory]
        [InlineData("email")]
        [InlineData("jwt")]
        [InlineData("signed-url")]
        [InlineData("signature")]
        [InlineData("xuid")]
        public void SensitiveJsonKeyContent_IsScannedWithoutDroppingBenignFields(string kind)
        {
            var key = kind switch
            {
                "email" => "member@unit.example",
                "jwt" => Jwt(),
                "signed-url" => "https://storage.test/path?sig=KEY-SAS-CANARY&keep=ok",
                "signature" => "sig=KEY-SIGNED-CANARY",
                _ => "xuid(2533274900000027)"
            };
            var request = JsonSerializer.Serialize(new
            {
                members = new Dictionary<string, object> { [key] = new { payload = "member-marker" } },
                keep = "public-marker"
            });
            var evidence = ExportRecords(PairRecord(1, request, "{\"keep\":\"public-marker\"}"));
            using var document = JsonDocument.Parse(Body(evidence, true));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.False(document.RootElement.GetProperty("members").TryGetProperty(key, out _));
            Assert.Equal("public-marker", document.RootElement.GetProperty("keep").GetString());
            var planted = kind switch
            {
                "signed-url" => "KEY-SAS-CANARY",
                "signature" => "KEY-SIGNED-CANARY",
                "xuid" => "2533274900000027",
                _ => key
            };
            Assert.DoesNotContain(planted, evidence.DecodedContent);
        }

        [Theory]
        [InlineData("data")]
        [InlineData("members")]
        [InlineData("id")]
        public void ExactLearnedSchemaNames_AreNotTreatedAsEchoValues(string key)
        {
            var request = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["secret"] = key,
                [key] = "schema-marker",
                ["metadata"] = 1
            });
            var evidence = ExportRecords(PairRecord(1, request, "{\"keep\":\"public-marker\"}"));
            using var document = JsonDocument.Parse(Body(evidence, true));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal("schema-marker", document.RootElement.GetProperty(key).GetString());
            Assert.Equal(1, document.RootElement.GetProperty("metadata").GetInt32());
            Assert.Equal(SazSanitizer.Redacted, document.RootElement.GetProperty("secret").GetString());
        }

        [Theory]
        [InlineData("form")]
        [InlineData("query")]
        [InlineData("bearer")]
        [InlineData("basic")]
        [InlineData("cookie")]
        public void AllSupportedCredentialSources_AreDiscoveredBeforeResponseEchoes(string source)
        {
            var canary = source == "basic" ? Convert.ToBase64String(Encoding.UTF8.GetBytes("audit:basic-canary")) :
                "SOURCE-" + source.ToUpperInvariant() + "-CANARY";
            var request = source == "form" ? "password=" + canary + "&keep=public-marker" : "keep=public-marker";
            var header = source switch
            {
                "bearer" => "Authorization: Bearer " + canary + "\r\n",
                "basic" => "Authorization: Basic " + canary + "\r\n",
                "cookie" => "Cookie: session=" + canary + ";theme=dark\r\n",
                _ => ""
            };
            var evidence = ExportRecords(PairRecord(1, request,
                "{\"message\":\"invalid " + canary + " supplied\",\"keep\":\"public-marker\"}",
                source == "form" ? "application/x-www-form-urlencoded" : "text/plain",
                source == "query" ? "/?access_token=" + canary + "&keep=public-marker" : "/", header));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Contains("public-marker", evidence.DecodedContent);
        }

        [Fact]
        public void BodyCredentials_AreKnownBeforeTheRequestUrlIsWritten()
        {
            const string canary = "BODY-URL-CANARY";
            var evidence = ExportRecords(PairRecord(1, "{\"password\":\"" + canary + "\"}",
                "{\"keep\":\"public-marker\"}", target: "/?note=" + canary + "&keep=public-marker"));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Contains("keep=public-marker", Encoding.ASCII.GetString(evidence.Entries["raw/1_c.txt"]));
        }

        [Theory]
        [InlineData("array")]
        [InlineData("object")]
        public void SensitiveCompositeLeaves_AreRememberedForNumericAndTextEchoes(string kind)
        {
            const string canary = "COMPOSITE-CREDENTIAL-CANARY";
            var value = kind == "array" ? "[\"" + canary + "\",2468]" :
                "{\"nested\":[\"" + canary + "\",2468]}";
            var evidence = ExportRecords(PairRecord(1, "{\"password\":" + value + "}",
                "{\"message\":\"invalid " + canary + "\",\"echo\":2468,\"port\":24680,\"keep\":\"public-marker\"}"));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("echo").GetString());
            Assert.Equal(24680, response.RootElement.GetProperty("port").GetInt32());
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
        }

        [Fact]
        public void ScalarNumericEchoes_AreRedactedWithoutChangingLongerNumbers()
        {
            var evidence = ExportRecords(PairRecord(1, "{\"pin\":2468,\"echo\":2468,\"port\":24680}",
                "{\"echo\":2468,\"port\":24680,\"negative\":-2468,\"fraction\":2468.5,\"keep\":\"public-marker\"}"));
            using var request = JsonDocument.Parse(Body(evidence, true));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(SazSanitizer.Redacted, request.RootElement.GetProperty("echo").GetString());
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("echo").GetString());
            Assert.Equal(24680, request.RootElement.GetProperty("port").GetInt32());
            Assert.Equal(24680, response.RootElement.GetProperty("port").GetInt32());
            Assert.Equal(-2468, response.RootElement.GetProperty("negative").GetInt32());
            Assert.Equal(2468.5m, response.RootElement.GetProperty("fraction").GetDecimal());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CredentialOverflow_FailsClosedForBothDirectionsIncludingHeadersAndUrls(bool overflowInResponse)
        {
            var request = JsonSerializer.Serialize(Enumerable.Range(0, overflowInResponse ? 32 : 65)
                .Select(index => new { secret = $"REQUEST-{index:D3}" }));
            var response = overflowInResponse
                ? JsonSerializer.Serialize(Enumerable.Range(0, 33).Select(index => new { secret = $"RESPONSE-{index:D3}" }))
                : "{\"keep\":\"public-marker\"}";
            var canary = overflowInResponse ? "RESPONSE-032" : "REQUEST-064";
            var evidence = ExportRecords(PairRecord(1, request, response,
                target: "/?note=" + canary,
                extraRequestHeaders: "X-Request-Echo: " + canary + "\r\n",
                extraResponseHeaders: "X-Response-Echo: " + canary + "\r\n"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.True(evidence.Summary.OmittedHeaders > 0);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.DoesNotContain("X-Request-Echo:", evidence.DecodedContent);
            Assert.DoesNotContain("X-Response-Echo:", evidence.DecodedContent);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
        }

        [Theory]
        [InlineData("Z\r\n4\r\npass\r\n11\r\nword=TRUNCATED-FRAME-CANARY")]
        [InlineData("Z\n4\npass\n11\nword=TRUNCATED-FRAME-CANARY\n0;x=y\n\n")]
        [InlineData("Z\r\n5\npass\n11\r\nword=TRUNCATED-FRAME-CANARY")]
        public void TruncatedAndLfOnlyChunkFraming_CannotBeCopiedAsPlainText(string body)
        {
            var evidence = Export("text/plain", body, requestBody: true,
                extraRequestHeaders: "Transfer-Encoding: chunked\r\n");

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("TRUNCATED-FRAME-CANARY", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
        }

        [Theory]
        [InlineData("query")]
        [InlineData("referer")]
        [InlineData("path")]
        [InlineData("flag")]
        public void ExcessiveUrlComponentEncoding_RedactsOnlyThatComponent(string component)
        {
            var encoded = "https://public.test/?access_token=DEPTH-COMPONENT-CANARY";
            for (var pass = 0; pass < 12; pass++)
            {
                encoded = Uri.EscapeDataString(encoded);
            }
            var target = component switch
            {
                "query" => "/?note=" + encoded + "&keep=public-marker",
                "path" => "/person/" + encoded + "?keep=public-marker",
                "flag" => "/?" + encoded + "&keep=public-marker",
                _ => "/"
            };
            var evidence = ExportRecords(PairRecord(1, "{\"keep\":\"public-marker\"}",
                "{\"keep\":\"public-marker\"}", target: target,
                extraRequestHeaders: component == "referer"
                    ? "Referer: https://example.test/?note=" + encoded + "&keep=public-marker\r\n" : ""));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("DEPTH-COMPONENT-CANARY", evidence.DecodedContent);
            Assert.Contains("keep=public-marker", evidence.DecodedContent);
            Assert.Contains(SazSanitizer.Redacted, evidence.DecodedContent);
        }

        [Theory]
        [InlineData("8")]
        [InlineData("example")]
        public void LearnedValues_DoNotAlterHostOrGeneratedContentType(string secret)
        {
            var evidence = ExportRecords(PairRecord(1,
                JsonSerializer.Serialize(new { pin = secret, echo = secret, keep = "public-marker" }),
                "{\"keep\":\"public-marker\"}", target: "https://example.test/"));
            var request = Encoding.ASCII.GetString(evidence.Entries["raw/1_c.txt"]);
            var response = Encoding.ASCII.GetString(evidence.Entries["raw/1_s.txt"]);

            Assert.Equal(0, evidence.Summary.OmittedHeaders);
            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: example.test\r\n", request);
            Assert.Contains("Content-Type: application/json; charset=utf-8\r\n", request);
            Assert.Contains("Content-Type: application/json; charset=utf-8\r\n", response);
            using var document = JsonDocument.Parse(Body(evidence, true));
            Assert.Equal(SazSanitizer.Redacted, document.RootElement.GetProperty("echo").GetString());
        }

        [Theory]
        [InlineData(" password ")]
        [InlineData("\tpassword\t")]
        public void WhitespaceNormalizedPasswordInputTypes_AreRecognized(string type)
        {
            var evidence = Export("application/xml",
                "<form><input type=\"" + type + "\" value=\"PADDED-INPUT-CANARY\"/>" +
                "<input type=\"text\" value=\"public-marker\"/></form>", requestBody: true);

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("PADDED-INPUT-CANARY", evidence.DecodedContent);
            Assert.Contains("public-marker", evidence.DecodedContent);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OversizedJwtHeaders_AreOmittedRatherThanCopiedUnchecked(bool inKey)
        {
            static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", padding = new string('a', 7000) }));
            var unsigned = header + "." + Encode(Encoding.UTF8.GetBytes("{\"sub\":\"JWT-LIMIT-CANARY\"}"));
            var token = unsigned + "." + Encode(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes("synthetic audit key; not accepted by a service"), Encoding.ASCII.GetBytes(unsigned)));
            Assert.True(header.Length > 8192);
            var body = inKey ? JsonSerializer.Serialize(new Dictionary<string, string> { [token] = "public-marker" }) : token;
            var evidence = Export(inKey ? "application/json" : "text/plain", body, requestBody: true);

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(token, evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
        }

        [Theory]
        [InlineData(false, "PATH-ECHO-CANARY", "PATH-ECHO-CANARY")]
        [InlineData(true, "PATH-ECHO-CANARY", "PATH-ECHO-CANARY")]
        [InlineData(false, "PATH+PLUS-CANARY", "PATH+PLUS-CANARY")]
        [InlineData(false, "PATH+PLUS-CANARY", "PATH%2BPLUS-CANARY")]
        [InlineData(false, "PATH+PLUS-CANARY", "PATH%252BPLUS-CANARY")]
        public void PathCredentials_AreLearnedWithoutTheirPrefixAndPreserveLiteralPluses(
            bool reverse, string canary, string pathValue)
        {
            var request = reverse ? JsonSerializer.Serialize(new { note = canary }) : "{\"keep\":\"public-marker\"}";
            var response = reverse ? "{\"keep\":\"public-marker\"}" :
                JsonSerializer.Serialize(new { message = "invalid " + canary + " supplied" });
            var evidence = ExportRecords(PairRecord(1, request, response,
                target: reverse ? "/" : "/api/token/" + pathValue,
                extraResponseHeaders: reverse ? "Location: https://example.test/session-id/" + pathValue + "\r\n" : ""));
            using var echo = JsonDocument.Parse(Body(evidence, reverse));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(reverse ? SazSanitizer.Redacted : "invalid " + SazSanitizer.Redacted + " supplied",
                echo.RootElement.GetProperty(reverse ? "note" : "message").GetString());
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        [Theory]
        [InlineData("<credentials><current>XML-OLD-CANARY</current><next>XML-NEW-CANARY</next></credentials>", "XML-NEW-CANARY")]
        [InlineData("<password>\n  XML-PAD-CANARY\n</password>", "XML-PAD-CANARY")]
        [InlineData("<authorization>Bearer XML-BEARER-CANARY</authorization>", "XML-BEARER-CANARY")]
        [InlineData("<authorization>Basic WE1MLUFTU0VSVC1DQU5BUlk=</authorization>", "WE1MLUFTU0VSVC1DQU5BUlk=")]
        [InlineData("<authorization>XBL3.0 x=12345;XML-XBL-CANARY</authorization>", "XML-XBL-CANARY")]
        [InlineData("<setting><name>password</name><value><string>XML-NESTED-CANARY</string></value></setting>", "XML-NESTED-CANARY")]
        [InlineData("<credentials><item value=\"XML-ATTRIBUTE-CANARY\"/></credentials>", "XML-ATTRIBUTE-CANARY")]
        [InlineData("<credentials xml:space=\"preserve\"> XML-DIRECT-CANARY <item>XML-OTHER-CANARY</item></credentials>", "XML-DIRECT-CANARY")]
        [InlineData("<password><![CDATA[XML-CDATA-CANARY]]></password>", "XML-CDATA-CANARY")]
        public void XmlSensitiveScalarsAndAttributes_AreLearnedForPairedEchoes(string xml, string canary)
        {
            var response = JsonSerializer.Serialize(new
            {
                message = "invalid " + canary + " supplied",
                keep = "public-marker",
                schema = "password",
                space = "preserve"
            });
            var evidence = ExportRecords(PairRecord(1, xml, response, "application/xml"));
            using var echo = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal("invalid " + SazSanitizer.Redacted + " supplied", echo.RootElement.GetProperty("message").GetString());
            Assert.Equal("public-marker", echo.RootElement.GetProperty("keep").GetString());
            Assert.Equal("password", echo.RootElement.GetProperty("schema").GetString());
            Assert.Equal("preserve", echo.RootElement.GetProperty("space").GetString());
        }

        [Fact]
        public void XmlSensitiveContainers_FailClosedWhenTheirCombinedCredentialExceedsTheLimit()
        {
            var first = new string('A', 3000);
            var second = new string('B', 3000);
            var evidence = ExportRecords(PairRecord(1,
                "<Signature><item>" + first + "</item><item>" + second + "</item></Signature>",
                JsonSerializer.Serialize(new { echo = second, keep = "public-marker" }), "application/xml"));
            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.DoesNotContain(first, evidence.DecodedContent);
            Assert.DoesNotContain(second, evidence.DecodedContent);
        }

        [Theory]
        [InlineData(30)]
        [InlineData(45)]
        [InlineData(64)]
        public void CookieHeaders_ApplyCapacityAndLengthBoundsToIndividualValues(int count)
        {
            var values = Enumerable.Range(0, count)
                .Select(index => "COOKIE-" + index.ToString("D2", CultureInfo.InvariantCulture) + "-" +
                    new string((char)('A' + index % 26), 80)).ToArray();
            var header = string.Join("; ", values.Select((value, index) => $"c{index:D2}={value}"));
            var evidence = ExportRecords(PairRecord(1, "{\"keep\":\"public-marker\"}",
                JsonSerializer.Serialize(new { echo = values[^1], keep = "public-marker" }),
                extraRequestHeaders: "Cookie: " + header + "\r\n"));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("echo").GetString());
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
            Assert.All(values, value => Assert.DoesNotContain(value, evidence.DecodedContent));
        }

        [Fact]
        public void SetCookieAttributes_DoNotCountTowardTheScalarValueLimit()
        {
            var value = new string('X', 4000);
            var header = "session=" + value +
                "; Path=/; Domain=example.test; Expires=Wed, 21 Oct 2037 07:28:00 GMT; Secure; HttpOnly; SameSite=None";
            Assert.True(header.Length > 4096);
            var evidence = ExportRecords(PairRecord(1,
                JsonSerializer.Serialize(new { echo = value, keep = "public-marker" }),
                "{\"domain\":\"example.test\",\"path\":\"/\",\"attribute\":\"HttpOnly\",\"keep\":\"public-marker\"}",
                extraResponseHeaders: "Set-Cookie: " + header + "\r\n"));
            using var request = JsonDocument.Parse(Body(evidence, true));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(SazSanitizer.Redacted, request.RootElement.GetProperty("echo").GetString());
            Assert.Equal("example.test", response.RootElement.GetProperty("domain").GetString());
            Assert.Equal("/", response.RootElement.GetProperty("path").GetString());
            Assert.Equal("HttpOnly", response.RootElement.GetProperty("attribute").GetString());
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
        }

        [Fact]
        public void OpaqueCookieValues_AreStillLearnedWhenNoNameValuePairIsPresent()
        {
            var evidence = ExportRecords(PairRecord(1, "{\"keep\":\"public-marker\"}",
                "{\"echo\":\"OPAQUE-COOKIE-CANARY\",\"keep\":\"public-marker\"}",
                extraRequestHeaders: "Cookie: OPAQUE-COOKIE-CANARY\r\n"));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("echo").GetString());
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
        }

        [Theory]
        [InlineData("header")]
        [InlineData("query")]
        [InlineData("location")]
        [InlineData("referer")]
        public void OversizedTokenLikeFields_FailClosedForTheirSessionWithoutAbortingTheArchive(string field)
        {
            var candidate = new string('a', 9000) + ".b.c";
            var requestHeader = field == "header" ? "X-Trace: " + candidate + "\r\n" :
                field == "referer" ? "Referer: https://example.test/?note=" + candidate + "\r\n" : "";
            var evidence = ExportRecords(
                PairRecord(1, "{\"keep\":\"first-marker\"}", "{\"keep\":\"first-marker\"}",
                    target: field == "query" ? "/?note=" + candidate : "/",
                    extraRequestHeaders: requestHeader,
                    extraResponseHeaders: field == "location" ? "Location: https://example.test/?note=" + candidate + "\r\n" : ""),
                PairRecord(2, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(candidate, evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            using var next = JsonDocument.Parse(Body(evidence, true, 2));
            Assert.Equal("public-marker", next.RootElement.GetProperty("keep").GetString());
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LearnedIdentifierKeys_AreRemovedInEitherDirection(bool inResponse)
        {
            const string xuid = "2535400000000001";
            var request = inResponse ? "{\"xuids\":[\"" + xuid + "\"]}" :
                "{\"xuid\":\"" + xuid + "\",\"scores\":{\"" + xuid + "\":10}}";
            var response = inResponse ? "{\"presence\":{\"" + xuid + "\":\"online\"}}" : "{\"keep\":\"public-marker\"}";
            var evidence = ExportRecords(PairRecord(1, request, response));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(xuid, evidence.DecodedContent);
            using var document = JsonDocument.Parse(Body(evidence, !inResponse));
            Assert.False(document.RootElement.GetProperty(inResponse ? "presence" : "scores").TryGetProperty(xuid, out _));
        }

        [Fact]
        public void RenamedKeys_UseDistinctPlaceholdersWithoutOverwritingExistingNames()
        {
            var request = JsonSerializer.Serialize(new
            {
                secrets = new[] { "DICT-ALPHA-2478", "DICT-BETA-2479" },
                values = new Dictionary<string, int>
                {
                    ["DICT-ALPHA-2478"] = 10,
                    ["DICT-BETA-2479"] = 20,
                    ["[REDACTED-KEY-1]"] = 30
                }
            });
            var evidence = ExportRecords(PairRecord(1, request, "{\"keep\":\"public-marker\"}"));
            using var document = JsonDocument.Parse(Body(evidence, true));
            var values = document.RootElement.GetProperty("values").EnumerateObject().ToArray();

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(3, values.Length);
            Assert.Equal(3, values.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(new[] { 10, 20, 30 }, values.Select(value => value.Value.GetInt32()).Order());
            Assert.Equal(30, document.RootElement.GetProperty("values").GetProperty("[REDACTED-KEY-1]").GetInt32());
            Assert.DoesNotContain("DICT-ALPHA-2478", evidence.DecodedContent);
            Assert.DoesNotContain("DICT-BETA-2479", evidence.DecodedContent);
        }

        [Theory]
        [InlineData("metadata")]
        [InlineData("properties")]
        [InlineData("password")]
        [InlineData("username")]
        public void RecognizedLongSchemaLabels_ArePreservedEvenWhenTheyMatchALearnedValue(string name)
        {
            var evidence = ExportRecords(PairRecord(1,
                JsonSerializer.Serialize(new Dictionary<string, string> { ["secret"] = name, [name] = "schema-marker" }),
                "{\"keep\":\"public-marker\"}"));
            using var document = JsonDocument.Parse(Body(evidence, true));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.True(document.RootElement.TryGetProperty(name, out _));
        }

        [Theory]
        [InlineData("host")]
        [InlineData("userid-host")]
        [InlineData("plus")]
        [InlineData("slash")]
        public void LearnedValuesInsideUrls_AreMaskedUsingLosslessRepresentations(string kind)
        {
            var canary = kind switch
            {
                "plus" => "C146+PLUSCANARY",
                "slash" => "C146/SLASHCANARY",
                "userid-host" => "c146hostcanary",
                _ => "r4hostcanary"
            };
            var url = kind switch
            {
                "plus" => "https://example.test/cb?ref=" + canary + "&keep=public-marker",
                "slash" => "https://example.test/" + canary + "?keep=public-marker",
                _ => "https://" + canary + ".example.test/x?keep=public-marker"
            };
            var evidence = ExportRecords(PairRecord(1,
                JsonSerializer.Serialize(new Dictionary<string, string> { [kind == "userid-host" ? "userid" : "password"] = canary }),
                JsonSerializer.Serialize(new { next = url })));
            using var response = JsonDocument.Parse(Body(evidence));
            var next = response.RootElement.GetProperty("next").GetString();

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.True(Uri.TryCreate(next, UriKind.Absolute, out _));
            Assert.Contains("keep=public-marker", Uri.UnescapeDataString(next));
            Assert.Contains("Host: example.test\r\n", Encoding.ASCII.GetString(evidence.Entries["raw/1_c.txt"]));
        }

        [Theory]
        [InlineData("plus")]
        [InlineData("slash")]
        public void RequestTargets_UseLiteralAndCrossSegmentLearnedValueMatching(string kind)
        {
            var canary = kind == "plus" ? "C146+LINECANARY" : "R4SL/ASHCANARY";
            var target = kind == "plus" ? "/login?hint=" + canary + "&keep=public-marker" :
                "/files/" + canary + "/view?keep=public-marker";
            var evidence = ExportRecords(PairRecord(1, JsonSerializer.Serialize(new { password = canary }),
                "{\"keep\":\"public-marker\"}", target: target));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Contains("keep=public-marker", evidence.DecodedContent);
        }

        [Theory]
        [InlineData("query")]
        [InlineData("form")]
        public void CredentialValuesWithLiteralPluses_AreLearnedBeforeFormDecoding(string source)
        {
            const string canary = "LITERAL+FORM-CANARY";
            var evidence = ExportRecords(PairRecord(1,
                source == "form" ? "password=" + canary + "&keep=public-marker" : "keep=public-marker",
                JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" }),
                source == "form" ? "application/x-www-form-urlencoded" : "text/plain",
                source == "query" ? "/?access_token=" + canary : "/"));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("echo").GetString());
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
        }

        [Fact]
        public void CombinedXmlCredentialText_IsLearnedAlongsideIndividualLeaves()
        {
            var evidence = ExportRecords(PairRecord(1,
                "<root><password><a>R4LEFT</a><b>R4RIGHT</b></password></root>",
                "{\"combined\":\"R4LEFTR4RIGHT\",\"leaf\":\"R4RIGHT\",\"keep\":\"public-marker\"}",
                "application/xml"));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("R4LEFTR4RIGHT", evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("combined").GetString());
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("leaf").GetString());
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
        }

        [Theory]
        [InlineData("basic")]
        [InlineData("scheme")]
        [InlineData("userinfo")]
        [InlineData("undecodable-query")]
        public void DecodedAndRawCredentialComponents_AreLearnedWithoutDroppingBenignData(string source)
        {
            var canary = source switch
            {
                "basic" => "R4BASIC:PASSWORD-CANARY",
                "scheme" => "C146SCHEMECANARY",
                "userinfo" => "R4USERINFOCANARY",
                _ => "%FFR4-ENC-CANARY"
            };
            var header = source == "basic"
                ? "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:" + canary)) + "\r\n"
                : source == "scheme" ? "Authorization: Token " + canary + "\r\n" : "";
            var target = source == "userinfo" ? "http://alice:" + canary + "@example.test/login" :
                source == "undecodable-query" ? "/api?access_token=" + canary : "/";
            var evidence = ExportRecords(PairRecord(1, "{\"keep\":\"public-marker\"}",
                JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" }),
                target: target, extraRequestHeaders: header));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("echo").GetString());
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
        }

        // A value that is a secret only because of the name it is sent under, such as x-session-token, is learned from a
        // response trailer as from a response header, although the archive never writes the trailer: its echo in either
        // body is redacted, and an unrelated value is kept. The canary matches no generic detector.
        [Theory]
        [InlineData("header", true)]
        [InlineData("header", false)]
        [InlineData("trailer", true)]
        [InlineData("trailer", false)]
        public void SessionTokenFromAResponseHeaderOrTrailer_IsRedactedWhereEitherBodyEchoesIt(
            string source, bool requestEchoes)
        {
            const string canary = "OPAQUE-SESSION-CANARY";
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var record = PairRecord(1, requestEchoes ? echo : "{\"keep\":\"public-marker\"}",
                requestEchoes ? "{\"keep\":\"public-marker\"}" : echo,
                extraResponseHeaders: source == "header" ? "x-session-token: " + canary + "\r\n" : "");
            if (source == "trailer")
            {
                record.SetResponseCaptureDetails("x-session-token: " + canary + "\r\n", 0);
            }

            var evidence = ExportRecords(record);
            using var echoed = JsonDocument.Parse(Body(evidence, requestEchoes));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, echoed.RootElement.GetProperty("echo").GetString());
            Assert.Equal("public-marker", echoed.RootElement.GetProperty("keep").GetString());
        }

        // A trailer whose name is not sensitive still goes through the generic detectors, as a header would: the decoded
        // password of a Basic credential, or the password in a URL's user information, is learned, so an echo of the
        // password alone is redacted.
        [Theory]
        [InlineData("basic", "OPAQUE-BASIC-CANARY")]
        [InlineData("userinfo", "OPAQUE-USERINFO-CANARY")]
        public void CredentialsInAnOrdinaryTrailer_AreLearnedFromTheirParts(string kind, string canary)
        {
            var value = kind == "basic"
                ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("svc-user:" + canary))
                : "https://svc-user:" + canary + "@callback.example.test/done";
            var record = PairRecord(1, JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" }),
                "{\"keep\":\"public-marker\"}");
            record.SetResponseCaptureDetails("x-diagnostics: " + value + "\r\n", 0);

            var evidence = ExportRecords(record);
            using var request = JsonDocument.Parse(Body(evidence, true));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, request.RootElement.GetProperty("echo").GetString());
            Assert.Equal("public-marker", request.RootElement.GetProperty("keep").GetString());
        }

        // An ordinary archive keeps the trailers and an import restores them; a sanitized export of the import still
        // learns from them before leaving them out, whichever body echoes the value.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ImportedTrailers_AreInspectedBeforeASanitizedExportLeavesThemOut(bool requestEchoes)
        {
            const string canary = "OPAQUE-IMPORTED-CANARY";
            const string trailers = "x-checksum: abc123\r\nx-session-token: " + canary + "\r\n";
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var original = Path.Join(_directory, "trailers.saz");
            var source = PairRecord(1, requestEchoes ? echo : "{\"keep\":\"public-marker\"}",
                requestEchoes ? "{\"keep\":\"public-marker\"}" : echo);
            source.SetResponseCaptureDetails(trailers, 0);
            SazArchive.Write(original, new[] { source });
            var imported = Assert.Single(FiddlerSazHandler.ImportSazFile(original, CancellationToken.None));
            Assert.Equal(trailers, imported.ResponseTrailers);

            var evidence = ExportRecords(imported);
            using var echoed = JsonDocument.Parse(Body(evidence, requestEchoes));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.DoesNotContain("x-session-token", evidence.DecodedContent);
            Assert.DoesNotContain("x-checksum", evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, echoed.RootElement.GetProperty("echo").GetString());
            Assert.Equal("public-marker", echoed.RootElement.GetProperty("keep").GetString());
        }

        // A trailer value too long to learn, or one secret more than the pair can hold across its headers, bodies and
        // trailers, leaves the whole pair out like any other capacity failure; the next session is unaffected.
        [Theory]
        [InlineData("oversized")]
        [InlineData("capacity")]
        public void TrailerSecretsBeyondTheCapacity_MakeThePairOpaqueButNotTheNextSession(string kind)
        {
            var oversized = kind == "oversized";
            var headers = oversized ? "" : string.Concat(Enumerable.Range(0, 24)
                .Select(index => $"x-api-token-{index:D2}: CAP-HEADER-{index:D2}\r\n"));
            var response = oversized ? "{\"keep\":\"public-marker\"}" : JsonSerializer.Serialize(
                Enumerable.Range(0, 24).Select(index => new { secret = $"CAP-BODY-{index:D2}" }));
            var trailers = oversized ? "x-session-token: CAP-" + new string('S', 5000) + "\r\n" : string.Concat(
                Enumerable.Range(0, 24).Select(index => $"x-session-token-{index:D2}: CAP-TRAILER-{index:D2}\r\n"));
            var first = PairRecord(1, "{\"keep\":\"public-marker\"}", response, extraRequestHeaders: headers);
            first.SetResponseCaptureDetails(trailers, 0);

            var evidence = ExportRecords(first,
                PairRecord(2, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Equal(0, evidence.Summary.WorkBudgetOmissions);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain("CAP-", evidence.DecodedContent);
            using var next = JsonDocument.Parse(Body(evidence, true, 2));
            Assert.Equal("public-marker", next.RootElement.GetProperty("keep").GetString());
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        // Trailers spend what the response's inspection left of its work budget, and the response is then rendered with
        // what they leave. Trailer header fields cost the response one unit a character in each pass and are never
        // written, so this response fits its budget alone, as do the trailers (the next session has them too), but not
        // both.
        [Fact]
        public void ResponseTrailers_SpendTheResponseWorkBudget()
        {
            var budget = (long)typeof(SazSanitizer)
                .GetField("MaximumInspectionWork", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            var field = "Trailer: " + new string('a', 32 * 1024 - 11) + "\r\n";
            var trailers = string.Concat(Enumerable.Repeat("x-padding:\r\n", 3 * 1024 * 1024 / 12));
            var first = PairRecord(1, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}",
                extraResponseHeaders: string.Concat(
                    Enumerable.Repeat(field, (int)((budget / 2 - 1024 * 1024) / field.Length))));
            first.SetResponseCaptureDetails(trailers, 0);
            var next = PairRecord(2, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}");
            next.SetResponseCaptureDetails(trailers, 0);

            var evidence = ExportRecords(first, next);

            Assert.Equal(1, evidence.Summary.WorkBudgetOmissions);
            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, true, 2)));
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        // A trailer line that is not a header field could hide a secret that cannot be learned, so it leaves the whole
        // pair out, even beside valid fields; a line of other whitespace, such as a vertical tab, is not a field either.
        // Empty lines only separate the fields.
        [Theory]
        [InlineData("x-session-token TRAILER-LINE-CANARY\r\n", true)]
        [InlineData("x-checksum: abc123\r\nx-session-token TRAILER-LINE-CANARY\r\n", true)]
        [InlineData("x-checksum: abc123\r\n x-session-token: TRAILER-LINE-CANARY\r\n", true)]
        [InlineData("x session token: TRAILER-LINE-CANARY\r\n", true)]
        [InlineData("x-session-token: TRAILER-LINE-CANARY\r\n\v\r\n", true)]
        [InlineData("\r\nx-session-token: TRAILER-LINE-CANARY\r\n\r\n", false)]
        public void TrailerLinesThatAreNotFields_MakeThePairOpaque(string trailers, bool opaque)
        {
            const string canary = "TRAILER-LINE-CANARY";
            var record = PairRecord(1, JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" }),
                "{\"keep\":\"public-marker\"}");
            record.SetResponseCaptureDetails(trailers, 0);

            var evidence = ExportRecords(record);

            Assert.DoesNotContain(canary, evidence.DecodedContent);
            if (opaque)
            {
                Assert.Equal(2, evidence.Summary.OmittedBodies);
                Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
                Assert.Empty(Body(evidence, true));
                Assert.Empty(Body(evidence));
            }
            else
            {
                using var request = JsonDocument.Parse(Body(evidence, true));
                Assert.Equal(0, evidence.Summary.OmittedBodies);
                Assert.Equal(SazSanitizer.Redacted, request.RootElement.GetProperty("echo").GetString());
                Assert.Equal("public-marker", request.RootElement.GetProperty("keep").GetString());
            }
        }

        // A captured or imported trailer value with a control character, or with a character above U+00FF that no field
        // byte decodes to, can't be learned as a receiver would read it: learned whole, it would miss an echo of the value
        // before that character, and trimmed, it would be a value no receiver reads. So the whole pair is left out.
        [Theory]
        [InlineData("del", false)]
        [InlineData("control", false)]
        [InlineData("beyond-latin1", false)]
        [InlineData("leading-vt", false)]
        [InlineData("trailing-vt", false)]
        [InlineData("leading-ff", false)]
        [InlineData("trailing-ideographic-space", false)]
        [InlineData("del", true)]
        [InlineData("beyond-latin1", true)]
        public void TrailerValuesWithControlOrNonLatin1Characters_MakeThePairOpaque(string kind, bool imported)
        {
            const string canary = "q7Opaque";
            var trailers = "x-session-token: " + UnusualValue(canary, kind) + "\r\n";
            var record = PairRecord(1, JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" }),
                "{\"keep\":\"public-marker\"}");
            record.SetResponseCaptureDetails(trailers, 0);
            if (imported)
            {
                var original = Path.Join(_directory, "unusual-trailers.saz");
                SazArchive.Write(original, new[] { record });
                record = Assert.Single(FiddlerSazHandler.ImportSazFile(original, CancellationToken.None));
                Assert.Equal(trailers, record.ResponseTrailers);
            }

            var evidence = ExportRecords(record);

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        // Stored header values follow the same field-value rule: a sensitive request or response header whose value has a
        // control character or a character above U+00FF leaves the whole pair out instead of being learned whole or
        // trimmed.
        [Theory]
        [InlineData(true, "del")]
        [InlineData(false, "del")]
        [InlineData(true, "trailing-vt")]
        [InlineData(false, "leading-vt")]
        [InlineData(true, "trailing-ff")]
        [InlineData(false, "beyond-latin1")]
        [InlineData(true, "unpaired-surrogate")]
        public void HeaderValuesWithControlOrNonLatin1Characters_MakeThePairOpaque(bool inRequest, string kind)
        {
            const string canary = "q7Opaque";
            var header = "x-session-token: " + UnusualValue(canary, kind) + "\r\n";
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var evidence = ExportRecords(PairRecord(1, inRequest ? "{\"keep\":\"public-marker\"}" : echo,
                inRequest ? echo : "{\"keep\":\"public-marker\"}",
                extraRequestHeaders: inRequest ? header : "", extraResponseHeaders: inRequest ? "" : header));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        // Tabs and obs-text (U+0080-U+00FF) are field-value characters, so such a value isn't opaque: it is learned as
        // stored apart from surrounding spaces and tabs, and its echo is redacted whole. A trailing NEL or no-break space
        // is part of the value, not whitespace to trim, though the value is also learned without it (see below).
        [Theory]
        [InlineData("trailer", "inner-tab")]
        [InlineData("trailer", "edge-tabs")]
        [InlineData("trailer", "obs-text")]
        [InlineData("trailer", "trailing-nel")]
        [InlineData("trailer", "trailing-nbsp")]
        [InlineData("header", "edge-tabs")]
        [InlineData("header", "obs-text")]
        [InlineData("header", "trailing-nel")]
        public void FieldValuesWithTabsAndObsText_AreLearnedAsStoredAndRedacted(string source, string kind)
        {
            var (stored, learned) = kind switch
            {
                "inner-tab" => ("q7Op\taque", "q7Op\taque"),
                "edge-tabs" => ("\tq7Opaque\t", "q7Opaque"),
                "obs-text" => ("q7Opaqu\u00E9", "q7Opaqu\u00E9"),
                "trailing-nel" => ("q7Opaque\u0085", "q7Opaque\u0085"),
                _ => ("q7Opaque\u00A0", "q7Opaque\u00A0"),
            };
            var field = "x-session-token: " + stored + "\r\n";
            var record = PairRecord(1, JsonSerializer.Serialize(new { echo = learned, keep = "public-marker" }),
                "{\"keep\":\"public-marker\"}", extraResponseHeaders: source == "header" ? field : "");
            if (source == "trailer")
            {
                record.SetResponseCaptureDetails(field, 0);
            }

            var evidence = ExportRecords(record);
            using var request = JsonDocument.Parse(Body(evidence, true));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Equal(SazSanitizer.Redacted, request.RootElement.GetProperty("echo").GetString());
            Assert.Equal("public-marker", request.RootElement.GetProperty("keep").GetString());
        }

        // A learned secret that starts or ends with a NEL or a no-break space is also learned as String.Trim() leaves it,
        // since a receiver that trims it uses that form: an echo of either form is redacted.
        [Theory]
        [InlineData("trailer", "trailing-nel")]
        [InlineData("trailer", "leading-nbsp")]
        [InlineData("header", "trailing-nbsp")]
        [InlineData("header", "leading-nel")]
        public void EdgeNelAndNbspSecrets_AreRedactedWithAndWithoutTheEdgeCharacter(string source, string kind)
        {
            const string canary = "q7Opaque";
            var exact = kind switch
            {
                "trailing-nel" => canary + "\u0085",
                "leading-nbsp" => "\u00A0" + canary,
                "trailing-nbsp" => canary + "\u00A0",
                _ => "\u0085" + canary,
            };
            var field = "x-session-token: " + exact + "\r\n";
            var record = PairRecord(1, JsonSerializer.Serialize(new { exact, trimmed = canary, keep = "public-marker" }),
                "{\"keep\":\"public-marker\"}", extraResponseHeaders: source == "header" ? field : "");
            if (source == "trailer")
            {
                record.SetResponseCaptureDetails(field, 0);
            }

            var evidence = ExportRecords(record);
            using var request = JsonDocument.Parse(Body(evidence, true));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, request.RootElement.GetProperty("exact").GetString());
            Assert.Equal(SazSanitizer.Redacted, request.RootElement.GetProperty("trimmed").GetString());
            Assert.Equal("public-marker", request.RootElement.GetProperty("keep").GetString());
        }

        // The trimmed forms count toward the pair's capacity like any learned value: forty secrets with a no-break space
        // fit, but not with their trimmed forms, so the pair is left out. The next session is unaffected.
        [Theory]
        [InlineData("header")]
        [InlineData("trailer")]
        public void TrimmedSecretForms_CountTowardThePairCapacity(string source)
        {
            var fields = string.Concat(Enumerable.Range(0, 40)
                .Select(index => $"x-api-token-{index:D2}: ALIAS-{index:D2}\u00A0\r\n"));
            var first = PairRecord(1, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}",
                extraRequestHeaders: source == "header" ? fields : "");
            if (source == "trailer")
            {
                first.SetResponseCaptureDetails(fields, 0);
            }

            var evidence = ExportRecords(first,
                PairRecord(2, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Equal(0, evidence.Summary.WorkBudgetOmissions);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain("ALIAS-", evidence.DecodedContent);
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, true, 2)));
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        // Trimming never rescues a value: a secret too long to learn stays too long even when its trimmed form would fit,
        // and an invalid character still leaves the pair out beside edge whitespace.
        [Theory]
        [InlineData("oversized")]
        [InlineData("invalid")]
        public void TrimmedSecretForms_NeverRescueAnOversizedOrInvalidValue(string kind)
        {
            var value = kind == "oversized"
                ? new string('S', 4090) + new string('\u00A0', 10)
                : "q7Opaque\u000B\u00A0";
            var evidence = ExportRecords(PairRecord(1,
                JsonSerializer.Serialize(new { echo = "q7Opaque", keep = "public-marker" }), "{\"keep\":\"public-marker\"}",
                extraResponseHeaders: "x-session-token: " + value + "\r\n"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
        }

        // A stored header line that is not a field, with a name that is not a token or no colon, could hide a secret that
        // is never learned, so it leaves the whole pair out in either direction instead of being dropped unread.
        [Theory]
        [InlineData("x session-token: HEADER-LINE-CANARY", true)]
        [InlineData("x session-token: HEADER-LINE-CANARY", false)]
        [InlineData("x-session-token HEADER-LINE-CANARY", true)]
        [InlineData("x-session-token HEADER-LINE-CANARY", false)]
        public void MalformedStoredHeaderLines_MakeThePairOpaque(string line, bool inRequest)
        {
            const string canary = "HEADER-LINE-CANARY";
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var evidence = ExportRecords(PairRecord(1, inRequest ? "{\"keep\":\"public-marker\"}" : echo,
                inRequest ? echo : "{\"keep\":\"public-marker\"}",
                extraRequestHeaders: inRequest ? line + "\r\n" : "", extraResponseHeaders: inRequest ? "" : line + "\r\n"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        // A trailer field inside a captured chunked body is learned before the framing is removed, so its secret is
        // redacted where the other message echoes it.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ChunkedBodyTrailers_AreLearnedBeforeTheFramingIsRemoved(bool requestChunked)
        {
            const string canary = "RAW-TRAILER-CANARY";
            var chunked = Chunk("{\"keep\":\"public-marker\"}")
                .Replace("0\r\n\r\n", "0\r\nx-session-token: " + canary + "\r\n\r\n", StringComparison.Ordinal);
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var evidence = ExportRecords(PairRecord(1, requestChunked ? chunked : echo, requestChunked ? echo : chunked,
                extraRequestHeaders: requestChunked ? "Transfer-Encoding: chunked\r\n" : "",
                extraResponseHeaders: requestChunked ? "" : "Transfer-Encoding: chunked\r\n"));
            using var echoed = JsonDocument.Parse(Body(evidence, !requestChunked));
            using var dechunked = JsonDocument.Parse(Body(evidence, requestChunked));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, echoed.RootElement.GetProperty("echo").GetString());
            Assert.Equal("public-marker", echoed.RootElement.GetProperty("keep").GetString());
            Assert.Equal("public-marker", dechunked.RootElement.GetProperty("keep").GetString());
        }

        [Fact]
        public void MalformedChunkedJsonWithTrailer_MakesThePairOpaque()
        {
            const string canary = "MALFORMED-JSON-TRAILER-CANARY";
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var malformed = "Z\r\n0\r\nx-session-token: " + canary + "\r\n\r\n";
            var evidence = ExportRecords(PairRecord(1, echo, malformed,
                extraResponseHeaders: "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        // A chunked body's trailer section that can't be read to its end could hide a field that is never learned: a line
        // too long to read, data after the section's final CRLF, or a section without its terminator leaves the whole
        // pair out, not only that body. The next session is unaffected.
        [Theory]
        [InlineData("long-line")]
        [InlineData("trailing-data")]
        [InlineData("no-final-crlf")]
        [InlineData("unterminated-line")]
        public void UnreadableChunkedTrailerSections_MakeThePairOpaque(string kind)
        {
            const string canary = "HIDDEN-CANARY";
            var section = kind switch
            {
                "long-line" => "0\r\nx-padding: " + new string('a', 9000) + "\r\nx-session-token: " + canary + "\r\n\r\n",
                "trailing-data" => "0\r\n\r\nx-session-token: " + canary + "\r\n",
                "no-final-crlf" => "0\r\nx-session-token: " + canary + "\r\n",
                _ => "0\r\nx-session-token: " + canary,
            };
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var evidence = ExportRecords(
                PairRecord(1, echo,
                    Chunk("{\"keep\":\"public-marker\"}").Replace("0\r\n\r\n", section, StringComparison.Ordinal),
                    extraResponseHeaders: "Transfer-Encoding: chunked\r\n"),
                PairRecord(2, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, true, 2)));
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        // A chunked body over the inspection limit can't be read through to its trailer section, so the whole pair is left
        // out rather than letting a trailer secret reach the archive through the other message's echo, in either
        // direction. The next session is unaffected.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ChunkedBodyOverTheInspectionLimit_DoesNotLeakItsTrailersThroughAnEcho(bool requestChunked)
        {
            const string canary = "LARGE-CANARY";
            var chunked = SazSanitizer.MaximumBodyBytes.ToString("X", CultureInfo.InvariantCulture) + "\r\n\"" +
                new string('a', SazSanitizer.MaximumBodyBytes - 2) + "\"\r\n0\r\nx-session-token: " + canary + "\r\n\r\n";
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var evidence = ExportRecords(
                PairRecord(1, requestChunked ? chunked : echo, requestChunked ? echo : chunked,
                    extraRequestHeaders: requestChunked ? "Transfer-Encoding: chunked\r\n" : "",
                    extraResponseHeaders: requestChunked ? "" : "Transfer-Encoding: chunked\r\n"),
                PairRecord(2, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, true, 2)));
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        // A decoded body over the inspection limit, as XMAT stores responses, is only omitted itself: the response's
        // separately stored trailers are still inspected, so the paired echo is redacted and the rest of the request kept.
        [Fact]
        public void DecodedBodyOverTheInspectionLimit_IsOmittedAloneWhileItsStoredTrailersAreLearned()
        {
            const string canary = "DECODED-CANARY";
            var record = PairRecord(1, JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" }),
                "\"" + new string('a', SazSanitizer.MaximumBodyBytes) + "\"");
            record.SetResponseCaptureDetails("x-session-token: " + canary + "\r\n", 0);

            var evidence = ExportRecords(record);
            using var request = JsonDocument.Parse(Body(evidence, true));

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain("Host: redacted.invalid", evidence.DecodedContent);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, request.RootElement.GetProperty("echo").GetString());
            Assert.Equal("public-marker", request.RootElement.GetProperty("keep").GetString());
        }

        // An archive's trailer flag that is not valid header lines is imported as it is, so sanitizing the import still
        // sees that it may hold a secret and leaves the whole pair out instead of never reading it.
        [Fact]
        public void ImportedMalformedTrailers_AreKeptSoSanitizingFailsClosed()
        {
            const string canary = "MALFORMED-IMPORT-CANARY";
            const string trailers = "x-checksum: abc123\r\nx-session-token " + canary + "\r\n";
            var original = Path.Join(_directory, "malformed-trailers.saz");
            var source = PairRecord(1, JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" }),
                "{\"keep\":\"public-marker\"}");
            source.SetResponseCaptureDetails(trailers, 0);
            SazArchive.Write(original, new[] { source });
            var imported = Assert.Single(FiddlerSazHandler.ImportSazFile(original, CancellationToken.None));
            Assert.Equal(trailers, imported.ResponseTrailers);

            var evidence = ExportRecords(imported);

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        // A malformed header line in a plain Fiddler archive could hold a secret that the other message echoes. The import
        // leaves the line out of the message's text, which keeps only valid fields, and makes the session's header history
        // uninspectable instead, so a sanitized export leaves the whole pair out. That history survives an ordinary SAZ
        // copy (its x-xmat-headers-uninspectable flag) and a saved and reloaded capture (its HeadersUninspectable column).
        [Theory]
        [InlineData(true, "import")]
        [InlineData(false, "import")]
        [InlineData(true, "saz")]
        [InlineData(false, "saz")]
        [InlineData(true, "saved")]
        [InlineData(false, "saved")]
        public void ImportedMalformedHeaderLines_KeepTheirSessionOpaqueThroughSazAndSavedCopies(bool inRequest, string route)
        {
            const string canary = "IMPORTED-LINE-CANARY";
            const string requestHead = "POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n";
            const string responseHead = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n";
            var line = "x session-token: " + canary + "\r\n";
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var original = Path.Join(_directory, "malformed-headers.saz");
            SazArchive.WriteRaw(original,
                Encoding.ASCII.GetBytes(requestHead + (inRequest ? line : "") + "\r\n" +
                    (inRequest ? "{\"keep\":\"public-marker\"}" : echo)),
                Encoding.ASCII.GetBytes(responseHead + (inRequest ? "" : line) + "\r\n" +
                    (inRequest ? echo : "{\"keep\":\"public-marker\"}")));
            var record = Assert.Single(FiddlerSazHandler.ImportSazFile(original, CancellationToken.None));
            if (route == "saz")
            {
                var copy = Path.Join(_directory, "malformed-headers-copy.saz");
                SazArchive.Write(copy, new[] { record });
                Assert.Contains(new KeyValuePair<string, string>("x-xmat-headers-uninspectable", "1"),
                    ProcessCaptures.SessionFlags(ProcessCaptures.Entries(copy)["raw/1_m.xml"]));
                record = Assert.Single(FiddlerSazHandler.ImportSazFile(copy, CancellationToken.None));
            }
            else if (route == "saved")
            {
                var (saved, mark) = LoadCapture(SaveCapture(record, WebProxyCaptureSchema.CreateFields()));
                Assert.Equal(1L, mark);
                record = saved;
            }
            Assert.Equal((requestHead + "\r\n", responseHead + "\r\n"),
                (record.RequestLineAndHeaders, record.ResponseLineAndHeaders));

            var evidence = ExportRecords(record);

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        // The import reads a session's header history from what it left out and from XMAT's x-xmat-headers-uninspectable
        // flag in any case: an archive without the flag, as from Fiddler, has an unknown history; "-1", "0" and "1" are
        // unknown, no known loss and uninspectable; any other value, or values that differ, are uninspectable; and header
        // lines that the import left out make the session uninspectable whatever the flag says. A sanitized export leaves
        // an uninspectable pair out, sanitizes the others as stored, counts the unknown ones and carries each history.
        [Theory]
        [InlineData(null, false, "unknown")]
        [InlineData("-1", false, "unknown")]
        [InlineData("0", false, "none")]
        [InlineData("1", false, "uninspectable")]
        [InlineData("X-XMAT-HEADERS-UNINSPECTABLE=-1", false, "unknown")]
        [InlineData("X-XMAT-HEADERS-UNINSPECTABLE=0", false, "none")]
        [InlineData("0|0", false, "none")]
        [InlineData("yes", false, "uninspectable")]
        [InlineData("", false, "uninspectable")]
        [InlineData("2", false, "uninspectable")]
        [InlineData(" 0", false, "uninspectable")]
        [InlineData("0|1", false, "uninspectable")]
        [InlineData("-1|0", false, "uninspectable")]
        [InlineData(null, true, "uninspectable")]
        [InlineData("-1", true, "uninspectable")]
        [InlineData("0", true, "uninspectable")]
        public void Import_ReadsTheHeaderHistoryFromWhatItLeftOutAndItsFlag(string flags, bool malformedLine, string history)
        {
            const string canary = "FLAGGED-LINE-CANARY";
            var metadata = flags == null
                ? Array.Empty<KeyValuePair<string, string>>()
                : flags.Split('|').Select(flag => flag.Split('=', 2) is [var name, var value]
                    ? new KeyValuePair<string, string>(name, value)
                    : new KeyValuePair<string, string>("x-xmat-headers-uninspectable", flag)).ToArray();
            var path = Path.Join(_directory, "flagged.saz");
            SazArchive.WriteRaw(path,
                Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n" +
                    (malformedLine ? "x session-token: " + canary + "\r\n" : "") + "\r\n{\"keep\":\"public-marker\"}"),
                Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n" +
                    JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" })),
                metadata);

            var evidence = ExportRecords(Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None)));

            AssertHeaderHistory(evidence, history);
            Assert.Equal(history == "unknown" ? 1 : 0, SazArchive.UnknownHeaderProvenanceSessions(evidence.Summary));
            if (malformedLine)
            {
                Assert.DoesNotContain(canary, evidence.DecodedContent);
            }
        }

        // A capture saved before the HeadersUninspectable column existed has an unknown history, and so has -1; 0 is no
        // known loss; NULL and any other stored value are uninspectable. A sanitized export of the reloaded session
        // follows the state and carries it on.
        [Theory]
        [InlineData(null, "unknown")]
        [InlineData("-1", "unknown")]
        [InlineData("0", "none")]
        [InlineData("1", "uninspectable")]
        [InlineData("2", "uninspectable")]
        [InlineData("-2", "uninspectable")]
        [InlineData("NULL", "uninspectable")]
        [InlineData("'yes'", "uninspectable")]
        [InlineData("0.5", "uninspectable")]
        public void StoredHeaderHistory_IsUnknownWithoutTheColumnAndUninspectableUnlessValid(string stored, string history)
        {
            var fields = WebProxyCaptureSchema.Fields.Where(field => field.Name != "HeadersUninspectable").ToList();
            if (stored != null)
            {
                fields.Add(new Field<long>("HeadersUninspectable"));
            }
            var path = SaveCapture(PairRecord(1, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}"),
                fields.ToArray());
            if (stored != null)
            {
                using var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE WebProxyConnections SET HeadersUninspectable = " + stored;
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var evidence = ExportRecords(LoadCapture(path).Record);

            AssertHeaderHistory(evidence, history);
            Assert.Equal(history == "unknown" ? 1 : 0, SazArchive.UnknownHeaderProvenanceSessions(evidence.Summary));
        }

        // A session whose header history is unknown is sanitized as stored: a secret that its stored headers or bodies
        // identify is redacted wherever it is echoed, and benign data is kept. The export counts it. An uninspectable
        // session in the same archive is left out and isn't counted.
        [Theory]
        [InlineData("legacy-capture")]
        [InlineData("fiddler-archive")]
        public void UnknownHeaderHistory_IsSanitizedAsStoredWhileAnUninspectablePairIsLeftOut(string source)
        {
            const string token = "q7KnownBearerToken";
            const string password = "q7KnownPassword";
            const string authorization = "Authorization: Bearer " + token + "\r\n";
            var request = JsonSerializer.Serialize(new { password, keep = "public-marker" });
            var response = JsonSerializer.Serialize(
                new { echo = token, again = password, note = "benign-note", keep = "public-marker" });
            ProxyConnectionModel unknown;
            if (source == "legacy-capture")
            {
                unknown = LoadCapture(SaveCapture(
                    PairRecord(1, request, response, extraRequestHeaders: authorization),
                    WebProxyCaptureSchema.Fields.Where(field => field.Name != "HeadersUninspectable").ToArray())).Record;
            }
            else
            {
                var archive = Path.Join(_directory, "fiddler.saz");
                SazArchive.WriteRaw(archive,
                    Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n" +
                        authorization + "\r\n" + request),
                    Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n" + response));
                unknown = Assert.Single(FiddlerSazHandler.ImportSazFile(archive, CancellationToken.None));
            }
            var path = Path.Join(_directory, "uninspectable.saz");
            SazArchive.WriteRaw(path,
                Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n" +
                    "\r\n{\"keep\":\"public-marker\"}"),
                Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"keep\":\"public-marker\"}"),
                new KeyValuePair<string, string>("x-xmat-request-number", "2"),
                new KeyValuePair<string, string>("x-xmat-headers-uninspectable", "1"));
            var uninspectable = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));

            var evidence = ExportRecords(unknown, uninspectable);
            using var echoed = JsonDocument.Parse(Body(evidence));

            Assert.DoesNotContain(token, evidence.DecodedContent);
            Assert.DoesNotContain(password, evidence.DecodedContent);
            Assert.Equal("benign-note", echoed.RootElement.GetProperty("note").GetString());
            AssertHeaderHistory(evidence, "unknown");
            AssertHeaderHistory(evidence, "uninspectable", 2);
            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Equal(1, SazArchive.UnknownHeaderProvenanceSessions(evidence.Summary));
        }

        // A sanitized archive carries each session's header history, so a sanitized export of its import keeps it: an
        // unknown history is counted again, and an uninspectable pair is still left out.
        [Fact]
        public void SanitizedReexport_KeepsEachHeaderHistory()
        {
            var records = new[] { "-1", "0", "1" }.Select((flag, index) =>
            {
                var path = Path.Join(_directory, $"history-{index + 1}.saz");
                SazArchive.WriteRaw(path,
                    Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n" +
                        "\r\n{\"keep\":\"public-marker\"}"),
                    Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"keep\":\"public-marker\"}"),
                    new KeyValuePair<string, string>(
                        "x-xmat-request-number", (index + 1).ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("x-xmat-headers-uninspectable", flag));
                return Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            }).ToArray();
            var sanitized = Path.Join(_directory, "sanitized-once.saz");
            SazArchive.Write(sanitized, records, sanitized: true);

            var evidence = ExportRecords(FiddlerSazHandler.ImportSazFile(sanitized, CancellationToken.None).ToArray());

            AssertHeaderHistory(evidence, "unknown", 1);
            AssertHeaderHistory(evidence, "none", 2);
            AssertHeaderHistory(evidence, "uninspectable", 3);
            Assert.Equal(1, SazArchive.UnknownHeaderProvenanceSessions(evidence.Summary));
        }

        // A SAZ header value with a byte above 0x7F, such as a no-break space (0xA0) or a NEL (0x85), is imported as that
        // character rather than '?', so the secret is learned as stored and, like any edge NEL or no-break space, without
        // it: an echo of either form in the other message is redacted.
        [Theory]
        [InlineData(0xA0, true, false)]
        [InlineData(0x85, false, false)]
        [InlineData(0x85, true, true)]
        [InlineData(0xA0, false, true)]
        public void ImportedHeaderBytesAboveAscii_AreLearnedSoEitherEchoIsRedacted(int edge, bool inRequest, bool leading)
        {
            const string canary = "q7Opaque";
            var exact = leading ? (char)edge + canary : canary + (char)edge;
            var field = "x-session-token: " + exact + "\r\n";
            var echo = JsonSerializer.Serialize(new { exact, trimmed = canary, keep = "public-marker" });
            var requestHead = "POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n" +
                (inRequest ? field : "") + "\r\n";
            var responseHead = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + (inRequest ? "" : field) + "\r\n";
            var path = Path.Join(_directory, "latin1-headers.saz");
            SazArchive.WriteRaw(path,
                Encoding.Latin1.GetBytes(requestHead + (inRequest ? "{\"keep\":\"public-marker\"}" : echo)),
                Encoding.Latin1.GetBytes(responseHead + (inRequest ? echo : "{\"keep\":\"public-marker\"}")));
            var imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal((requestHead, responseHead), (imported.RequestLineAndHeaders, imported.ResponseLineAndHeaders));

            var evidence = ExportRecords(imported);
            using var echoed = JsonDocument.Parse(Body(evidence, !inRequest));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, echoed.RootElement.GetProperty("exact").GetString());
            Assert.Equal(SazSanitizer.Redacted, echoed.RootElement.GetProperty("trimmed").GetString());
            Assert.Equal("public-marker", echoed.RootElement.GetProperty("keep").GetString());
        }

        // Read as one character per byte, a header line of only a NEL or a no-break space is header data that isn't a
        // field, not the empty line that ends the headers: the fields after it stay headers, and the import marks the
        // session for the line it leaves out.
        [Theory]
        [InlineData(0xA0)]
        [InlineData(0x85)]
        public void ImportedHeaderLinesOfANelOrNoBreakSpace_DoNotEndTheHeaders(int edge)
        {
            const string canary = "AFTER-EDGE-CANARY";
            var path = Path.Join(_directory, "edge-line.saz");
            SazArchive.WriteRaw(path,
                Encoding.Latin1.GetBytes("POST / HTTP/1.1\r\nHost: example.test\r\n" + (char)edge + "\r\n" +
                    "x-session-token: " + canary + "\r\nContent-Type: application/json\r\n\r\n{\"keep\":\"public-marker\"}"),
                Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n" +
                    JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" })));
            var imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal(
                "POST / HTTP/1.1\r\nHost: example.test\r\nx-session-token: " + canary +
                "\r\nContent-Type: application/json\r\n\r\n",
                imported.RequestLineAndHeaders);
            Assert.Equal("{\"keep\":\"public-marker\"}", Encoding.ASCII.GetString(imported.RequestBody));

            var evidence = ExportRecords(imported);

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        // XMAT's header encoding flags are trusted only as XMAT writes them. A value other than utf-8, flags that disagree,
        // or bytes that aren't UTF-8 under the utf-8 flag leave that message's text as Latin-1, which keeps every byte
        // rather than replacing any, and mark the session, so a sanitized export leaves the pair out instead of learning a
        // value it can't be sure of.
        [Theory]
        [InlineData("utf-16", false, true)]
        [InlineData("utf-8|iso-8859-1", false, true)]
        [InlineData("utf-8", true, true)]
        [InlineData("x-unknown", false, false)]
        [InlineData("utf-8", true, false)]
        public void UnusableHeaderEncodingFlags_KeepTheBytesAndLeaveThePairOut(string hints, bool invalidUtf8, bool inRequest)
        {
            const string canary = "q7Opaque";
            // An e-acute after the canary as its two UTF-8 bytes, or as one Latin-1 byte that isn't UTF-8.
            var field = "x-session-token: " + canary + (invalidUtf8 ? "\u00E9" : "\u00C3\u00A9") + "\r\n";
            var echo = JsonSerializer.Serialize(new { exact = canary + "\u00E9", trimmed = canary, keep = "public-marker" });
            var requestHead = "POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n" +
                (inRequest ? field : "") + "\r\n";
            var responseHead = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + (inRequest ? "" : field) + "\r\n";
            var flag = inRequest ? "x-xmat-request-header-encoding" : "x-xmat-response-header-encoding";
            var path = Path.Join(_directory, "unusable-encoding.saz");
            SazArchive.WriteRaw(path,
                Encoding.Latin1.GetBytes(requestHead + (inRequest ? "{\"keep\":\"public-marker\"}" : echo)),
                Encoding.Latin1.GetBytes(responseHead + (inRequest ? echo : "{\"keep\":\"public-marker\"}")),
                hints.Split('|').Select(hint => new KeyValuePair<string, string>(flag, hint)).ToArray());
            var imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal((requestHead, responseHead), (imported.RequestLineAndHeaders, imported.ResponseLineAndHeaders));

            var evidence = ExportRecords(imported);

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        // Under XMAT's utf-8 flag an import keeps a header value above U+00FF, one outside the Basic Multilingual Plane
        // included, as it was written. A sanitized export still leaves the pair out, as for any such value (see
        // HeaderValuesWithControlOrNonLatin1Characters_MakeThePairOpaque), whether or not the field is sensitive.
        [Theory]
        [InlineData("x-session-token", true)]
        [InlineData("x-display-name", false)]
        public void Utf8HeaderValues_AreKeptOnImportButLeaveASanitizedPairOut(string name, bool inRequest)
        {
            const string canary = "q7Opaque";
            var field = name + ": " + canary + "\u20AC\U0001F600\r\n";
            var echo = JsonSerializer.Serialize(new { echo = canary, keep = "public-marker" });
            var requestHead = "POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n" +
                (inRequest ? field : "") + "\r\n";
            var responseHead = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + (inRequest ? "" : field) + "\r\n";
            var path = Path.Join(_directory, "utf8-headers.saz");
            SazArchive.WriteRaw(path,
                Encoding.UTF8.GetBytes(requestHead + (inRequest ? "{\"keep\":\"public-marker\"}" : echo)),
                Encoding.UTF8.GetBytes(responseHead + (inRequest ? echo : "{\"keep\":\"public-marker\"}")),
                new KeyValuePair<string, string>(
                    inRequest ? "x-xmat-request-header-encoding" : "x-xmat-response-header-encoding", "utf-8"));
            var imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Equal((requestHead, responseHead), (imported.RequestLineAndHeaders, imported.ResponseLineAndHeaders));

            var evidence = ExportRecords(imported);

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.DoesNotContain(canary, evidence.DecodedContent);
        }

        // A header's bytes can't be proved to be Latin-1 or UTF-8: without a flag, "q7caf" and the UTF-8 bytes of an e-acute
        // import as the Latin-1 text "q7caf" + U+00C3 U+00A9 while an echo has "q7caf" + U+00E9, and a utf-8 flag on the
        // same bytes, declared or forged, gives the reverse. Either reading of an identified secret is learned, so an echo
        // of each is redacted, while a benign value with the same ambiguity is kept.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EncodingReadingsOfASecret_AreRedactedWhereverEitherIsEchoed(bool utf8Flag)
        {
            const string latin1 = "q7caf\u00C3\u00A9";
            const string utf8 = "q7caf\u00E9";
            var echo = JsonSerializer.Serialize(new { latin1, utf8, note = "caf\u00E9", keep = "public-marker" });
            var path = Path.Join(_directory, "ambiguous-encoding.saz");
            SazArchive.WriteRaw(path,
                Encoding.Latin1.GetBytes("POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n" +
                    "x-session-token: " + latin1 + "\r\nx-note: caf\u00C3\u00A9\r\n\r\n{\"keep\":\"public-marker\"}"),
                Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n" + echo),
                utf8Flag
                    ? new[] { new KeyValuePair<string, string>("x-xmat-request-header-encoding", "utf-8") }
                    : Array.Empty<KeyValuePair<string, string>>());
            var imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            Assert.Contains("x-session-token: " + (utf8Flag ? utf8 : latin1) + "\r\n", imported.RequestLineAndHeaders);

            var evidence = ExportRecords(imported);
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("q7caf", evidence.DecodedContent);
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("latin1").GetString());
            Assert.Equal(SazSanitizer.Redacted, response.RootElement.GetProperty("utf8").GetString());
            Assert.Equal("caf\u00E9", response.RootElement.GetProperty("note").GetString());
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
        }

        // The other reading of a secret is learned trimmed as well as whole. The bytes of "q7caf" and of an e-acute and a
        // no-break space or NEL in UTF-8 import without a flag as Latin-1 text whose UTF-8 reading is "q7caf" + U+00E9 + the
        // edge character, and under a utf-8 flag as that text, whose Latin-1 reading is the bytes again. Every form of the
        // other reading, trimmed before or after it is taken, is redacted where it is echoed.
        [Theory]
        [InlineData(false, 0xA0)]
        [InlineData(false, 0x85)]
        [InlineData(true, 0xA0)]
        [InlineData(true, 0x85)]
        public void TrimmedEncodingReadingsOfASecret_AreRedacted(bool utf8Flag, int edge)
        {
            var bytes = "q7caf\u00C3\u00A9\u00C2" + (char)edge;
            var echoes = utf8Flag
                ? new[] { bytes, "q7caf\u00C3\u00A9\u00C2", "q7caf\u00C3\u00A9" }
                : new[] { "q7caf\u00E9" + (char)edge, "q7caf\u00E9" };
            var echo = JsonSerializer.Serialize(new { echoes, keep = "public-marker" });
            var path = Path.Join(_directory, "ambiguous-edge.saz");
            SazArchive.WriteRaw(path,
                Encoding.Latin1.GetBytes("POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n" +
                    "x-session-token: " + bytes + "\r\n\r\n{\"keep\":\"public-marker\"}"),
                Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n" + echo),
                utf8Flag
                    ? new[] { new KeyValuePair<string, string>("x-xmat-request-header-encoding", "utf-8") }
                    : Array.Empty<KeyValuePair<string, string>>());

            var evidence = ExportRecords(Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None)));
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("q7caf", evidence.DecodedContent);
            Assert.Equal(echoes.Length, response.RootElement.GetProperty("echoes").GetArrayLength());
            Assert.All(response.RootElement.GetProperty("echoes").EnumerateArray(),
                element => Assert.Equal(SazSanitizer.Redacted, element.GetString()));
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
        }

        // A secret's other readings count toward the pair's capacity like any learned form: twenty values that fit with
        // their trimmed forms don't fit with their readings, so the pair is left out. The next session is unaffected.
        [Fact]
        public void EncodingReadingsOfSecrets_CountTowardThePairCapacity()
        {
            var fields = string.Concat(Enumerable.Range(0, 20)
                .Select(index => $"x-api-token-{index:D2}: ALIAS-{index:D2}\u00C3\u00A9\u00C2\u00A0\r\n"));

            var evidence = ExportRecords(
                PairRecord(1, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}", extraRequestHeaders: fields),
                PairRecord(2, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Equal(0, evidence.Summary.WorkBudgetOmissions);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.DoesNotContain("ALIAS-", evidence.DecodedContent);
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, true, 2)));
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        // As for a header value, a character above U+00FF in a start line leaves the pair out of a sanitized export; the
        // next session is unaffected.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void StartLinesWithCharactersAboveLatin1_LeaveThePairOut(bool inRequest)
        {
            var target = inRequest ? "/menu/\u20AC" : "/";
            var first = new ProxyConnectionModel(1, "200", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(20), "https", "example.test:443", "POST", target,
                "POST " + target + " HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/json\r\n\r\n",
                Encoding.UTF8.GetBytes("{\"keep\":\"public-marker\"}"),
                "HTTP/1.1 200 " + (inRequest ? "OK" : "\u00C9t\u00E9 \u20AC") + "\r\nContent-Type: application/json\r\n\r\n",
                Encoding.UTF8.GetBytes("{\"keep\":\"public-marker\"}"), "192.0.2.27")
            { RecordRowId = 101 };

            var evidence = ExportRecords(first,
                PairRecord(2, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}"));

            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, true, 2)));
            Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, false, 2)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ImportedRepeatedCookieHeaders_AreSplitWithoutTreatingExpiresDatesAsCookies(bool expires)
        {
            var original = Path.Join(_directory, "original-cookies.saz");
            var source = PairRecord(1, "", "{\"debug\":\"sid IMPORT-SETCOOKIE-CANARY and IMPORT-COOKIE-CANARY\",\"keep\":\"public-marker\"}",
                "text/plain", "/home",
                "Cookie: a=1\r\nCookie: sid=IMPORT-COOKIE-CANARY\r\n",
                "Set-Cookie: lang=en; Path=/\r\nSet-Cookie: sid=IMPORT-SETCOOKIE-CANARY" +
                (expires ? "; Expires=Wed, 21 Oct 2037 07:28:00 GMT" : "") + "; HttpOnly\r\n");
            SazArchive.Write(original, new[] { source });
            var imported = Assert.Single(FiddlerSazHandler.ImportSazFile(original, CancellationToken.None));
            Assert.Contains("\r\nCookie: a=1\r\nCookie: sid=IMPORT-COOKIE-CANARY\r\n", imported.RequestLineAndHeaders);
            Assert.Contains(
                "\r\nSet-Cookie: lang=en; Path=/\r\nSet-Cookie: sid=IMPORT-SETCOOKIE-CANARY", imported.ResponseLineAndHeaders);
            var evidence = ExportRecords(imported);
            using var response = JsonDocument.Parse(Body(evidence));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("IMPORT-COOKIE-CANARY", evidence.DecodedContent);
            Assert.DoesNotContain("IMPORT-SETCOOKIE-CANARY", evidence.DecodedContent);
            Assert.Equal("public-marker", response.RootElement.GetProperty("keep").GetString());
        }

        [Theory]
        [InlineData("query")]
        [InlineData("encoded-query")]
        [InlineData("form")]
        public void SensitiveParameterNames_AreInspectedWithoutChangingUnrelatedParameters(string source)
        {
            const string email = "private-name@unit.example";
            var name = source == "encoded-query" ? Uri.EscapeDataString(email) : email;
            var evidence = ExportRecords(PairRecord(1,
                source == "form" ? "members[" + email + "]=admin&keep=public-marker" : "keep=public-marker",
                "{\"keep\":\"public-marker\"}",
                source == "form" ? "application/x-www-form-urlencoded" : "text/plain",
                source == "form" ? "/" : "/?" + name + "=1&keep=public-marker"));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain(email, evidence.DecodedContent);
            Assert.Contains("keep=public-marker", evidence.DecodedContent);
        }

        [Fact]
        public void DiscoveryBudgetFailure_DiscardsTheWholePairRatherThanPublishingAnUnlearnedEcho()
        {
            var value = "https://public.test/?next=https://public.test/path/" +
                new string('a', SazSanitizer.MaximumBodyBytes - 1024 - 64);
            for (var pass = 0; pass < 7; pass++)
            {
                value = Uri.EscapeDataString(value);
            }
            var request = "next=" + value + "&password=BUDGET-SECRET-CANARY";
            Assert.True(Encoding.UTF8.GetByteCount(request) <= SazSanitizer.MaximumBodyBytes);
            var evidence = ExportRecords(PairRecord(1, request, "{\"echo\":\"BUDGET-SECRET-CANARY\"}",
                "application/x-www-form-urlencoded"));

            Assert.Equal(1, evidence.Summary.WorkBudgetOmissions);
            Assert.Equal(2, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("BUDGET-SECRET-CANARY", evidence.DecodedContent);
            Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
            Assert.Empty(Body(evidence));
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(7, false)]
        [InlineData(7, true)]
        public void HeaderBudgetFailures_ProduceOpaqueFramingWithoutAbortingOrPublishingPartialOutput(
            int megabytes, bool inResponse)
        {
            var header = "X-Trace: " + new string('a', megabytes * 1024 * 1024) + "\r\n";
            var evidence = ExportRecords(PairRecord(1, "{\"keep\":\"public-marker\"}", "{\"keep\":\"public-marker\"}",
                extraRequestHeaders: inResponse ? "" : header,
                extraResponseHeaders: inResponse ? header : ""));

            if (megabytes == 1)
            {
                Assert.Equal(0, evidence.Summary.OmittedBodies);
                Assert.Contains("public-marker", evidence.DecodedContent);
            }
            else
            {
                Assert.Equal(1, evidence.Summary.WorkBudgetOmissions);
                Assert.Equal(2, evidence.Summary.OmittedBodies);
                Assert.Empty(Body(evidence, true));
                Assert.Empty(Body(evidence));
                Assert.DoesNotContain("public-marker", evidence.DecodedContent);
                Assert.Contains("Host: redacted.invalid", evidence.DecodedContent);
            }
        }

        [Fact]
        public void ZeroPaddedNonzeroChunkSizes_AreDetectedAfterAMalformedFirstLine()
        {
            var evidence = Export("text/plain",
                "Z\r\n0000000004\r\npass\r\n0000000011\r\nword=ZERO-PAD-CANARY\r\n",
                requestBody: true, extraRequestHeaders: "Transfer-Encoding: chunked\r\n");

            Assert.Equal(1, evidence.Summary.OmittedBodies);
            Assert.DoesNotContain("ZERO-PAD-CANARY", evidence.DecodedContent);
            Assert.Empty(Body(evidence, true));
        }

        [Fact]
        public void LosslessInspection_PreservesLiteralPlusesAndJsonEscapesInBenignControls()
        {
            const string value = "PUBLIC+PLUS/SLASH";
            var evidence = ExportRecords(PairRecord(1,
                JsonSerializer.Serialize(new { keep = value }),
                JsonSerializer.Serialize(new { next = "https://example.test/?keep=" + value }),
                target: "/?keep=" + value));

            Assert.Equal(0, evidence.Summary.OmittedBodies);
            Assert.Contains(value, evidence.DecodedContent);
            Assert.Contains("\\u002B", Encoding.UTF8.GetString(Body(evidence)), StringComparison.OrdinalIgnoreCase);
            using var response = JsonDocument.Parse(Body(evidence));
            Assert.Contains(value, response.RootElement.GetProperty("next").GetString());
        }

        private Evidence Export(string type, string body, bool requestBody = false, string target = "/",
            string extraRequestHeaders = "", string extraResponseHeaders = "", string pairedResponse = null,
            string status = "200") =>
            Export(type, Encoding.UTF8.GetBytes(body), requestBody, target, extraRequestHeaders,
                extraResponseHeaders, pairedResponse, status);

        private Evidence Export(string type, byte[] body, bool requestBody = false, string target = "/",
            string extraRequestHeaders = "", string extraResponseHeaders = "", string pairedResponse = null,
            string status = "200")
        {
            var method = requestBody ? "POST" : "GET";
            var record = new ProxyConnectionModel(1, status,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMilliseconds(20),
                "https", "example.test:443", method, target,
                method + " " + target + " HTTP/1.1\r\nHost: example.test\r\n" + extraRequestHeaders +
                (requestBody ? "Content-Type: " + type + "\r\n" : string.Empty) + "\r\n",
                requestBody ? body : Array.Empty<byte>(),
                "HTTP/1.1 " + status + " Synthetic\r\nContent-Type: " + type + "\r\n" + extraResponseHeaders + "\r\n",
                pairedResponse != null ? Encoding.UTF8.GetBytes(pairedResponse) :
                    requestBody ? Array.Empty<byte>() : body, "192.0.2.27")
            {
                RecordRowId = 101
            };
            return ExportRecords(record);
        }

        private Evidence ExportRecords(params ProxyConnectionModel[] records)
        {
            var path = Path.Join(_directory, Guid.NewGuid().ToString("N") + ".saz");
            var originals = records.Select(record => (
                Request: record.RequestBody.ToArray(), Response: record.ResponseBody.ToArray(),
                RequestHeaders: record.RequestLineAndHeaders, ResponseHeaders: record.ResponseLineAndHeaders)).ToArray();
            var summary = SazArchive.Write(path, records, sanitized: true);
            for (var index = 0; index < records.Length; index++)
            {
                Assert.Equal(originals[index].Request, records[index].RequestBody);
                Assert.Equal(originals[index].Response, records[index].ResponseBody);
                Assert.Equal(originals[index].RequestHeaders, records[index].RequestLineAndHeaders);
                Assert.Equal(originals[index].ResponseHeaders, records[index].ResponseLineAndHeaders);
            }
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries.ToDictionary(entry => entry.FullName, entry =>
            {
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                return buffer.ToArray();
            });
            Assert.Equal(records.SelectMany(record => new[]
                {
                    $"raw/{record.RequestNumber}_c.txt", $"raw/{record.RequestNumber}_m.xml", $"raw/{record.RequestNumber}_s.txt"
                }).OrderBy(name => name, StringComparer.Ordinal),
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
            var reimported = FiddlerSazHandler.ImportSazFile(path, CancellationToken.None);
            Assert.Equal(records.Length, reimported.Count);
            Assert.Equal(records.Length, summary.Sessions);
            var omissions = 0;
            foreach (var record in records)
            {
                var metadata = XDocument.Parse(Encoding.UTF8.GetString(entries[$"raw/{record.RequestNumber}_m.xml"]));
                Assert.Equal(record.RecordRowId.ToString(CultureInfo.InvariantCulture), (string)metadata.Root.Attribute("SID"));
                Assert.Equal("2100", (string)metadata.Root.Attribute("BitFlags"));
                Assert.Equal(16, metadata.Root.Element("SessionTimers").Attributes().Count());
                var flags = metadata.Descendants("SessionFlag").ToDictionary(flag => (string)flag.Attribute("N"),
                    flag => (string)flag.Attribute("V"));
                Assert.Equal("0.0.0.0", flags["x-clientip"]);
                omissions += flags.Keys.Count(key => key.EndsWith("-body-omitted", StringComparison.Ordinal));
                Assert.All(flags.Keys, key => Assert.Contains(key, new[]
                {
                    "x-clientip", "x-xmat-request-body-omitted", "x-xmat-response-body-omitted", "x-xmat-request-number",
                    "x-xmat-connection-id", "x-xmat-request-timestamp", "x-xmat-response-timestamp", "x-xmat-scheme",
                    "x-xmat-headers-uninspectable",
                }));
                if (flags.ContainsKey("x-xmat-request-body-omitted"))
                {
                    Assert.Empty(bodies[$"raw/{record.RequestNumber}_c.txt"]);
                }
                if (flags.ContainsKey("x-xmat-response-body-omitted"))
                {
                    Assert.Empty(bodies[$"raw/{record.RequestNumber}_s.txt"]);
                }
                var imported = Assert.Single(reimported, item => item.RequestNumber == record.RequestNumber);
                Assert.Equal(record.Status, imported.Status);
                Assert.Equal(record.Method, imported.Method);
                Assert.Equal("0.0.0.0", imported.ClientIP);
                Assert.Equal(record.RequestTime.ToUniversalTime(), imported.RequestTime.ToUniversalTime());
                Assert.Equal(record.Duration, imported.Duration);
                Assert.Equal(bodies[$"raw/{record.RequestNumber}_c.txt"], imported.RequestBody);
                Assert.Equal(bodies[$"raw/{record.RequestNumber}_s.txt"], imported.ResponseBody);
            }
            Assert.Equal(summary.OmittedBodies, omissions);
            var views = new List<string>();
            foreach (var entry in entries)
            {
                views.Add(Encoding.UTF8.GetString(entry.Value));
                if (!entry.Key.EndsWith(".txt", StringComparison.Ordinal))
                {
                    continue;
                }
                var separator = entry.Value.AsSpan().IndexOf("\r\n\r\n"u8);
                try
                {
                    using var document = JsonDocument.Parse(entry.Value.AsMemory(separator + 4));
                    AddJsonViews(document.RootElement, views);
                }
                catch (JsonException)
                {
                }
            }
            var decodedViews = new List<string>();
            foreach (var view in views)
            {
                var text = view;
                decodedViews.Add(text);
                for (var pass = 0; pass < 16; pass++)
                {
                    var decoded = Uri.UnescapeDataString(WebUtility.HtmlDecode(text));
                    decodedViews.Add(decoded);
                    if (decoded == text)
                    {
                        break;
                    }
                    text = decoded;
                }
            }
            return new(string.Join("\n", decodedViews), summary, entries);
        }

        private static void AddJsonViews(JsonElement element, List<string> views)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    views.Add(property.Name);
                    AddJsonViews(property.Value, views);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    AddJsonViews(child, views);
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                views.Add(element.GetString());
            }
            else
            {
                views.Add(element.GetRawText());
            }
        }

        private static byte[] Body(Evidence evidence, bool request = false, long number = 1)
        {
            var message = evidence.Entries[$"raw/{number}_{(request ? "c" : "s")}.txt"];
            return message.AsSpan(message.AsSpan().IndexOf("\r\n\r\n"u8) + 4).ToArray();
        }

        // A sanitized session as its header history leaves it: an uninspectable pair is left out, and any other is
        // sanitized as stored, keeping its benign marker. The archive carries the history either way.
        private static void AssertHeaderHistory(Evidence evidence, string history, long number = 1)
        {
            var flags = ProcessCaptures.SessionFlags(evidence.Entries[$"raw/{number}_m.xml"])
                .Where(flag => flag.Key == "x-xmat-headers-uninspectable")
                .Select(flag => flag.Value);
            Assert.Equal(new[] { history switch { "unknown" => "-1", "none" => "0", _ => "1" } }, flags);
            if (history == "uninspectable")
            {
                Assert.Empty(Body(evidence, true, number));
                Assert.Empty(Body(evidence, false, number));
            }
            else
            {
                Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, true, number)));
                Assert.Contains("public-marker", Encoding.UTF8.GetString(Body(evidence, false, number)));
            }
        }

        private static ProxyConnectionModel PairRecord(long number, string request, string response,
            string requestType = "application/json", string target = "/", string extraRequestHeaders = "",
            string extraResponseHeaders = "") =>
            new(number, "200", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMilliseconds(20),
                "https", "example.test:443", "POST", target,
                "POST " + target + " HTTP/1.1\r\nHost: example.test\r\nContent-Type: " + requestType + "\r\n" +
                extraRequestHeaders + "\r\n",
                Encoding.UTF8.GetBytes(request),
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + extraResponseHeaders + "\r\n",
                Encoding.UTF8.GetBytes(response), "192.0.2.27")
            { RecordRowId = number + 100 };

        // Saves the record as Save Captures does, in a capture file whose web proxy table has the given columns.
        private string SaveCapture(ProxyConnectionModel record, IField[] fields)
        {
            var path = Path.Join(_directory, Guid.NewGuid().ToString("N") + ".xmatcap");
            using var database = new SqLtDatabase();
            database.Initialize();
            database.CreateTable("CaptureDevices", new Field<long>("DeviceType"), new Field<string>("DeviceName"),
                new Field<long>("CaptureType")).AddRow((long)DeviceType.LocalPC, "Saved PC", (long)CaptureType.WebProxy);
            var table = database.CreateTable(WebServiceCaptureMethod.TableName, fields);
            table.AddRow(record.ToDataTableValues("Saved PC", table));
            database.SaveToFile(path);
            return path;
        }

        // Loads a saved capture's only session as Load Captures does, with the HeadersUninspectable value it stores (null
        // without the column).
        private static (ProxyConnectionModel Record, object Mark) LoadCapture(string path)
        {
            using var database = new SqLtReadonlyDatabase(path);
            var record = Assert.Single(database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
            return (new ProxyConnectionModel(record),
                ((IRawDataRecord)record).TryReadRaw("HeadersUninspectable", out var mark) ? mark : null);
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

        // A field value with a character outside HTTP's field-value grammar; String.Trim() would remove the VT, the FF and
        // the ideographic space, and no encoding can hold an unpaired surrogate.
        private static string UnusualValue(string value, string kind) => kind switch
        {
            "del" => value + "\u007F",
            "control" => value + "\u0001",
            "beyond-latin1" => value + "\u0100",
            "leading-vt" => "\u000B" + value,
            "trailing-vt" => value + "\u000B",
            "leading-ff" => "\u000C" + value,
            "trailing-ff" => value + "\u000C",
            "unpaired-surrogate" => value + "\uD800",
            _ => value + "\u3000",
        };

        public void Dispose() => TestFolderCleanup.Delete(_directory);
    }
}
