// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    public class ReplayRecorderTooLargeTests
    {
        public ReplayRecorderTooLargeTests() => Localization.LoadLanguage("en-us");

        [Fact]
        public async Task TooLargeResponse_IsMarkedNotKept_AndVerificationSkipsItsMissingBody()
        {
            await using var server = new LoopbackHttpServer(
                _ => new LoopbackResponse(Body: new byte[2048]));
            using var sender = new ReplaySender();
            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = server.Origin + "/large" },
                ReplaySendOptions.Default with { MaxResponseBytes = 1024 },
                CancellationToken.None);

            Assert.Equal(ReplayOutcome.TooLarge, exchange.Outcome);
            ProxyConnectionModel row = ReplayRecorder.ToRow(1, ReplayProvenance.Composed, exchange);

            Assert.Empty(row.ResponseBody);
            Assert.True(row.ResponseHeaders.ContainsKey(RequestReplayDraft.ResponseBodyNotKeptHeader));
            Assert.Equal(
                Localization.GetLocalizedString("REPLAY_NOTE_RESPONSE_TOO_LARGE"),
                row.ReplayNote);

            OperationResult<ReplayResult> verification = await new ReplayOperation().ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromCaptures(new[] { row }),
                    new ReplayOptions(Verify: true)),
                OperationContext.None,
                CancellationToken.None);

            Assert.True(verification.Succeeded);
            Assert.Null(Assert.Single(verification.Value.Items).Verification);
            Assert.Contains(
                verification.Diagnostics,
                diagnostic => diagnostic.Code == ReplayOperation.VerifySkippedCode);
        }
    }
}
