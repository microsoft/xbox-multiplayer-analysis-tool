// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Operations.Capture;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class SanitizedExportRegressionTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public SanitizedExportRegressionTests(CaptureUiApplicationFixture application)
        {
            _application = application;
            Localization.LoadLanguage("en-us");
        }

        [Theory]
        [InlineData("{\"note\":\"\\ud800\"}")]
        [InlineData("{\"note\":\"\\udc00\"}")]
        [InlineData("{\"\\ud800\":\"value\"}")]
        public void MalformedUnicodeInJson_IsOmittedInsteadOfAbortingExport(string json)
        {
            var sanitizer = new SazSanitizer();
            var result = sanitizer.SanitizeMessage(Request("application/json"), Encoding.UTF8.GetBytes(json), true);

            Assert.Empty(result.Body);
            Assert.True(result.BodyOmitted);
            Assert.Equal(1, sanitizer.Summary.OmittedBodies);
        }

        [Theory]
        [InlineData(101, null)]
        [InlineData(103, null)]
        [InlineData(204, null)]
        [InlineData(200, "CONNECT")]
        [InlineData(201, "CONNECT")]
        public void BodylessResponses_DoNotGainForbiddenContentLength(int status, string method)
        {
            var result = new SazSanitizer().SanitizeMessage(
                $"HTTP/1.1 {status} Status\r\nContent-Length: 123\r\nX-Keep: visible\r\n\r\n",
                Array.Empty<byte>(), false, method);

            Assert.DoesNotContain("Content-Length:", result.Headers);
            Assert.Contains("X-Keep: visible", result.Headers);
            Assert.Empty(result.Body);
            Assert.False(result.BodyOmitted);
        }

        [Theory]
        [InlineData(200, "HEAD")]
        [InlineData(304, "GET")]
        public void HeadAndNotModified_KeepRepresentationLengthAndEncoding(int status, string method)
        {
            var result = new SazSanitizer().SanitizeMessage(
                $"HTTP/1.1 {status} Status\r\nContent-Length: 12345\r\nContent-Encoding: gzip\r\nETag: public-etag\r\n\r\n",
                Array.Empty<byte>(), false, method);

            Assert.Contains("Content-Length: 12345\r\n", result.Headers);
            Assert.Contains("Content-Encoding: gzip\r\n", result.Headers);
            Assert.Contains("ETag: public-etag\r\n", result.Headers);
            Assert.Empty(result.Body);
        }

        [Theory]
        [InlineData("HEAD")]
        [InlineData("CONNECT")]
        public void ArchiveWriter_UsesTheRequestMethodForResponseFraming(string method)
        {
            var path = Path.Join(Path.GetTempPath(), "XMAT-bodyless-" + Guid.NewGuid().ToString("N") + ".saz");
            try
            {
                var record = new ProxyConnectionModel(1, "200", DateTime.UtcNow, TimeSpan.Zero,
                    "https", "example.test:443", method, "/",
                    method + " " + (method == "CONNECT" ? "example.test:443" : "/") + " HTTP/1.1\r\nHost: example.test\r\n\r\n",
                    Array.Empty<byte>(),
                    "HTTP/1.1 200 OK\r\nContent-Length: 12345\r\n\r\n", Array.Empty<byte>(), "0.0.0.0");
                Assert.True(
                    SazExportTestSupport.ExportRecords(
                        path,
                        new[] { record },
                        SazExportKind.Sanitized)
                    .Succeeded);
                using var archive = ZipFile.OpenRead(path);
                using var reader = new StreamReader(archive.GetEntry("raw/1_s.txt").Open());
                var response = reader.ReadToEnd();

                if (method == "HEAD")
                {
                    Assert.Contains("Content-Length: 12345\r\n", response);
                }
                else
                {
                    Assert.DoesNotContain("Content-Length:", response);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(204)]
        [InlineData(205)]
        public void ProtocolBodylessResponses_OmitUnexpectedBodyBytes(int status)
        {
            var sanitizer = new SazSanitizer();
            var result = sanitizer.SanitizeMessage(
                $"HTTP/1.1 {status} Status\r\nContent-Type: text/plain\r\n\r\n",
                Encoding.UTF8.GetBytes("unexpected-body-canary"), false);

            Assert.Empty(result.Body);
            Assert.True(result.BodyOmitted);
            Assert.Equal(1, sanitizer.Summary.OmittedBodies);
            Assert.Equal(status == 205, result.Headers.Contains("Content-Length: 0\r\n", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HttpDeflate_InspectsStandardZlibAndLegacyRawStreams(bool raw)
        {
            using var compressed = new MemoryStream();
            using (Stream encoder = raw
                ? new DeflateStream(compressed, CompressionLevel.SmallestSize, true)
                : new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
            {
                encoder.Write(Encoding.UTF8.GetBytes("{\"password\":\"deflate-canary\",\"keep\":7}"));
            }
            var sanitizer = new SazSanitizer();
            var result = sanitizer.SanitizeMessage(
                Request("application/json", "Content-Encoding: deflate\r\n"), compressed.ToArray(), true);

            Assert.True(result.Body.Length > 0);
            Assert.DoesNotContain("deflate-canary", Encoding.UTF8.GetString(result.Body));
            Assert.Equal(7, JsonDocument.Parse(result.Body).RootElement.GetProperty("keep").GetInt32());
            Assert.Equal(0, sanitizer.Summary.OmittedBodies);
        }

        [Fact]
        public void StandardOAuthDeviceCredentials_AreRedactedWithoutChangingTheGrantType()
        {
            var result = new SazSanitizer().SanitizeMessage(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n",
                Encoding.UTF8.GetBytes("{\"device_code\":\"device-credential-canary\",\"user_code\":\"ABCD-EFGH\"," +
                    "\"verification_uri\":\"https://login.test/device\"}"), false);
            var text = Encoding.UTF8.GetString(result.Body);
            const string grant = "urn:ietf:params:oauth:grant-type:device_code";
            var encoded = Uri.EscapeDataString(grant);
            var form = new SazSanitizer().SanitizeMessage(
                Request("application/x-www-form-urlencoded"),
                Encoding.UTF8.GetBytes("grant_type=" + encoded + "&device_code=device-credential-canary&scope=openid"), true);

            Assert.DoesNotContain("device-credential-canary", text + Encoding.UTF8.GetString(form.Body));
            Assert.DoesNotContain("ABCD-EFGH", text);
            Assert.Contains("login.test/device", text);
            Assert.Contains(encoded, Encoding.UTF8.GetString(form.Body));
            Assert.Contains("scope=openid", Encoding.UTF8.GetString(form.Body));
        }

        [Theory]
        [InlineData("k")]
        [InlineData("d")]
        [InlineData("p")]
        [InlineData("q")]
        [InlineData("dp")]
        [InlineData("dq")]
        [InlineData("qi")]
        [InlineData("oth")]
        public void PrivateJwkMembers_AreRedactedButPublicParametersRemain(string member)
        {
            var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray())
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["kty"] = member == "k" ? "oct" : "RSA",
                [member] = key,
                ["n"] = "public-key-material",
                ["e"] = "AQAB"
            });
            var result = new SazSanitizer().SanitizeMessage(
                Request("application/jwk+json"), Encoding.UTF8.GetBytes(body), true);
            using var document = JsonDocument.Parse(result.Body);

            Assert.DoesNotContain(key, Encoding.UTF8.GetString(result.Body));
            Assert.Equal(SazSanitizer.Redacted, document.RootElement.GetProperty(member).GetString());
            Assert.Equal("public-key-material", document.RootElement.GetProperty("n").GetString());
            Assert.Equal("AQAB", document.RootElement.GetProperty("e").GetString());
        }

        [Fact]
        public void NonKeyObjects_DoNotLoseOrdinarySingleLetterFields()
        {
            var result = new SazSanitizer().SanitizeMessage(
                Request("application/json"), Encoding.UTF8.GetBytes("{\"d\":\"diagnostic\",\"k\":17}"), true);
            using var document = JsonDocument.Parse(result.Body);

            Assert.Equal("diagnostic", document.RootElement.GetProperty("d").GetString());
            Assert.Equal(17, document.RootElement.GetProperty("k").GetInt32());
        }

        [Theory]
        [InlineData("<c><password value=\"attribute-canary\"/></c>", "attribute-canary")]
        [InlineData("<c><Token Value=\"attribute-canary\"/></c>", "attribute-canary")]
        [InlineData("<c><Credentials login=\"login-canary\" pass=\"attribute-canary\"/></c>", "attribute-canary")]
        [InlineData("<c><KeyValueOfstringstring><Key>Password</Key><Value>child-canary</Value></KeyValueOfstringstring></c>", "child-canary")]
        [InlineData("<c><item><Name>ApiKey</Name><Value>child-canary</Value></item></c>", "child-canary")]
        [InlineData("<c><item><Key api_key=\"attribute-canary\">Password</Key><Value>child-canary</Value></item></c>", "attribute-canary")]
        [InlineData("<c><item name=\"Password\" data=\"attribute-canary\"/></c>", "attribute-canary")]
        [InlineData("<c><item name=\"ApiKey\"><blob>child-canary</blob></item></c>", "child-canary")]
        [InlineData("<c><item><Key>Password</Key><Value><nested>child-canary</nested></Value></item></c>", "child-canary")]
        public void RecognizedXmlCredentials_AreRemovedFromAttributesAndChildPairs(string body, string canary)
        {
            var result = new SazSanitizer().SanitizeMessage(Request("application/xml"), Encoding.UTF8.GetBytes(body), true);

            Assert.DoesNotContain(canary, Encoding.UTF8.GetString(result.Body));
            Assert.True(result.Body.Length > 0);
        }

        [Theory]
        [InlineData(65)]
        [InlineData(256)]
        public void DeepXml_IsOmittedBeforeRecursiveTraversal(int depth)
        {
            var xml = string.Concat(Enumerable.Repeat("<n>", depth)) + "visible" +
                string.Concat(Enumerable.Repeat("</n>", depth));
            var sanitizer = new SazSanitizer();
            var result = sanitizer.SanitizeMessage(Request("application/xml"), Encoding.UTF8.GetBytes(xml), true);

            Assert.Empty(result.Body);
            Assert.True(result.BodyOmitted);
            Assert.Equal(1, sanitizer.Summary.OmittedBodies);
        }

        [Fact]
        public void NormalXmlDepth_IsInspectedRatherThanDropped()
        {
            var xml = string.Concat(Enumerable.Repeat("<n>", 16)) + "<password>depth-canary</password><keep>visible</keep>" +
                string.Concat(Enumerable.Repeat("</n>", 16));
            var result = new SazSanitizer().SanitizeMessage(Request("application/xml"), Encoding.UTF8.GetBytes(xml), true);

            Assert.DoesNotContain("depth-canary", Encoding.UTF8.GetString(result.Body));
            Assert.Contains("<keep>visible</keep>", Encoding.UTF8.GetString(result.Body));
            Assert.False(result.BodyOmitted);
        }

        [Theory]
        [InlineData("https://profile.xboxlive.com/users/gt(GamerCanary)/profile/settings", "GamerCanary")]
        [InlineData("https://privacy.xboxlive.com/users/xuid(1)/permission/validate?setting=CommunicateUsingVoice&target=xuid(2533274900000002)", "2533274900000002")]
        [InlineData("https://privacy.xboxlive.com/users/xuid(1)/permission/validate?target=xuid%282533274900000002%29", "2533274900000002")]
        [InlineData("https://login.test/common/userrealm/dana.canary@example.test?api-version=1.0", "dana.canary@example.test")]
        public void RecognizedIdentifiers_AreRemovedFromUriPathsAndQueryValues(string uri, string canary)
        {
            var result = new SazSanitizer().SanitizeMessage(
                "GET " + uri + " HTTP/1.1\r\nReferer: " + uri + "\r\nLocation: " + uri + "\r\n\r\n",
                Array.Empty<byte>(), true);

            Assert.DoesNotContain(canary, result.Headers);
        }

        [Theory]
        [InlineData("Gamertag")]
        [InlineData("GameDisplayName")]
        public void XboxProfilePairsAndDisplayClaims_AreRedacted(string id)
        {
            var result = new SazSanitizer().SanitizeMessage(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n",
                Encoding.UTF8.GetBytes("{\"id\":\"" + id + "\",\"value\":\"GamerCanary\"," +
                    "\"DisplayClaims\":{\"xui\":[{\"gtg\":\"ClaimCanary\",\"xid\":\"2533274900000002\"}]}}"), false);
            var text = Encoding.UTF8.GetString(result.Body);

            Assert.DoesNotContain("GamerCanary", text);
            Assert.DoesNotContain("ClaimCanary", text);
            Assert.DoesNotContain("2533274900000002", text);
        }

        [Fact]
        public void RelativeOAuthRequest_KeepsTheOriginalRedirectHost()
        {
            var result = new SazSanitizer().SanitizeMessage(
                "GET /oauth2/authorize?client_id=abc&redirect_uri=https://app.test&login_hint=alice@contoso.test&scope=openid HTTP/1.1\r\n\r\n",
                Array.Empty<byte>(), true);

            Assert.Contains("redirect_uri=https://app.test", result.Headers);
            Assert.Contains("scope=openid", result.Headers);
            Assert.DoesNotContain("alice@contoso.test", result.Headers);
        }

        [Theory]
        [InlineData("https://app.test/users/xuid(123456)?access_token=uri-canary&keep=ok")]
        [InlineData("/oauth2/authorize?redirect_uri=https://app.test&login_hint=alice@contoso.test&scope=openid")]
        public void JsonUrlValues_AreSanitizedOnceAndRemainValidUris(string uri)
        {
            var result = new SazSanitizer().SanitizeMessage(Request("application/json"),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { endpoint = uri })), true);
            var sanitized = JsonDocument.Parse(result.Body).RootElement.GetProperty("endpoint").GetString();

            Assert.DoesNotContain("uri-canary", sanitized);
            Assert.DoesNotContain("alice@contoso.test", sanitized);
            Assert.DoesNotContain("123456", sanitized);
            Assert.DoesNotContain("\"", sanitized);
            Assert.DoesNotContain("[", sanitized);
            Assert.Contains("app.test", sanitized);
            Assert.True(Uri.TryCreate(sanitized, UriKind.RelativeOrAbsolute, out _));
        }

        [Theory]
        [InlineData("Endpoint=sb://ns.test/;SharedAccessKeyName=Rule;SharedAccessKey=sbkey-canary=", "sbkey-canary")]
        [InlineData("AccountEndpoint=https://cosmos.test/;AccountKey=cosmos-canary=", "cosmos-canary")]
        [InlineData("Endpoint=https://signalr.test;AccessKey=signalr-canary", "signalr-canary")]
        [InlineData("Endpoint=https://config.test;Id=public;Secret=config-canary", "config-canary")]
        [InlineData("url=https://h.test/x,password=comma-canary", "comma-canary")]
        [InlineData("callback=https://h.test/x&token=amp-canary", "amp-canary")]
        [InlineData("https://h.test/app;jsessionid=jsess-canary", "jsess-canary")]
        [InlineData("AccountKey=abcd//efgh-canary==", "efgh-canary")]
        [InlineData("secret=ab12//cd34-canary", "cd34-canary")]
        [InlineData("Basic dXNlcjpw//YXNz-canary", "YXNz-canary")]
        [InlineData("Cookie: returnUrl=https://site.test/x; session=sess-canary", "sess-canary")]
        [InlineData("Authorization: Digest username=\"user\", uri=\"https://h.test/x\", response=\"digest-canary\"", "digest-canary")]
        [InlineData("/p:Password=switch-canary", "switch-canary")]
        [InlineData("/password:pw-canary", "pw-canary")]
        [InlineData("Endpoint=https://h.test;Secret=first-canary@tail-canary", "tail-canary")]
        public void CredentialsAdjacentToUrls_AreRedactedAcrossAllSupportedTextLocations(string value, string canary)
        {
            var json = new SazSanitizer().SanitizeMessage(Request("application/json"),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { message = value, keep = "visible" })), true);
            var xml = new SazSanitizer().SanitizeMessage(Request("application/xml"),
                Encoding.UTF8.GetBytes(new XElement("root", new XElement("message", value),
                    new XElement("keep", "visible")).ToString(SaveOptions.DisableFormatting)), true);
            var form = new SazSanitizer().SanitizeMessage(Request("application/x-www-form-urlencoded"),
                Encoding.UTF8.GetBytes("message=" + Uri.EscapeDataString(value) + "&keep=visible"), true);
            var plain = new SazSanitizer().SanitizeMessage(Request("text/plain"),
                Encoding.UTF8.GetBytes(value + "\nkeep: visible"), true);
            var header = new SazSanitizer().SanitizeMessage(
                "GET / HTTP/1.1\r\nX-Diagnostic: " + value + "\r\nX-Keep: visible\r\n\r\n",
                Array.Empty<byte>(), true);

            foreach (var message in new[] { json, xml, form, plain })
            {
                Assert.False(message.BodyOmitted);
                Assert.DoesNotContain(canary, Encoding.UTF8.GetString(message.Body));
                Assert.Contains("visible", Encoding.UTF8.GetString(message.Body));
            }
            Assert.DoesNotContain(canary, header.Headers);
            Assert.Contains("X-Keep: visible", header.Headers);
        }

        [Theory]
        [InlineData("Check https://site.test/path?access_token=uri-canary&keep=ok now")]
        [InlineData("Check https://username:password-canary@site.test/path?keep=ok now")]
        public void EmbeddedUrls_KeepTheirEndpointAndValidEscaping(string text)
        {
            var result = new SazSanitizer().SanitizeMessage(Request("application/json"),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { message = text })), true);
            var sanitized = JsonDocument.Parse(result.Body).RootElement.GetProperty("message").GetString();
            var uri = sanitized.Substring("Check ".Length, sanitized.Length - "Check ".Length - " now".Length);

            Assert.DoesNotContain("canary", uri);
            Assert.Contains("site.test/path", uri);
            Assert.DoesNotContain("\"", uri);
            Assert.True(Uri.TryCreate(uri, UriKind.Absolute, out _));
        }

        [Theory]
        [InlineData("https://x.service.signalr.net;AccessKey=authority-canary;Version=1.0;")]
        [InlineData("rediss://cache.test:6380,password=authority-canary,ssl=True")]
        [InlineData("sqlserver://db.test:1433;user=sa;password=authority-canary;")]
        [InlineData("https://h.test,password=authority-canary")]
        [InlineData("https://h.test;Secret=first-part@authority-canary")]
        public void WholeUriAuthorityAssignments_DoNotBypassCredentialRedaction(string uri)
        {
            var result = new SazSanitizer().SanitizeMessage(Request("application/json"),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { value = uri, keep = "visible" })), true);

            Assert.False(result.BodyOmitted);
            Assert.DoesNotContain("authority-canary", Encoding.UTF8.GetString(result.Body));
            Assert.Contains("visible", Encoding.UTF8.GetString(result.Body));
        }

        [Theory]
        [InlineData("secretsmanager.us-east-1.amazonaws.com:443")]
        [InlineData("token-service.example.test:8443")]
        [InlineData("[::1]:443")]
        public void ConnectAuthorityHosts_AreNotInterpretedAsSecretAssignments(string target)
        {
            var firstLine = "CONNECT " + target + " HTTP/1.1";
            var result = new SazSanitizer().SanitizeMessage(firstLine + "\r\n\r\n", Array.Empty<byte>(), true);

            Assert.StartsWith(firstLine + "\r\n", result.Headers);
            Assert.DoesNotContain("REDACTED", result.Headers);
        }

        [Theory]
        [InlineData(600)]
        [InlineData(999)]
        public void CapturedNonstandardStatusCodes_DoNotAbortTheExport(int status)
        {
            var result = new SazSanitizer().SanitizeMessage(
                $"HTTP/1.1 {status} Captured status\r\nContent-Type: text/plain\r\n\r\n",
                Encoding.UTF8.GetBytes("keep: visible"), false);

            Assert.StartsWith($"HTTP/1.1 {status} ", result.Headers);
            Assert.Contains("visible", Encoding.UTF8.GetString(result.Body));
            Assert.False(result.BodyOmitted);
        }

        [Fact]
        public void XmlSpaceOnASensitiveElement_RemainsValidAndDoesNotOmitUnrelatedContent()
        {
            var result = new SazSanitizer().SanitizeMessage(Request("application/xml"),
                Encoding.UTF8.GetBytes("<root><password xml:space=\"preserve\">space-canary</password>" +
                    "<keep>visible</keep></root>"), true);

            Assert.False(result.BodyOmitted);
            Assert.DoesNotContain("space-canary", Encoding.UTF8.GetString(result.Body));
            var document = XDocument.Parse(Encoding.UTF8.GetString(result.Body));
            Assert.Equal("preserve", (string)document.Root.Element("password").Attribute(XNamespace.Xml + "space"));
            Assert.Equal("visible", document.Root.Element("keep").Value);
        }

        [Theory]
        [InlineData("extension")]
        [InlineData("directory")]
        public async Task EarlyDestinationFailure_DoesNotLeaveAReaderOrDatabaseLock(string failureMode)
        {
            var directory = Path.Join(Path.GetTempPath(), "XMAT-export-reader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Exception bodyFailure = null;
            try
            {
                using SqLtDatabase database =
                    CaptureDatabase(
                        out IDataTable table);
                CaptureAppModel model = null;
                string destination = failureMode == "extension"
                    ? Path.Join(directory, "wrong.xmatcap")
                    : Path.Join(directory, "missing", "output.saz");
                _application.Run(window =>
                {
                    var controller = new WebServiceDeviceCaptureController("selected", DeviceType.GenericProxyDevice, true);
                    controller.LoadCaptureDataset(table.Dataset);
                    var context = new Context(controller);
                    model = new CaptureAppModel { SelectedDeviceContext = context };
                    model.CaptureDeviceContexts.Add(context);
                });
                CaptureExportOutcome outcome =
                    await CaptureExportDriver.RunAsync(
                        _application,
                        model,
                        SazExportKind.Sanitized,
                        new CaptureExportScript
                        {
                            Destination = destination,
                        });
                string expectedCode =
                    failureMode == "extension"
                        ? SazExportCodes.SazRequired
                        : SazExportCodes.UnsupportedLocation;
                string expectedText =
                    failureMode == "extension"
                        ? "EXPORT_SAZ_REQUIRED"
                        : "EXPORT_UNSUPPORTED";
                Assert.Null(outcome.Result);
                Assert.Equal(expectedCode, outcome.Code);
                Assert.Contains(expectedText, outcome.Shown);
                AddRow(table, 2);
                _application.Run(window =>
                {
                    Assert.False(model.IsCaptureDataOperationRunning);
                    model.SelectedDeviceContext = null;
                });
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    TestFolderCleanup.Delete(directory);
                }
                catch (Exception cleanup) when (
                    bodyFailure != null &&
                    (cleanup is IOException or
                     UnauthorizedAccessException))
                {
                }
            }
        }

        [Fact]
        public async Task Cancellation_KeepsTheOperationLeaseUntilReaderAndWorkerCleanupFinish()
        {
            var directory = Path.Join(Path.GetTempPath(), "XMAT-export-cancel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Exception bodyFailure = null;
            try
            {
                using SqLtDatabase database =
                    CaptureDatabase(
                        out IDataTable table);
                using var releaseCleanup = new ManualResetEventSlim();
                var destination = Path.Join(directory, "existing.saz");
                File.WriteAllBytes(
                    destination,
                    "existing archive"u8.ToArray());
                var original = File.ReadAllBytes(destination);
                CaptureAppModel model = null;
                _application.Run(window =>
                {
                    var controller = new WebServiceDeviceCaptureController("selected", DeviceType.GenericProxyDevice, true);
                    controller.LoadCaptureDataset(table.Dataset);
                    var context = new Context(controller);
                    model = new CaptureAppModel { SelectedDeviceContext = context };
                    model.CaptureDeviceContexts.Add(context);
                });
                var script = new CaptureExportScript
                {
                    Destination = destination,
                    Replace = true,
                };
                var dialogs =
                    new ScriptedCaptureExportDialogs(
                        script);
                BlockingExportSource blocked = null;
                CaptureExportWorkflow workflow =
                    CaptureExportDriver.Workflow(
                        dialogs);
                workflow.SourceFrozen = source =>
                    blocked =
                        new BlockingExportSource(
                            source,
                            releaseCleanup);
                Task<bool> closing = null;
                dialogs.DuringStep = async progress =>
                {
                    if (progress.Step !=
                        CaptureExportStep.Writing)
                    {
                        return;
                    }

                    await blocked.ReadingStarted.Task
                        .WaitAsync(
                            TimeSpan.FromSeconds(10));
                    Assert.True(
                        progress.RequestCancellation());
                    await blocked.CleanupStarted.Task
                        .WaitAsync(
                            TimeSpan.FromSeconds(10));
                    try
                    {
                        Assert.True(
                            model.IsCaptureDataOperationRunning);
                        Assert.False(
                            model.CanExportSanitizedCaptures);
                        Assert.False(
                            model.CanModifyCaptureData);
                        closing =
                            model.CanCloseAsync();
                        Assert.False(
                            closing.IsCompleted);
                        Assert.False(
                            model.TryBeginCaptureDataOperation(
                                out _));
                        Assert.Equal(
                            original,
                            File.ReadAllBytes(destination));
                    }
                    finally
                    {
                        releaseCleanup.Set();
                    }
                };
                CaptureExportOutcome outcome = null;
                try
                {
                    await _application.RunAsync(
                        async () =>
                        {
                            outcome =
                                await workflow.RunAsync(
                                    model,
                                    SazExportKind.Sanitized,
                                    owner: null);
                            Assert.NotNull(closing);
                            Assert.True(
                                await closing.WaitAsync(
                                    TimeSpan.FromSeconds(10)));
                            Assert.False(
                                model.IsCaptureDataOperationRunning);
                        });
                }
                finally
                {
                    releaseCleanup.Set();
                }

                Assert.Equal(
                    SazExportCodes.Cancelled,
                    outcome.Code);
                Assert.DoesNotContain(
                    "EXPORT_CANCELLED",
                    outcome.Shown);
                Assert.Equal(original, File.ReadAllBytes(destination));
                Assert.Empty(
                    Directory.GetFiles(
                        directory,
                        ".xmat-*.tmp"));
                await _application.RunAsync(
                    async () =>
                    {
                        Assert.False(
                            model.IsCaptureDataOperationRunning);
                        Assert.True(
                            model.CanExportSanitizedCaptures);
                        model.SelectedDeviceContext = null;
                    });
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    TestFolderCleanup.Delete(directory);
                }
                catch (Exception cleanup) when (
                    bodyFailure != null &&
                    (cleanup is IOException or
                     UnauthorizedAccessException))
                {
                }
            }
        }

        private static SqLtDatabase CaptureDatabase(
            out IDataTable table)
        {
            var database = new SqLtDatabase();
            database.Initialize();
            table = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                new Field<long>(WebServiceCaptureMethod.FieldKey_RequestNumber),
                new Field<long>(WebServiceCaptureMethod.FieldKey_ConnectionId),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestScheme),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestHost),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPort),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPath),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestStatus),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP));
            AddRow(table, 1);
            return database;
        }

        private static void AddRow(
            IDataTable table,
            long number)
        {
            var record = new DataRecord();
            table.AddRow(
                "selected",
                number,
                number,
                record.Str(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                record.Str(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                record.Str(WebServiceCaptureMethod.FieldKey_RequestScheme),
                record.Str(WebServiceCaptureMethod.FieldKey_RequestHost),
                record.Str(WebServiceCaptureMethod.FieldKey_RequestPort),
                record.Str(WebServiceCaptureMethod.FieldKey_RequestMethod),
                record.Str(WebServiceCaptureMethod.FieldKey_RequestPath),
                record.Str(WebServiceCaptureMethod.FieldKey_RequestStatus),
                record.Str(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                string.Empty,
                record.Str(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                string.Empty,
                "0.0.0.0");
        }

        private sealed class BlockingExportSource :
            SazExportSource
        {
            private readonly SazExportSource _inner;
            private readonly ManualResetEventSlim _release;

            internal BlockingExportSource(
                SazExportSource inner,
                ManualResetEventSlim release)
                : base(inner.TextSource)
            {
                _inner = inner;
                _release = release;
            }

            internal TaskCompletionSource ReadingStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource CleanupStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal override bool IsUnsupported =>
                _inner.IsUnsupported;

            internal override long? GetWatermark(
                CancellationToken cancellationToken) =>
                _inner.GetWatermark(
                    cancellationToken);

            internal override SazExportPage ReadPage(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                CancellationToken cancellationToken) =>
                _inner.ReadPage(
                    afterRowId,
                    throughRowId,
                    scan,
                    budgets,
                    cancellationToken);

            internal override SazExportPage ReadPageForKind(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                SazExportKind kind,
                CancellationToken cancellationToken) =>
                _inner.ReadPageForKind(
                    afterRowId,
                    throughRowId,
                    scan,
                    budgets,
                    kind,
                    cancellationToken);

            internal override StoredRow ReadRow(
                SazExportKey key,
                SazExportBudgets budgets,
                CancellationToken cancellationToken) =>
                ReadRowForKind(
                    key,
                    budgets,
                    SazExportKind.Plain,
                    cancellationToken);

            internal override StoredRow ReadRowForKind(
                SazExportKey key,
                SazExportBudgets budgets,
                SazExportKind kind,
                CancellationToken cancellationToken)
            {
                ReadingStarted.TrySetResult();
                if (!cancellationToken.WaitHandle.WaitOne(
                        TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException(
                        "The test did not cancel the export read.");
                }

                CleanupStarted.TrySetResult();
                if (!_release.Wait(
                        TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException(
                        "The test did not release export cleanup.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                return _inner.ReadRowForKind(
                    key,
                    budgets,
                    kind,
                    cancellationToken);
            }
        }

        [Theory]
        [InlineData("timeout")]
        [InlineData("cancellation")]
        [InlineData("malformed-http")]
        public void RenderingFailureClassification_ContainsOnlySanitizerResourceFailures(string failure)
        {
            using var cancellation = new CancellationTokenSource();
            var sanitizer = new SazSanitizer(cancellation.Token);
            var headers = Request("text/plain", "X-Trace: " + new string('a', 8192) + "!\r\n");
            var inspect = typeof(SazSanitizer).GetMethod("InspectMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            var render = typeof(SazSanitizer).GetMethod("RenderMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            var inspection = inspect.Invoke(sanitizer, new object[] { headers, Array.Empty<byte>(), true, "POST" });
            typeof(SazSanitizer).GetField("_knownSecretPattern", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(sanitizer, new Regex("(a+)+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(1)));
            if (failure == "cancellation")
            {
                cancellation.Cancel();
            }
            if (failure == "timeout")
            {
                var result = (SanitizedHttpMessage)render.Invoke(sanitizer,
                    new object[] { headers, Array.Empty<byte>(), true, "POST", inspection });
                Assert.Null(result.Headers);
                Assert.True((bool)typeof(SazSanitizer).GetProperty("OpaqueSession",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sanitizer));
            }
            else
            {
                var exception = Assert.Throws<TargetInvocationException>(() => render.Invoke(sanitizer,
                    new object[]
                    {
                        failure == "malformed-http" ? "malformed\r\n\r\n" : headers,
                        Array.Empty<byte>(), true, "POST", inspection
                    }));
                if (failure == "cancellation")
                {
                    Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
                }
                else
                {
                    var invalid =
                        Assert.IsType<SazSanitizeException>(
                            exception.InnerException);
                    Assert.Equal(
                        SazSanitizeException.InvalidHttpCode,
                        invalid.Code);
                }
                Assert.False((bool)typeof(SazSanitizer).GetProperty("OpaqueSession",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sanitizer));
            }
        }

        private static string Request(string type, string extra = "") =>
            "POST / HTTP/1.1\r\nContent-Type: " + type + "\r\n" + extra + "\r\n";

        private sealed class ReaderDataset :
            IDataset,
            IDisposable,
            ICaptureTextSourceProvider
        {
            private readonly SqliteConnection _connection;
            private readonly List<SqliteDataReader> _readers = new();

            public CaptureTextSource CaptureTextSource =>
                global::XMAT.DataAbstractionLayer.CaptureTextSource.Literal;
            private readonly List<SqliteCommand> _commands = new();
            internal int OpenReaders { get; private set; }
            internal string ConnectionString { get; }
            internal Action BeforeRead { get; set; }
            internal Action BeforeDispose { get; set; }

            internal ReaderDataset(string path)
            {
                ConnectionString = $"Data Source={path};Pooling=False;Default Timeout=1";
                _connection = new SqliteConnection(ConnectionString);
                _connection.Open();
                using var command = _connection.CreateCommand();
                command.CommandText = "CREATE TABLE SourceRows (id INTEGER); INSERT INTO SourceRows VALUES (1);";
                command.ExecuteNonQuery();
            }

            public IEnumerable<IFieldDefinition> FieldDefs => Array.Empty<IFieldDefinition>();
            public IEnumerable<IDataRecord> DataRecords => DataRecordsWhen();
            public IEnumerable<IDataRecord> DataRecordsFrom(long first) => DataRecords;
            public IEnumerable<IDataRecord> DataRecordsFrom(long first, int limit) => DataRecords.Take(limit);
            public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] values)
            {
                var command = _connection.CreateCommand();
                command.CommandText = "SELECT id FROM SourceRows";
                var reader = command.ExecuteReader();
                _readers.Add(reader);
                _commands.Add(command);
                OpenReaders++;
                return Enumerate(reader, command);
            }

            private IEnumerable<IDataRecord> Enumerate(SqliteDataReader reader, SqliteCommand command)
            {
                try
                {
                    BeforeRead?.Invoke();
                    while (reader.Read())
                    {
                        yield return new DataRecord();
                    }
                }
                finally
                {
                    try
                    {
                        BeforeDispose?.Invoke();
                    }
                    finally
                    {
                        reader.Dispose();
                        command.Dispose();
                        OpenReaders--;
                    }
                }
            }

            public void Dispose()
            {
                foreach (var reader in _readers)
                {
                    reader.Dispose();
                }
                foreach (var command in _commands)
                {
                    command.Dispose();
                }
                _connection.Dispose();
            }
        }

        private sealed class DataRecord : IDataRecord
        {
            public int RowId => 1;
            public long Int(string name) => 1;
            public string Str(string name) => name switch
            {
                WebServiceCaptureMethod.FieldKey_DeviceName => "selected",
                WebServiceCaptureMethod.FieldKey_RequestTimestamp => "2026-01-01T00:00:00.0000000Z",
                WebServiceCaptureMethod.FieldKey_ResponseTimestamp => "2026-01-01T00:00:00.0100000Z",
                WebServiceCaptureMethod.FieldKey_RequestScheme => "https",
                WebServiceCaptureMethod.FieldKey_RequestHost => "example.test",
                WebServiceCaptureMethod.FieldKey_RequestPort => "443",
                WebServiceCaptureMethod.FieldKey_RequestMethod => "GET",
                WebServiceCaptureMethod.FieldKey_RequestPath => "/",
                WebServiceCaptureMethod.FieldKey_RequestStatus => "200",
                WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders => "GET / HTTP/1.1\r\nHost: example.test\r\n\r\n",
                WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders => "HTTP/1.1 200 OK\r\n\r\n",
                _ => string.Empty
            };
        }

        private sealed class Context(WebServiceDeviceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.GenericProxyDevice;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => controller.DeviceName;
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; } = true;
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => controller;
        }
    }
}
