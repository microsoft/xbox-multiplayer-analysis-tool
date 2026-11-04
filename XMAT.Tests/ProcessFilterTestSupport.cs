// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text.Json;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    // Shared helpers for the process-filter tests. Expected selectors always come from the shared parser, so the tests
    // compare canonical selectors without restating the canonical form.
    internal static class ProcessFilterTestSupport
    {
        internal static ProcessFilterSpec Spec(IEnumerable<string> include, IEnumerable<string> exclude = null)
        {
            OperationResult<ProcessFilterSpec> parsed = ProcessFilter.Parse(
                include ?? Array.Empty<string>(),
                exclude ?? Array.Empty<string>());
            Assert.True(parsed.Succeeded, "The test selectors must be valid.");
            return parsed.Value;
        }

        internal static string[] Items(ImmutableArray<string> values) =>
            values.IsDefault ? Array.Empty<string>() : values.ToArray();

        internal static string[] Strings(JsonElement array) =>
            array.EnumerateArray().Select(item => item.GetString()).ToArray();

        // A JSON value without its original indentation, so documents written at different depths compare equal.
        internal static string Compact(JsonElement element) => JsonSerializer.Serialize(element);

        internal static long[] RowIds(IEnumerable<CaptureRowKey> keys) =>
            keys.Select(key => key.RowId).OrderBy(id => id).ToArray();

        internal static ClientProcessInfo Local(string name, int processId) =>
            new(name, processId, null, ClientProcessStatus.Local);

        internal static ProxyConnectionModel WithProcess(
            ProxyConnectionModel model,
            ClientProcessInfo process,
            bool resolving = false)
        {
            model.SetClientProcess(process, resolving);
            return model;
        }
    }

    // The shared selection, recorded: each request and the result it returned. A rewrite models a faulty selection.
    // It completes synchronously, as the shared operation does, so a caller on the UI thread never waits for it.
    internal sealed class RecordingProcessSelection : IOperation<FilterCaptureSessionsRequest, FilterCaptureSessionsResult>
    {
        private readonly Func<FilterCaptureSessionsResult, FilterCaptureSessionsResult> _rewrite;

        internal RecordingProcessSelection(Func<FilterCaptureSessionsResult, FilterCaptureSessionsResult> rewrite = null)
        {
            _rewrite = rewrite;
        }

        internal List<FilterCaptureSessionsRequest> Requests { get; } = new();

        internal List<FilterCaptureSessionsResult> Results { get; } = new();

        public Task<OperationResult<FilterCaptureSessionsResult>> ExecuteAsync(
            FilterCaptureSessionsRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            OperationResult<FilterCaptureSessionsResult> shared = new FilterCaptureSessionsOperation()
                .ExecuteAsync(request, context, cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (!shared.Succeeded)
            {
                return Task.FromResult(shared);
            }

            FilterCaptureSessionsResult value = _rewrite == null ? shared.Value : _rewrite(shared.Value);
            Results.Add(value);
            return Task.FromResult(OperationResult<FilterCaptureSessionsResult>.Success(value));
        }
    }
}
