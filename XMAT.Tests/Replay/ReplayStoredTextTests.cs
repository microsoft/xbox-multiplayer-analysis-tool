// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // Q's parity gates (QP): an XMAT capture without the text-format marker is sent or exported only as stored, once the
    // run accepts that for its capture file, and refused before anything is selected, drafted or sent otherwise; the
    // source of a capture's text travels with its records, models and drafts; an accepted send is recorded as Literal;
    // and a marked capture's quotes reach the wire exactly. Every request targets a loopback server.
    public sealed class ReplayStoredTextTests : IDisposable
    {
        private const string StoredHeaders = "If-None-Match: \"v1\"\r\nX-Adjacent: \"\"\r\n";

        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory, "HeadlessTestArtifacts", "ReplayStoredText", Guid.NewGuid().ToString("N"));

        public void Dispose() => TestFolderCleanup.Delete(_directory);

        [Theory]
        [InlineData("marked", "literal")]
        [InlineData("unmarked", "ambiguousUnmarked")]
        [InlineData("saz", "notApplicable")]
        public void ReadCapture_ClassifiesTheInput_AndEveryRecordModelAndDraftKeepsItsSource(string kind, string semantics)
        {
            string capture = Write(kind, new CapturedExchange("GET", "http://127.0.0.1:9/one", StoredHeaders));

            OperationResult<CaptureRecordSet> read = new AnalyzeCaptureOperation().ReadCapture(capture, null, CancellationToken.None);

            Assert.True(read.Succeeded);
            CaptureTextSource source = read.Value.TextSource;
            Assert.Equal(
                kind == "unmarked" ? CaptureTextSemantics.AmbiguousUnmarked : CaptureTextSemantics.Literal,
                source.Semantics);
            Assert.Equal(new StoredTextSummary(semantics, "notNeeded"), read.Value.StoredText);
            if (kind == "saz")
            {
                Assert.Same(CaptureTextSource.Literal, source);
            }
            else
            {
                Assert.NotNull(source.Identity);
            }

            IDataRecord record = Assert.Single(Assert.Single(read.Value.Devices).Records);
            var model = new ProxyConnectionModel(record);
            RequestReplayDraft draft = RequestReplayDraft.FromCapture(model);
            Assert.Same(source, model.CaptureTextSource);
            Assert.Same(source, new ProxyConnectionModel(model).CaptureTextSource);
            Assert.Same(source, draft.CaptureTextSource);
            Assert.Same(source, draft.WithUrl("http://127.0.0.1:9/two").CaptureTextSource);
            Assert.Same(source, draft.WithCapturedOrigin(new Uri("http://127.0.0.1:9/")).CaptureTextSource);
            Assert.Same(CaptureTextSource.Literal, new RequestReplayDraft().CaptureTextSource);
        }

        // Refused before the selection is checked, so before any draft exists, and before anything is sent; an edit of the
        // URL leaves the stored text in the request, so it needs the decision too.
        [Fact]
        public async Task UnmarkedCapture_WithoutAcceptance_IsRefusedBeforeSelectionDraftsAndSends()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var other = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = Write("unmarked", new CapturedExchange("GET", server.Origin + "/one", StoredHeaders));
            string sha256 = Sha256(capture);

            foreach (ReplayRequest request in new[]
            {
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1"))),
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("99"))),
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1")), Edits: new ReplayEdits(Url: other.Origin + "/edited")),
            })
            {
                OperationResult<ReplayResult> sent = await Execute(request);
                AssertRefused(sent.Outcome, sent.Diagnostics, ReplayOperation.StoredTextAmbiguousCode, ReplayMessage);

                OperationResult<ReplayExport> exported = new ReplayOperation().PrepareForExport(request, CancellationToken.None);
                AssertRefused(exported.Outcome, exported.Diagnostics, ReplayOperation.StoredTextAmbiguousExportCode, ExportMessage);
            }

            Assert.Equal((0, 0), (server.Connections, other.Connections));
            Assert.Equal(sha256, Sha256(capture));
        }

        [Theory]
        [InlineData("%0")]
        [InlineData("%Ethernet")]
        [InlineData("%25NoSuchIf")]
        public async Task StoredTextDecision_PrecedesScopedDestination(string zone)
        {
            string host = "fe80::1" + zone;
            string url = $"http://[{host}]:8080/";
            string capture = Write(
                "unmarked",
                new CapturedExchange("GET", "http://[fe80::1]:8080/", StoredHeaders));
            SetStoredDestination(capture, "http", host, "8080", "/");
            ProxyConnectionModel stored = Model(capture);
            Assert.Equal(
                ("http", host, "8080", "/"),
                (stored.Scheme, stored.Host, stored.Port, stored.Path));
            RequestReplayDraft draft = RequestReplayDraft.FromCapture(stored);
            Assert.Equal(url, draft.Url);
            Assert.True(ReplayDestination.IsScopedIpv6(draft.Url));

            var operation = new ReplayOperation();
            var fileSource = ReplaySource.FromFile(capture, Selection("1"));
            ReplayOperation.PreparedRun sent = await operation.PrepareRunAsync(
                new ReplayRequest(fileSource),
                CancellationToken.None);
            OperationResult<ReplayExport> exported = operation.PrepareForExport(
                new ReplayRequest(fileSource),
                CancellationToken.None);

            AssertRefused(
                sent.Failure.Outcome,
                sent.Failure.Diagnostics,
                ReplayOperation.StoredTextAmbiguousCode,
                ReplayMessage);
            AssertRefused(
                exported.Outcome,
                exported.Diagnostics,
                ReplayOperation.StoredTextAmbiguousExportCode,
                ExportMessage);

            ReplayOperation.PreparedRun accepted = await operation.PrepareRunAsync(
                new ReplayRequest(fileSource, new ReplayOptions(AcceptStoredText: true)),
                CancellationToken.None);
            Assert.Equal(OperationOutcome.InvalidInput, accepted.Failure.Outcome);
            Assert.Equal(ReplayDestination.ScopedIpv6Code, Assert.Single(accepted.Failure.Diagnostics).Code);

            var heldSource = ReplaySource.FromCaptures(new[] { stored });
            ReplayOperation.PreparedRun heldSend = await operation.PrepareRunAsync(
                new ReplayRequest(heldSource, new ReplayOptions(AcceptStoredText: true)),
                CancellationToken.None);
            OperationResult<ReplayExport> heldExport = operation.PrepareForExport(
                new ReplayRequest(heldSource, new ReplayOptions(AcceptStoredText: true)),
                CancellationToken.None);
            AssertRefused(
                heldSend.Failure.Outcome,
                heldSend.Failure.Diagnostics,
                ReplayOperation.StoredTextAmbiguousCode,
                ReplayMessage);
            AssertRefused(
                heldExport.Outcome,
                heldExport.Diagnostics,
                ReplayOperation.StoredTextAmbiguousExportCode,
                ExportMessage);
        }

        // The stored-text decision comes before a malformed destination too, the decision's limit included: the request is
        // refused for its text until the run accepts that for its capture file, and then refused as malformed. A held row of
        // the capture is refused for its text even so. Nothing is sent.
        [Fact]
        public async Task StoredTextDecision_PrecedesAMalformedDestination()
        {
            string capture = Write("unmarked", new CapturedExchange("GET", "http://127.0.0.1:9/one", StoredHeaders));
            SetStoredDestination(
                capture, "http", "127.0.0.1", "9", new string(' ', ReplayDestination.MaxCaptureDestinationChars) + "/one");
            ProxyConnectionModel held = Model(capture);
            Assert.Equal(CaptureDestination.OverLimit, ReplayDestination.ReadCaptureDestination(held));
            var fileSource = ReplaySource.FromFile(capture, Selection("1"));
            var accepted = new ReplayOptions(AcceptStoredText: true);

            OperationResult<ReplayResult> refused = await Execute(new ReplayRequest(fileSource));
            OperationResult<ReplayResult> malformed = await Execute(new ReplayRequest(fileSource, accepted));
            OperationResult<ReplayResult> heldRefused = await Execute(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { held }), accepted));

            AssertRefused(refused.Outcome, refused.Diagnostics, ReplayOperation.StoredTextAmbiguousCode, ReplayMessage);
            Assert.Equal(OperationOutcome.InvalidInput, malformed.Outcome);
            Assert.Equal(ReplayOperation.RequestMalformedCode, Assert.Single(malformed.Diagnostics).Code);
            AssertRefused(
                heldRefused.Outcome, heldRefused.Diagnostics, ReplayOperation.StoredTextAmbiguousCode, ReplayMessage);
        }

        [Fact]
        public async Task UnmarkedCapture_Accepted_SendsAndExportsItsTextAsStored_AndSaysSo()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = Write("unmarked", new CapturedExchange("GET", server.Origin + "/one", StoredHeaders));
            var accepted = new ReplayOptions(AcceptStoredText: true);
            var expected = new StoredTextSummary("ambiguousUnmarked", ReplayOperation.AcceptedAsStoredPolicy);

            OperationResult<ReplayResult> sent = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1")), accepted));
            OperationResult<ReplayResult> edited = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("1")), accepted, new ReplayEdits(Url: server.Origin + "/edited")));
            OperationResult<ReplayExport> exported = new ReplayOperation().PrepareForExport(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1")), accepted), CancellationToken.None);

            AssertAcceptedWithOneWarning(sent.Succeeded, sent.Diagnostics);
            AssertAcceptedWithOneWarning(edited.Succeeded, edited.Diagnostics);
            AssertAcceptedWithOneWarning(exported.Succeeded, exported.Diagnostics);
            Assert.Equal((expected, Sha256(capture)), (sent.Value.StoredText, sent.Value.InputSha256));
            Assert.Equal(expected, edited.Value.StoredText);
            Assert.Equal(expected, exported.Value.StoredText);
            Assert.Equal(new[] { "/one", "/edited" }, server.Requests.Select(request => request.Target));
            Assert.All(server.Requests, request =>
                Assert.Equal(("\"v1\"", "\"\""), (request.Header("If-None-Match"), request.Header("X-Adjacent"))));
            RequestReplayDraft draft = Assert.Single(exported.Value.Items).Draft;
            Assert.Contains(StoredHeaders.TrimEnd('\r', '\n'), draft.HeadersText, StringComparison.Ordinal);
            Assert.Equal(CaptureTextSemantics.AmbiguousUnmarked, draft.CaptureTextSource.Semantics);
        }

        [Theory]
        [InlineData("marked", false, "literal")]
        [InlineData("marked", true, "literal")]
        [InlineData("saz", false, "notApplicable")]
        [InlineData("saz", true, "notApplicable")]
        public async Task MarkedCaptureAndSaz_NeedNoDecision_AndTheAcceptanceChangesNothing(string kind, bool accept, string semantics)
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = Write(kind, new CapturedExchange("GET", server.Origin + "/one", StoredHeaders));
            var options = new ReplayOptions(AcceptStoredText: accept);

            OperationResult<ReplayResult> sent = await Execute(new ReplayRequest(ReplaySource.FromFile(capture, Selection("1")), options));
            OperationResult<ReplayExport> exported = new ReplayOperation().PrepareForExport(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1")), options), CancellationToken.None);

            Assert.True(sent.Succeeded);
            Assert.True(exported.Succeeded);
            Assert.Empty(sent.Diagnostics);
            Assert.Empty(exported.Diagnostics);
            Assert.Equal(new StoredTextSummary(semantics, "notNeeded"), sent.Value.StoredText);
            Assert.Equal(new StoredTextSummary(semantics, "notNeeded"), exported.Value.StoredText);
            Assert.Equal("\"v1\"", Assert.Single(server.Requests).Header("If-None-Match"));
        }

        // The run's acceptance covers its capture file only: rows and drafts that the caller holds carry their own source,
        // which it never covers, whether sent, previewed, expanded as a sequence or exported. A Literal row passes.
        [Fact]
        public async Task TheAcceptanceCoversOnlyTheRunsCaptureFile_NotRowsOrDraftsThatTheCallerHolds()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            ProxyConnectionModel unmarked = Model(Write("unmarked", new CapturedExchange("GET", server.Origin + "/one", StoredHeaders)));
            ProxyConnectionModel marked = Model(Write("marked", new CapturedExchange("GET", server.Origin + "/two", StoredHeaders)));
            var accepted = new ReplayOptions(AcceptStoredText: true);
            RequestReplayDraft draft = RequestReplayDraft.FromCapture(unmarked);
            RequestReplayDraft template = RequestReplayDraft.FromCapture(unmarked);
            template.Url = server.Origin + "/item/#";
            Assert.True(ReplaySequence.TryParse("1-2", out ReplaySequence sequence));

            foreach (ReplaySource source in new[]
            {
                ReplaySource.FromCaptures(new[] { marked, unmarked }),
                ReplaySource.FromDraft(draft),
                ReplaySource.FromTemplate(template, sequence),
            })
            {
                OperationResult<ReplayResult> sent = await Execute(new ReplayRequest(source, accepted));
                AssertRefused(sent.Outcome, sent.Diagnostics, ReplayOperation.StoredTextAmbiguousCode, ReplayMessage);
            }

            OperationResult<ReplayPlanPreview> preview = new ReplayOperation().Preview(ReplaySource.FromDraft(draft), accepted);
            AssertRefused(preview.Outcome, preview.Diagnostics, ReplayOperation.StoredTextAmbiguousCode, ReplayMessage);
            OperationResult<ReplayExport> exported = new ReplayOperation().PrepareForExport(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { unmarked }), accepted), CancellationToken.None);
            AssertRefused(exported.Outcome, exported.Diagnostics, ReplayOperation.StoredTextAmbiguousExportCode, ExportMessage);
            Assert.Equal(0, server.Connections);

            OperationResult<ReplayResult> literal = await Execute(new ReplayRequest(ReplaySource.FromCaptures(new[] { marked })));
            Assert.True(literal.Succeeded);
            Assert.Null(literal.Value.StoredText);
            Assert.Equal("/two", Assert.Single(server.Requests).Target);
        }

        // A held source's refusal comes before the plan size, as for a capture file: a sequence too large to send reports
        // the stored-text refusal in Preview and in ExecuteAsync alike, and so do too many held rows.
        [Fact]
        public async Task HeldSources_AreRefusedBeforeThePlanSize_AndPreviewAgreesWithExecute()
        {
            ProxyConnectionModel unmarked = Model(Write("unmarked", new CapturedExchange("GET", "http://127.0.0.1:9/one", StoredHeaders)));
            RequestReplayDraft template = RequestReplayDraft.FromCapture(unmarked);
            template.Url = "http://127.0.0.1:9/item/#";
            Assert.True(ReplaySequence.TryParse("1-10000", out ReplaySequence sequence));
            var twice = new ReplayOptions(Repeat: 2);

            OperationResult<ReplayPlanPreview> preview = new ReplayOperation().Preview(ReplaySource.FromTemplate(template, sequence), twice);
            OperationResult<ReplayResult> sequenced = await Execute(new ReplayRequest(ReplaySource.FromTemplate(template, sequence), twice));
            OperationResult<ReplayResult> rows = await Execute(new ReplayRequest(
                ReplaySource.FromCaptures(Enumerable.Repeat(unmarked, 11).ToArray()), new ReplayOptions(Repeat: 1000)));

            AssertRefused(preview.Outcome, preview.Diagnostics, ReplayOperation.StoredTextAmbiguousCode, ReplayMessage);
            AssertRefused(sequenced.Outcome, sequenced.Diagnostics, ReplayOperation.StoredTextAmbiguousCode, ReplayMessage);
            AssertRefused(rows.Outcome, rows.Diagnostics, ReplayOperation.StoredTextAmbiguousCode, ReplayMessage);
        }

        // The Replays tab stores what its recorder kept of a send (the request as XMAT prepared it, the response it got, and
        // the markers of any body it did not keep) literally, with the send's provenance; Literal is that storage
        // classification, not proof of what crossed the wire. So an accepted send's row is Literal: it goes into the marked
        // table, a save keeps marker 1, and Reissue or Edit and resend of it needs no decision.
        [Fact]
        public async Task AnAcceptedSend_IsRecordedAsALiteralRow()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("reply"));
            string capture = Write("unmarked", new CapturedExchange("GET", server.Origin + "/one", StoredHeaders));
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable devices = CreateDevicesTable(database);
            IDataTable table = CreateWebProxyTable(database);
            devices.AddRow((long)DeviceType.Unknown, "XMAT Replays", (long)CaptureType.WebProxy);
            var controller = new WebServiceDeviceCaptureController("XMAT Replays", DeviceType.Unknown, true);
            var recorder = new ReplayRecorder(controller, table, action => action(), _ => ReplayProvenance.Composed);

            OperationResult<ReplayResult> sent = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("1")), new ReplayOptions(AcceptStoredText: true), Sink: recorder));

            Assert.True(sent.Succeeded);
            ProxyConnectionModel row = Assert.Single(controller.ProxyConnections);
            Assert.Equal(CaptureTextSemantics.Literal, row.CaptureTextSource.Semantics);
            Assert.Equal(row.RecordRowId, (long)Assert.Single(table.Dataset.DataRecords).RowId);
            Assert.Contains("\r\nIf-None-Match: \"v1\"\r\nX-Adjacent: \"\"\r\n", row.RequestLineAndHeaders, StringComparison.Ordinal);

            OperationResult<ReplayResult> reissued = await Execute(new ReplayRequest(ReplaySource.FromCaptures(new[] { row })));
            OperationResult<ReplayResult> resent = await Execute(
                new ReplayRequest(ReplaySource.FromDraft(RequestReplayDraft.FromCapture(row))));
            Assert.True(reissued.Succeeded);
            Assert.True(resent.Succeeded);
            Assert.Equal(3, server.Requests.Count);

            string saved = Path.Join(_directory, "saved.xmatcap");
            database.SaveToFile(saved);
            Assert.Equal(1L, UserVersion(saved));
            using var loaded = new SqLtReadonlyDatabase(saved);
            Assert.Equal(CaptureTextSemantics.Literal, loaded.CaptureTextSource.Semantics);
            IDataRecord stored = Assert.Single(loaded.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
            Assert.Equal(row.RequestLineAndHeaders, new ProxyConnectionModel(stored).RequestLineAndHeaders);
        }

        // Q evidence 3, parity leg (QP 11.6): a real AddRow with a quote-bearing device name and header values, the real
        // save, a guarded load, the production model and RequestReplayDraft.FromCapture, then production replay to a
        // loopback listener, which receives the request line and header lines byte for byte (the sender adds only the
        // empty body's Content-Length).
        [Fact]
        public async Task MarkedCapture_SaveLoadFromCaptureAndReplay_PutTheExactQuotesOnTheWire()
        {
            const string Device = "dev\"ice";
            using var server = new RawLoopback();
            string head =
                "GET / HTTP/1.1\r\n" +
                $"Host: 127.0.0.1:{server.Port}\r\n" +
                "If-None-Match: \"v1\"\r\n" +
                "X-Adjacent: \"\"\r\n\r\n";
            string path = Path.Join(_directory, "fidelity.xmatcap");
            Directory.CreateDirectory(_directory);
            using (var database = new SqLtDatabase())
            {
                database.Initialize();
                IDataTable devices = CreateDevicesTable(database);
                IDataTable captures = CreateWebProxyTable(database);
                devices.AddRow((long)DeviceType.XboxConsole, Device, (long)CaptureType.WebProxy);
                captures.AddRow(
                    Device,
                    1L,
                    1L,
                    ProxyConnectionModel.ToDateTimeString(DateTime.UnixEpoch),
                    ProxyConnectionModel.ToDateTimeString(DateTime.UnixEpoch.AddMilliseconds(10)),
                    "http",
                    "127.0.0.1",
                    server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "GET",
                    "/",
                    "200",
                    head,
                    string.Empty,
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
                    string.Empty,
                    "10.0.0.1");
                database.SaveToFile(path);
            }

            Assert.Equal(1L, UserVersion(path));
            using var saved = new SqLtReadonlyDatabase(path);
            IDataRecord record = Assert.Single(
                saved.TableByName(WebServiceCaptureMethod.TableName)
                    .Dataset.DataRecordsWhen(new FieldValue<string>(WebServiceCaptureMethod.FieldKey_DeviceName, Device)));
            var model = new ProxyConnectionModel(record);
            Assert.Equal(head, model.RequestLineAndHeaders);
            Assert.Same(saved.CaptureTextSource, model.CaptureTextSource);
            RequestReplayDraft draft = RequestReplayDraft.FromCapture(model);

            Task<ReceivedRequest> received = server.AcceptAsync();
            OperationResult<ReplayResult> sent = await Execute(new ReplayRequest(ReplaySource.FromDraft(draft)));

            Assert.True(sent.Succeeded, string.Join(", ", sent.Diagnostics.Select(diagnostic => diagnostic.Code)));
            Assert.Equal(head[..^2] + "Content-Length: 0\r\n\r\n", (await received).Head);
        }

        private const string ReplayMessage =
            "This unmarked XMAT capture requires --accept-stored-text before replay, or xmat mark-capture-text to create a " +
                "marked copy.";

        private const string ExportMessage =
            "This unmarked XMAT capture requires --accept-stored-text to export its stored text unchanged, or xmat " +
                "mark-capture-text to create a marked copy.";

        private static void AssertRefused(
            OperationOutcome outcome, IReadOnlyList<OperationDiagnostic> diagnostics, string code, string message)
        {
            Assert.Equal(OperationOutcome.InvalidInput, outcome);
            OperationDiagnostic error = Assert.Single(diagnostics);
            Assert.Equal((code, OperationDiagnosticSeverity.Error, message), (error.Code, error.Severity, error.Message));
        }

        // An accepted unmarked run proceeds and warns, once, that the text is ambiguous.
        private static void AssertAcceptedWithOneWarning(bool succeeded, IReadOnlyList<OperationDiagnostic> diagnostics)
        {
            Assert.True(succeeded, string.Join(", ", diagnostics.Select(diagnostic => diagnostic.Code)));
            OperationDiagnostic warning = Assert.Single(diagnostics);
            Assert.Equal(("capture.text.ambiguous", OperationDiagnosticSeverity.Warning), (warning.Code, warning.Severity));
        }

        // A marked or unmarked XMAT capture, or a Fiddler archive, of these exchanges on one device.
        private string Write(string kind, params CapturedExchange[] exchanges)
        {
            string name = Guid.NewGuid().ToString("N");
            if (kind == "saz")
            {
                return ReplayCaptures.WriteSaz(_directory, name + ".saz", exchanges);
            }

            string path = ReplayCaptures.WriteXmatcap(_directory, name + ".xmatcap", ("Device 1", exchanges));
            if (kind == "unmarked")
            {
                HostileSqliteSchemas.SetUserVersion(path, CaptureTextFormat.AmbiguousUserVersion);
            }

            return path;
        }

        private static void SetStoredDestination(
            string path,
            string scheme,
            string host,
            string port,
            string requestPath)
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE \"{WebServiceCaptureMethod.TableName}\" SET " +
                $"\"{WebServiceCaptureMethod.FieldKey_RequestScheme}\" = $scheme, " +
                $"\"{WebServiceCaptureMethod.FieldKey_RequestHost}\" = $host, " +
                $"\"{WebServiceCaptureMethod.FieldKey_RequestPort}\" = $port, " +
                $"\"{WebServiceCaptureMethod.FieldKey_RequestPath}\" = $path";
            command.Parameters.AddWithValue("$scheme", scheme);
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$port", port);
            command.Parameters.AddWithValue("$path", requestPath);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        // The production model of a capture file's one request, as ReplayOperation reads it.
        private static ProxyConnectionModel Model(string capture)
        {
            OperationResult<CaptureRecordSet> read = new AnalyzeCaptureOperation().ReadCapture(capture, null, CancellationToken.None);
            Assert.True(read.Succeeded);
            return new ProxyConnectionModel(Assert.Single(Assert.Single(read.Value.Devices).Records));
        }

        private static IDataTable CreateDevicesTable(SqLtDatabase database) =>
            database.CreateTable(
                "CaptureDevices", new Field<long>("DeviceType"), new Field<string>("DeviceName"), new Field<long>("CaptureType"));

        private static IDataTable CreateWebProxyTable(SqLtDatabase database) =>
            database.CreateTable(
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

        private static long UserVersion(string path)
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            return (long)command.ExecuteScalar();
        }

        private static Task<OperationResult<ReplayResult>> Execute(ReplayRequest request) =>
            new ReplayOperation().ExecuteAsync(request, OperationContext.None, CancellationToken.None);

        private static ReplaySelection Selection(string text)
        {
            Assert.True(ReplaySelection.TryParse(text, out ReplaySelection selection));
            return selection;
        }

        private static string Sha256(string file) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    }
}
