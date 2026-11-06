// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;

namespace XMAT.Tests.Operations.Captures
{
    public sealed class FilterCaptureSessionsOperationTests
    {
        [Fact]
        public async Task ExecuteAsync_ReturnsExactKeysAndCountsPerOrdinalDevice()
        {
            CaptureSessionRef[] sessions =
            [
                Session(1, "d1", Local("game.exe", 10)),
                Session(2, "d1", Local("game-helper.exe", 11)),
                Session(3, "d1", ClientProcessInfo.NotRecorded),
                Session(4, "d2", Local("GAME.EXE", 12)),
                Session(5, "d2", Local("browser.exe", 13)),
            ];
            var filter = new ProcessFilterSpec(
                ImmutableArray.Create("game*.exe"),
                ImmutableArray.Create("game-helper.exe"));

            OperationResult<FilterCaptureSessionsResult> result =
                await new FilterCaptureSessionsOperation()
                    .ExecuteAsync(
                        new FilterCaptureSessionsRequest(
                            sessions,
                            filter),
                        OperationContext.None,
                        CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new[] { sessions[0].Key, sessions[3].Key }, result.Value.IncludedKeys);
            Assert.Equal(
                new ProcessFilterCounts(Total: 5, Included: 2, Hidden: 3, Unknown: 1),
                result.Value.Scope.Counts);
            Assert.Equal(new ProcessFilterCounts(3, 1, 2, 1), result.Value.Scope.Devices["d1"]);
            Assert.Equal(new ProcessFilterCounts(2, 1, 1, 0), result.Value.Scope.Devices["d2"]);
            Assert.Equal(filter, result.Value.Scope.Filter);
        }

        [Fact]
        public async Task ExecuteAsync_EmptyFilterIncludesUnknownSessions()
        {
            CaptureSessionRef[] sessions =
            [
                Session(1, "d1", ClientProcessInfo.NotRecorded),
                Session(
                    2,
                    "d1",
                    new ClientProcessInfo(
                        string.Empty,
                        0,
                        null,
                        ClientProcessStatus.Remote)),
            ];

            OperationResult<FilterCaptureSessionsResult> result =
                await new FilterCaptureSessionsOperation()
                    .ExecuteAsync(
                        new FilterCaptureSessionsRequest(
                            sessions,
                            ProcessFilterSpec.Empty),
                        OperationContext.None,
                        CancellationToken.None);

            Assert.Equal(sessions.Select(session => session.Key), result.Value.IncludedKeys);
            Assert.Equal(new ProcessFilterCounts(2, 2, 0, 2), result.Value.Scope.Counts);
        }

        [Fact]
        public void Select_ValidatesBeforeCancellationAndThenObservesIt()
        {
            CaptureSessionRef session = Session(1, "d1", Local("game.exe", 10));
            var invalid = new ProcessFilterSpec(
                ImmutableArray.Create(@"C:\private\game.exe"),
                ImmutableArray<string>.Empty);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            OperationResult<FilterCaptureSessionsResult> rejected =
                FilterCaptureSessionsOperation.Select(
                    new FilterCaptureSessionsRequest([session], invalid),
                    cancellation.Token);

            Assert.Equal(OperationOutcome.InvalidInput, rejected.Outcome);
            Assert.Equal("process-filter.invalid-selector", Assert.Single(rejected.Diagnostics).Code);
            Assert.Throws<OperationCanceledException>(() =>
                FilterCaptureSessionsOperation.Select(
                    new FilterCaptureSessionsRequest([session], ProcessFilterSpec.Empty),
                    cancellation.Token));
        }

        [Fact]
        public void CaptureSessionReference_IsBodyFree()
        {
            string[] members = typeof(CaptureSessionRef).GetProperties().Select(property => property.Name).ToArray();

            Assert.Equal(
                new[]
                {
                    nameof(CaptureSessionRef.Key),
                    nameof(CaptureSessionRef.DeviceId),
                    nameof(CaptureSessionRef.Process),
                },
                members);
        }

        private static CaptureSessionRef Session(long rowId, string deviceId, ClientProcessInfo process) => new(
            new CaptureRowKey(new Guid("A6D38BEC-3414-445E-91A6-E79E30DDEAA0"), 4, rowId, rowId + 100),
            deviceId,
            process);

        private static ClientProcessInfo Local(string name, int processId) => new(
            name,
            processId,
            DateTimeOffset.Parse("2026-11-03T20:00:00.0000000+00:00", System.Globalization.CultureInfo.InvariantCulture),
            ClientProcessStatus.Local);
    }
}
