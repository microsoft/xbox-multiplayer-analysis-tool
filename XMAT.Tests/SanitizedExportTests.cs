// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class SanitizedExportTests : IDisposable
    {
        private readonly CaptureUiApplicationFixture _application;
        private readonly string _directory = Path.Join(Path.GetTempPath(), "XMAT-sanitized-export-" + Guid.NewGuid().ToString("N"));

        public SanitizedExportTests(CaptureUiApplicationFixture application)
        {
            _application = application;
            Directory.CreateDirectory(_directory);
            Localization.LoadLanguage("en-us");
        }

        [Fact]
        public void Archive_RedactsCredentialsWithoutChangingSourceOrOrdinaryExport()
        {
            var record = Record();
            var originalRequest = record.RequestLineAndHeaders;
            var originalResponse = record.ResponseLineAndHeaders;
            var originalBody = record.RequestBody.ToArray();
            var ordinary = Path.Join(_directory, "ordinary.saz");
            FiddlerSazHandler.CreateSazFile(ordinary, new[] { record });
            var ordinaryBytes = File.ReadAllBytes(ordinary);

            var path = Path.Join(_directory, "sanitized.saz");
            var summary = FiddlerSazHandler.CreateSanitizedSazFile(path, new[] { record });
            var entries = Entries(path);
            var all = string.Join("\n", entries.Values.Select(Encoding.UTF8.GetString));
            foreach (var secret in new[]
            {
                "header-canary", "cookie-canary", "query-canary", "body-canary",
                "response-canary", "response-body-canary", "192.0.2.77"
            })
            {
                Assert.DoesNotContain(secret, all);
            }
            Assert.Contains("keep=ok", all);
            Assert.Contains("\"visible\":\"keep\"", all);
            Assert.Contains("Host: example.test", all);
            Assert.Contains("0.0.0.0", all);
            Assert.Equal(1, summary.Sessions);
            Assert.True(summary.RedactedValues >= 7);
            Assert.Equal(0, summary.OmittedBodies);
            Assert.Equal(originalRequest, record.RequestLineAndHeaders);
            Assert.Equal(originalResponse, record.ResponseLineAndHeaders);
            Assert.Equal(originalBody, record.RequestBody);
            Assert.Equal(ordinaryBytes, File.ReadAllBytes(ordinary));
            Assert.Contains("header-canary", Encoding.UTF8.GetString(Entries(ordinary)["raw/1_c.txt"]));

            var imported = FiddlerSazHandler.ImportSazFile(path, CancellationToken.None);
            var reloaded = Assert.Single(imported);
            Assert.Equal("200", reloaded.Status);
            Assert.Equal("POST", reloaded.Method);
            Assert.Contains(SazSanitizer.Redacted, reloaded.RequestLineAndHeaders);
            Assert.Equal("keep", JsonDocument.Parse(reloaded.RequestBody).RootElement.GetProperty("visible").GetString());
        }

        [Theory]
        [InlineData("authorization")]
        [InlineData("Proxy-Authorization")]
        [InlineData("Cookie")]
        [InlineData("X-Api-Key")]
        [InlineData("Ocp-Apim-Subscription-Key")]
        [InlineData("X-XBL-Authorization")]
        [InlineData("X-Forwarded-For")]
        [InlineData("X-Auth")]
        [InlineData("X-Functions-Key")]
        [InlineData("X-MS-Encryption-Key")]
        public void Headers_RedactMixedCaseRepeatedAndFoldedValues(string name)
        {
            var headers = "GET / HTTP/1.1\r\nHost: example.test\r\n" +
                name + ": Bearer\r\n folded-canary\r\n" +
                name + ": second-canary\r\nX-Keep: keep-value\r\n\r\n";
            var result = new SazSanitizer().SanitizeMessage(headers, Array.Empty<byte>(), true);

            Assert.DoesNotContain("folded-canary", result.Headers);
            Assert.DoesNotContain("second-canary", result.Headers);
            Assert.Equal(2, result.Headers.Split(name + ": " + SazSanitizer.Redacted).Length - 1);
            Assert.Contains("X-Keep: keep-value\r\n", result.Headers);
        }

        [Fact]
        public void Headers_EmptyAuthorizationDoesNotConsumeTheNextHeader()
        {
            var result = new SazSanitizer().SanitizeMessage(
                "GET / HTTP/1.1\r\nAuthorization:\r\nX-Keep: keep-value\r\n\r\n", Array.Empty<byte>(), true);

            Assert.Contains("Authorization: \r\nX-Keep: keep-value\r\n", result.Headers);
        }

        [Fact]
        public void ResponseHeaders_RetainRepeatedCookiesAndDropMalformedLines()
        {
            var sanitizer = new SazSanitizer();
            var result = sanitizer.SanitizeMessage(
                "HTTP/1.1 200 OK\r\nSet-Cookie: first=one-canary\r\nset-cookie: second=two-canary\r\n" +
                "malformed-canary\r\nX-Keep: keep-value\r\n\r\n", Array.Empty<byte>(), false);

            Assert.DoesNotContain("canary", result.Headers);
            Assert.Contains("Set-Cookie: [REDACTED]\r\nset-cookie: [REDACTED]\r\n", result.Headers);
            Assert.Contains("X-Keep: keep-value", result.Headers);
            Assert.Equal(1, sanitizer.Summary.OmittedHeaders);
        }

        [Theory]
        [InlineData("application/json", "{\"PASSWORD\":\"body-canary\",\"keep\":7}")]
        [InlineData("application/problem+json", "{\"nested\":{\"api_key\":\"body-canary\"},\"keep\":7}")]
        [InlineData("application/json", "{\"keys\":[\"body-canary\"],\"keep\":7}")]
        [InlineData("application/xml", "<root><Password>body-canary</Password><keep>7</keep></root>")]
        [InlineData("application/x-www-form-urlencoded", "password=body-canary&keep=7&%61pi_key=body-canary")]
        [InlineData("text/plain", "password = body-canary\nkeep: 7")]
        public void SupportedBodies_RemoveSecretsAndRetainDiagnosticData(string contentType, string body)
        {
            var result = new SazSanitizer().SanitizeMessage(
                RequestHeaders(contentType), Encoding.UTF8.GetBytes(body), true);
            var text = Encoding.UTF8.GetString(result.Body);

            Assert.DoesNotContain("body-canary", text);
            Assert.Contains("7", text);
            Assert.Contains("REDACTED", text);
            Assert.Contains("charset=utf-8", result.Headers);
            Assert.Contains($"Content-Length: {result.Body.Length}\r\n", result.Headers);
        }

        [Fact]
        public void NamedValues_JsonAndXmlCannotHideSecretsBehindNameValuePairs()
        {
            var sanitizer = new SazSanitizer();
            var json = sanitizer.SanitizeMessage(RequestHeaders("application/json"),
                Encoding.UTF8.GetBytes("{\"Name\":\"Password\",\"Value\":\"json-canary\"}"), true);
            var xml = sanitizer.SanitizeMessage(RequestHeaders("application/xml"),
                Encoding.UTF8.GetBytes("<!-- comment-canary --><root><add key=\"ApiKey\" value=\"xml-canary\"/></root>"), true);

            Assert.DoesNotContain("json-canary", Encoding.UTF8.GetString(json.Body));
            Assert.DoesNotContain("xml-canary", Encoding.UTF8.GetString(xml.Body));
            Assert.DoesNotContain("comment-canary", Encoding.UTF8.GetString(xml.Body));
            Assert.True(json.Body.Length > 0);
            Assert.True(xml.Body.Length > 0);
        }

        [Fact]
        public void Xml_NamedSecretTextAndCDataAreRedacted()
        {
            var result = new SazSanitizer().SanitizeMessage(RequestHeaders("application/xml"),
                Encoding.UTF8.GetBytes("<root><Property Name=\"Password\"><![CDATA[text-canary]]></Property>" +
                    "<keep>visible</keep></root>"), true);

            Assert.DoesNotContain("text-canary", Encoding.UTF8.GetString(result.Body));
            Assert.Contains("<keep>visible</keep>", Encoding.UTF8.GetString(result.Body));
            Assert.True(result.Body.Length > 0);
        }

        [Fact]
        public void ContentTypeParameters_AreSanitizedWithoutReintroducingRawValuesOrInvalidSyntax()
        {
            var result = new SazSanitizer().SanitizeMessage(
                RequestHeaders("application/json; api_key=parameter-canary"),
                Encoding.UTF8.GetBytes("{\"keep\":7}"), true);
            var type = result.Headers.Split("\r\n").Single(line =>
                line.StartsWith("Content-Type:", StringComparison.Ordinal)).Substring("Content-Type:".Length).Trim();

            Assert.DoesNotContain("parameter-canary", result.Headers);
            Assert.True(System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(type, out var parsed));
            Assert.Equal("application/json", parsed.MediaType);
            Assert.Contains("7", Encoding.UTF8.GetString(result.Body));
        }

        [Fact]
        public void Urls_RedactCredentialsEncodedParametersNestedRedirectsAndIdentifiers()
        {
            var redirect = Uri.EscapeDataString("https://login.test/return?code=redirect-canary&keep=1");
            var target = "https://user-canary:password-canary@example.test/users/xuid(123456789)" +
                "?%2561ccess_token=query-canary&state=state-canary&keep=yes&next=" + redirect;
            var result = new SazSanitizer().SanitizeMessage(
                "GET " + target + " HTTP/1.1\r\nLocation: /next?api_key=location-canary&keep=ok\r\n\r\n",
                Array.Empty<byte>(), true);

            foreach (var secret in new[]
            {
                "user-canary", "password-canary", "query-canary", "state-canary",
                "redirect-canary", "location-canary", "123456789"
            })
            {
                Assert.DoesNotContain(secret, result.Headers);
            }
            Assert.Contains("keep=yes", result.Headers);
            Assert.Contains("keep=ok", result.Headers);
        }

        [Theory]
        [InlineData("https://")]
        [InlineData("//")]
        [InlineData("wss://")]
        [InlineData("postgresql://")]
        public void UrlCredentials_AreRemovedAcrossSchemesWithoutLosingTheServiceEndpoint(string scheme)
        {
            var endpoint = scheme + "user-canary:password-canary@service.test/path";
            var result = new SazSanitizer().SanitizeMessage(
                RequestHeaders("application/json", "Location: " + endpoint + "\r\n"),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { endpoint })), true);
            var text = Encoding.UTF8.GetString(result.Body);

            Assert.DoesNotContain("user-canary", result.Headers + text);
            Assert.DoesNotContain("password-canary", result.Headers + text);
            Assert.Contains("service.test/path", JsonDocument.Parse(result.Body).RootElement.GetProperty("endpoint").GetString());
        }

        [Fact]
        public void OAuthAndSamlFormCredentials_AreRedacted()
        {
            var result = new SazSanitizer().SanitizeMessage(RequestHeaders("application/x-www-form-urlencoded"),
                Encoding.UTF8.GetBytes("grant_type=authorization_code&code=oauth-canary&SAMLResponse=saml-canary" +
                    "&otp=otp-canary&nonce=nonce-canary&keep=visible"), true);
            var text = Encoding.UTF8.GetString(result.Body);

            Assert.DoesNotContain("canary", text);
            Assert.Contains("keep=visible", text);
        }

        [Fact]
        public void Text_RedactsTokensPrivateKeysAndInlineCookieValues()
        {
            var pat = "ghp_" + new string('a', 32);
            var aws = "AKIA" + new string('A', 16);
            string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var jwt = string.Join(".", Encode("{\"alg\":\"HS256\"}"),
                Encode("{\"sub\":\"test-subject\"}"), Encode("test-signature"));
            var pem = "-----BEGIN " + "PRIVATE KEY-----\nprivate-key-canary\n-----END " + "PRIVATE KEY-----";
            var body = $"token={pat}\n{aws}\n{jwt}\n{pem}\nCookie: a=first-canary; b=second-canary\nkeep: visible";
            var result = new SazSanitizer().SanitizeMessage(RequestHeaders("text/plain"), Encoding.UTF8.GetBytes(body), true);
            var text = Encoding.UTF8.GetString(result.Body);

            foreach (var secret in new[] { pat, aws, jwt, "private-key-canary", "first-canary", "second-canary" })
            {
                Assert.DoesNotContain(secret, text);
            }
            Assert.Contains("keep: visible", text);
        }

        [Fact]
        public void JwtWithEmptyClaims_IsStillRecognizedAsACredential()
        {
            string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=');
            var jwt = string.Join(".", Encode("{\"alg\":\"HS256\"}"), Encode("{}"), Encode("test-signature"));
            var result = new SazSanitizer().SanitizeMessage(RequestHeaders("application/json"),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { data = jwt })), true);

            Assert.DoesNotContain(jwt, Encoding.UTF8.GetString(result.Body));
            Assert.Contains("REDACTED", Encoding.UTF8.GetString(result.Body));
        }

        [Theory]
        [InlineData("gzip")]
        [InlineData("deflate")]
        [InlineData("br")]
        public void Compression_IsInspectedAndExportedWithCorrectUncompressedFraming(string encoding)
        {
            var body = Compress(Encoding.UTF8.GetBytes("{\"token\":\"compressed-canary\",\"keep\":\"caf\u00e9\"}"), encoding);
            var result = new SazSanitizer().SanitizeMessage(
                RequestHeaders("application/json", "Content-Encoding: " + encoding + "\r\n" +
                    "Transfer-Encoding: chunked\r\nTrailer: Digest\r\nDigest: old\r\nETag: old\r\n"),
                body, true);

            Assert.DoesNotContain("compressed-canary", Encoding.UTF8.GetString(result.Body));
            Assert.Contains("caf", JsonDocument.Parse(result.Body).RootElement.GetProperty("keep").GetString());
            Assert.DoesNotContain("Content-Encoding:", result.Headers);
            Assert.DoesNotContain("Transfer-Encoding:", result.Headers);
            Assert.DoesNotContain("Trailer:", result.Headers);
            Assert.DoesNotContain("Digest:", result.Headers);
            Assert.DoesNotContain("ETag:", result.Headers);
            Assert.Contains($"Content-Length: {result.Body.Length}\r\n", result.Headers);
        }

        [Theory]
        [InlineData("utf-16")]
        [InlineData("iso-8859-1")]
        public void DeclaredCharsets_AreDecodedBeforeScanning(string charset)
        {
            var encoding = Encoding.GetEncoding(charset);
            var body = encoding.GetBytes("{\"password\":\"charset-canary\",\"keep\":\"caf\u00e9\"}");
            var result = new SazSanitizer().SanitizeMessage(RequestHeaders("application/json; charset=" + charset), body, true);

            Assert.DoesNotContain("charset-canary", Encoding.UTF8.GetString(result.Body));
            Assert.Equal("caf\u00e9", JsonDocument.Parse(result.Body).RootElement.GetProperty("keep").GetString());
            Assert.Contains("charset=utf-8", result.Headers);
        }

        [Theory]
        [InlineData("utf-8")]
        [InlineData("utf-16")]
        [InlineData("utf-16BE")]
        [InlineData("utf-32")]
        [InlineData("utf-32BE")]
        public void ByteOrderMarks_AreRecognizedBeforeTextInspection(string charset)
        {
            var encoding = Encoding.GetEncoding(charset);
            var body = encoding.GetPreamble().Concat(
                encoding.GetBytes("{\"password\":\"bom-canary\",\"keep\":7}")).ToArray();
            var result = new SazSanitizer().SanitizeMessage(RequestHeaders("application/json"), body, true);

            Assert.True(result.Body.Length > 0);
            Assert.DoesNotContain("bom-canary", Encoding.UTF8.GetString(result.Body));
            Assert.Equal(7, JsonDocument.Parse(result.Body).RootElement.GetProperty("keep").GetInt32());
        }

        [Theory]
        [InlineData("application/octet-stream", "binary-canary", "")]
        [InlineData("text/html", "<input value='html-canary'>", "")]
        [InlineData("multipart/form-data; boundary=sample", "multipart-canary", "")]
        [InlineData("application/json", "{\"password\":\"malformed-canary\"", "")]
        [InlineData("application/xml", "<root>malformed-canary", "")]
        [InlineData("application/xml", "<!DOCTYPE root [<!ENTITY secret SYSTEM 'file:///never-read'>]><root>&secret;</root>", "")]
        [InlineData("text/plain; charset=unknown-charset", "charset-canary", "")]
        [InlineData("text/plain", "encoding-canary", "Content-Encoding: unknown\r\n")]
        [InlineData("text/plain", "gzip-canary", "Content-Encoding: gzip\r\n")]
        [InlineData("text/plain", "binary\0canary", "")]
        [InlineData("", "missing-type-canary", "")]
        public void UninspectableBodies_AreOmittedInsteadOfCopied(string type, string body, string additional)
        {
            var sanitizer = new SazSanitizer();
            var result = sanitizer.SanitizeMessage(RequestHeaders(type, additional), Encoding.UTF8.GetBytes(body), true);

            Assert.Empty(result.Body);
            Assert.Equal(1, sanitizer.Summary.OmittedBodies);
            Assert.Contains("Content-Length: 0\r\n", result.Headers);
            Assert.DoesNotContain("Content-Encoding:", result.Headers);
        }

        [Fact]
        public void InvalidUtf8AndOversizedCompressedBodies_AreOmitted()
        {
            var sanitizer = new SazSanitizer();
            var invalid = sanitizer.SanitizeMessage(RequestHeaders("text/plain"),
                new byte[] { 0xEF, 0xBB, 0xBF, 0xFF }, true);
            var expanded = new byte[SazSanitizer.MaximumBodyBytes + 1];
            Array.Fill(expanded, (byte)'a');
            var large = sanitizer.SanitizeMessage(RequestHeaders("text/plain", "Content-Encoding: gzip\r\n"),
                Compress(expanded, "gzip"), true);

            Assert.Empty(invalid.Body);
            Assert.Empty(large.Body);
            Assert.Equal(2, sanitizer.Summary.OmittedBodies);
        }

        [Fact]
        public void FailedExport_PreservesExistingDestinationAndRemovesTemporaryFile()
        {
            var path = Path.Join(_directory, "existing.saz");
            File.WriteAllText(path, "original destination");
            IEnumerable<ProxyConnectionModel> BrokenRecords()
            {
                yield return Record();
                throw new IOException("simulated source failure");
            }

            Assert.Throws<IOException>(() => FiddlerSazHandler.CreateSanitizedSazFile(path, BrokenRecords()));

            Assert.Equal("original destination", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(_directory));
        }

        [Fact]
        public void Archive_OmittedBodiesAreMarkedInSessionMetadata()
        {
            var path = Path.Join(_directory, "omitted.saz");
            var record = Record("application/octet-stream");
            var summary = FiddlerSazHandler.CreateSanitizedSazFile(path, new[] { record });
            var entries = Entries(path);
            var metadata = XDocument.Parse(Encoding.UTF8.GetString(entries["raw/1_m.xml"]));
            var marker = metadata.Descendants("SessionFlag").Single(element =>
                (string)element.Attribute("N") == "x-xmat-request-body-omitted");

            Assert.Equal("uninspectable", (string)marker.Attribute("V"));
            Assert.Equal(1, summary.OmittedBodies);
            Assert.DoesNotContain("body-canary", Encoding.UTF8.GetString(entries["raw/1_c.txt"]));
            Assert.Contains("body-canary", Encoding.UTF8.GetString(record.RequestBody));
        }

        [Fact]
        public void SanitizedExport_CannotOverwriteNativeCaptureFiles()
        {
            var path = Path.Join(_directory, "capture.xmatcap");
            File.WriteAllText(path, "original capture");

            Assert.Throws<ArgumentException>(() => FiddlerSazHandler.CreateSanitizedSazFile(path, new[] { Record() }));

            Assert.Equal("original capture", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(_directory));
        }

        [Fact]
        public async Task Model_ExportsOnlySelectedLoadedCaptureAndKeepsSourceRecordsUnchanged()
        {
            var selected = new DataRecord(1, "selected");
            var other = new DataRecord(2, "other");
            var original = selected.Str(WebServiceCaptureMethod.FieldKey_RequestBody);
            Task<SanitizedExportSummary> exporting = null;
            CaptureAppModel model = null;
            var path = Path.Join(_directory, "selected.saz");
            _application.Run(window =>
            {
                var controller = new WebServiceDeviceCaptureController("selected", DeviceType.GenericProxyDevice, true);
                controller.LoadCaptureDataset(new Dataset(selected, other));
                var context = new Context(controller);
                model = new CaptureAppModel { SelectedDeviceContext = context };
                model.CaptureDeviceContexts.Add(context);
                Assert.True(model.CanExportSanitizedCaptures);
                exporting = model.ExportSanitizedDataCapturesAsync(path);
            });
            var summary = await exporting;

            Assert.Equal(1, summary.Sessions);
            Assert.Equal(3, Entries(path).Count);
            Assert.DoesNotContain("body-canary", Encoding.UTF8.GetString(Entries(path)["raw/1_c.txt"]));
            Assert.Equal(original, selected.Str(WebServiceCaptureMethod.FieldKey_RequestBody));
            _application.Run(window =>
            {
                Assert.False(model.IsCaptureDataOperationRunning);
                model.SelectedDeviceContext = null;
            });
        }

        [Fact]
        public void CommandEligibility_RejectsEmptyRunningAndBusyCapturesButAllowsReadOnlyWebCapture()
        {
            _application.Run(window =>
            {
                var model = new CaptureAppModel();
                Assert.False(model.CanExportSanitizedCaptures);
                var controller = new WebServiceDeviceCaptureController("selected", DeviceType.GenericProxyDevice, true);
                var context = new Context(controller);
                model.CaptureDeviceContexts.Add(context);
                model.SelectedDeviceContext = context;
                Assert.False(model.CanExportSanitizedCaptures);
                controller.LoadCaptureDataset(new Dataset(new DataRecord(1, "selected")));
                Assert.True(model.CanExportSanitizedCaptures);
                controller.IsRunning = true;
                Assert.False(model.CanExportSanitizedCaptures);
                controller.IsRunning = false;
                Assert.True(model.TryBeginCaptureDataOperation(out var operation));
                using (operation)
                {
                    Assert.False(model.CanExportSanitizedCaptures);
                    Assert.False(model.CanCloseAsync().GetAwaiter().GetResult());
                }
                Assert.True(model.CanExportSanitizedCaptures);
                Assert.True(model.CanCloseAsync().GetAwaiter().GetResult());
                model.SelectedDeviceContext = new Context(controller, CaptureType.NetworkTrace);
                Assert.False(model.CanExportSanitizedCaptures);
                model.SelectedDeviceContext = null;
            });
        }

        [Fact]
        public void FileMenu_HasASeparateSanitizedCommandWithoutChangingOrdinaryExport()
        {
            using var stream = typeof(SanitizedExportTests).Assembly.GetManifestResourceStream("XMAT.Tests.MainWindow.xaml");
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var menu = document.Descendants(presentation + "MenuItem")
                .Single(element => (string)element.Attribute(xaml + "Name") == "FileMenu");
            var sanitized = menu.Elements(presentation + "MenuItem")
                .Single(element => (string)element.Attribute(xaml + "Name") == "FileExportSanitizedCaptures");
            var command = (string)sanitized.Attribute("Command");
            var binding = document.Descendants(presentation + "CommandBinding")
                .Single(element => (string)element.Attribute("Command") == command);
            Assert.Equal("{x:Static local:AppCommands.ExportSanitized}", command);
            Assert.Equal("ExportSanitized_Executed", (string)binding.Attribute("Executed"));
            Assert.Equal("ExportSanitized_CanExecute", (string)binding.Attribute("CanExecute"));
            Assert.Contains(menu.Elements(presentation + "MenuItem"),
                element => (string)element.Attribute("Command") == "{x:Static local:AppCommands.Export}");
            Assert.NotSame(AppCommands.Export, AppCommands.ExportSanitized);
        }

        private static string RequestHeaders(string type, string additional = "") =>
            "POST /path HTTP/1.1\r\nHost: example.test\r\nContent-Type: " + type + "\r\n" +
            additional + "Content-Length: 999\r\n\r\n";

        private static ProxyConnectionModel Record(string requestContentType = "application/json") => new(
            1, "200", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMilliseconds(20),
            "https", "example.test:443", "POST", "/path",
            "POST https://example.test/path?access_token=query-canary&keep=ok HTTP/1.1\r\n" +
            "Host: example.test\r\nContent-Type: " + requestContentType + "\r\nAuthorization: Bearer header-canary\r\n" +
            "Cookie: session=cookie-canary\r\n\r\n",
            Encoding.UTF8.GetBytes("{\"password\":\"body-canary\",\"visible\":\"keep\"}"),
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nSet-Cookie: session=response-canary\r\n\r\n",
            Encoding.UTF8.GetBytes("{\"access_token\":\"response-body-canary\",\"count\":7}"), "192.0.2.77");

        private static Dictionary<string, byte[]> Entries(string path)
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
            {
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                return buffer.ToArray();
            });
        }

        private static byte[] Compress(byte[] bytes, string encoding)
        {
            using var output = new MemoryStream();
            using (Stream compressor = encoding switch
            {
                "gzip" => new GZipStream(output, CompressionLevel.SmallestSize, true),
                "deflate" => new ZLibStream(output, CompressionLevel.SmallestSize, true),
                _ => new BrotliStream(output, CompressionLevel.SmallestSize, true)
            })
            {
                compressor.Write(bytes);
            }
            return output.ToArray();
        }

        private sealed class DataRecord(int number, string device) : IDataRecord
        {
            private readonly ProxyConnectionModel _source = Record();
            public int RowId => number;
            public long Int(string name) => name == WebServiceCaptureMethod.FieldKey_RequestNumber ? number : 1000;
            public string Str(string name) => name switch
            {
                WebServiceCaptureMethod.FieldKey_DeviceName => device,
                WebServiceCaptureMethod.FieldKey_RequestTimestamp => ProxyConnectionModel.ToDateTimeString(_source.RequestTime),
                WebServiceCaptureMethod.FieldKey_ResponseTimestamp => ProxyConnectionModel.ToDateTimeString(_source.RequestTime + _source.Duration),
                WebServiceCaptureMethod.FieldKey_RequestScheme => _source.Scheme,
                WebServiceCaptureMethod.FieldKey_RequestHost => _source.Host,
                WebServiceCaptureMethod.FieldKey_RequestPort => _source.Port,
                WebServiceCaptureMethod.FieldKey_RequestMethod => _source.Method,
                WebServiceCaptureMethod.FieldKey_RequestPath => _source.Path,
                WebServiceCaptureMethod.FieldKey_RequestStatus => _source.Status,
                WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders => _source.RequestLineAndHeaders,
                WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders => _source.ResponseLineAndHeaders,
                WebServiceCaptureMethod.FieldKey_RequestBody => Convert.ToBase64String(_source.RequestBody),
                WebServiceCaptureMethod.FieldKey_ResponseBody => Convert.ToBase64String(_source.ResponseBody),
                WebServiceCaptureMethod.FieldKey_ClientIP => _source.ClientIP,
                _ => string.Empty
            };
        }

        private sealed class Dataset(params IDataRecord[] records) : IDataset
        {
            public IEnumerable<IFieldDefinition> FieldDefs => Array.Empty<IFieldDefinition>();
            public IEnumerable<IDataRecord> DataRecords => records;
            public IEnumerable<IDataRecord> DataRecordsFrom(long first) => records.Where(record => record.RowId >= first);
            public IEnumerable<IDataRecord> DataRecordsFrom(long first, int limit) => DataRecordsFrom(first).Take(limit);
            public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] filters) =>
                records.Where(record => filters.All(filter => record.Str(filter.Name) == (string)filter.Value));
        }

        private sealed class Context(WebServiceDeviceCaptureController controller, CaptureType type = CaptureType.WebProxy) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.GenericProxyDevice;
            public CaptureType CaptureType => type;
            public string DeviceName => controller.DeviceName;
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; } = true;
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => controller;
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
