// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations;
using XMAT.Operations.Inspection;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // A response larger than the send's response size limit comes without its body (TooLarge). Its Replays row says so,
    // with the body-not-kept marker, so a preview shows the body as not kept and verify skips it, and with a note that
    // names the limit without a number; a response that really was empty is shown and verified as empty.
    public class ReplayRecorderTooLargeTests
    {
        public ReplayRecorderTooLargeTests() => Localization.LoadLanguage("en-us");

        [Theory]
        [InlineData(false, "2048")]
        [InlineData(true, "unknown")]
        public async Task ATooLargeResponse_IsRecordedAsNotKept_WithTheSizeItDeclared(bool chunked, string declared)
        {
            await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(Headers: "X-Kept: yes\r\n", Body: new byte[2048], Chunked: chunked));
            using var sender = new ReplaySender();
            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = server.Origin + "/large" },
                ReplaySendOptions.Default with { MaxResponseBytes = 1024 },
                CancellationToken.None);
            Assert.Equal(ReplayOutcome.TooLarge, exchange.Outcome);

            ProxyConnectionModel row = ReplayRecorder.ToRow(1, ReplayProvenance.Composed, exchange);

            Assert.Empty(row.ResponseBody);
            Assert.EndsWith(
                $"\r\n{RequestReplayDraft.ResponseBodyNotKeptHeader}: {declared}\r\n\r\n", row.ResponseLineAndHeaders, StringComparison.Ordinal);
            Assert.Contains("\r\nX-Kept: yes\r\n", row.ResponseLineAndHeaders, StringComparison.Ordinal);
            Assert.Equal(Localization.GetLocalizedString("REPLAY_NOTE_RESPONSE_TOO_LARGE"), row.ReplayNote);
            Assert.DoesNotContain("1024", row.ReplayNote, StringComparison.Ordinal);
            Assert.DoesNotContain("2048", row.ReplayNote, StringComparison.Ordinal);
            Assert.DoesNotContain("NO_IMPL_", row.ReplayNote, StringComparison.Ordinal);
            Assert.True(
                BodyPreviewRequest.FromHeaders(row.ResponseHeaders, row.ResponseBody, BodyPreviewSide.Response).NotRetained);
        }

        // Verify never compares a replay with a body that the row did not keep; it compares one with a body that was empty.
        [Fact]
        public async Task Verify_SkipsATooLargeRow_AndComparesARowWhoseBodyWasEmpty()
        {
            await using var server = new LoopbackHttpServer(
                request => request.Target == "/large" ? new LoopbackResponse(Body: new byte[2048]) : LoopbackResponse.Ok());
            using var sender = new ReplaySender();
            ReplayExchange tooLarge = await sender.SendAsync(
                new RequestReplayDraft { Url = server.Origin + "/large" }, ReplaySendOptions.Default with { MaxResponseBytes = 1024 }, CancellationToken.None);
            ReplayExchange empty = await sender.SendAsync(
                new RequestReplayDraft { Url = server.Origin + "/empty" }, ReplaySendOptions.Default, CancellationToken.None);
            ProxyConnectionModel tooLargeRow = ReplayRecorder.ToRow(1, ReplayProvenance.Composed, tooLarge);
            ProxyConnectionModel emptyRow = ReplayRecorder.ToRow(2, ReplayProvenance.Composed, empty);
            Assert.Equal((ReplayOutcome.TooLarge, ReplayOutcome.Completed), (tooLarge.Outcome, empty.Outcome));
            Assert.DoesNotContain(RequestReplayDraft.ResponseBodyNotKeptHeader, emptyRow.ResponseLineAndHeaders, StringComparison.OrdinalIgnoreCase);
            Assert.Null(emptyRow.ReplayNote);
            Assert.False(
                BodyPreviewRequest.FromHeaders(emptyRow.ResponseHeaders, emptyRow.ResponseBody, BodyPreviewSide.Response).NotRetained);

            OperationResult<ReplayResult> result = await new ReplayOperation().ExecuteAsync(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { tooLargeRow, emptyRow }), new ReplayOptions(Verify: true)),
                OperationContext.None,
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Null(result.Value.Items[0].Verification);
            Assert.True(result.Value.Items[1].Verification.Matches);
            Assert.Equal(ReplayOperation.VerifySkippedCode, Assert.Single(result.Diagnostics).Code);
        }

        // A body that the tab's budget did not keep keeps its own marker, with its size, and its own note.
        [Fact]
        public void ABudgetOmission_KeepsItsSizeAndNote_AndIsNotTakenForTooLarge()
        {
            var exchange = new ReplayExchange
            {
                Outcome = ReplayOutcome.Completed,
                Request = new ReplayMessage("GET / HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", "example.test") }, null, []),
                RequestUri = new Uri("http://example.test/"),
                Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Response = new ReplayMessage(
                    "HTTP/1.1 200 OK", null, new[] { new KeyValuePair<string, string>("Content-Length", "11") }, "hello world"u8.ToArray()),
                StatusCode = 200,
            };

            ProxyConnectionModel row = ReplayRecorder.ToRow(1, ReplayProvenance.Composed, exchange, keepResponseBody: false);

            Assert.EndsWith(
                $"\r\n{RequestReplayDraft.ResponseBodyNotKeptHeader}: 11\r\n\r\n", row.ResponseLineAndHeaders, StringComparison.Ordinal);
            Assert.Equal(Localization.GetLocalizedString("REPLAY_NOTE_RESPONSE_BODY", 11L, 256L), row.ReplayNote);
        }
    }
}
