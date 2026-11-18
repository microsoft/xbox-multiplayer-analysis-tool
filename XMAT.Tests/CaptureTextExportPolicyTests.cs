// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Export;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Operations.Export;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests
{
    // A sanitized SAZ export rewrites stored text, so the shared export operation refuses a source whose text is not
    // known to be literal, for the command line and the GUI alike, before anything is written. An xmatcap export copies
    // the text unchanged and keeps the literal marker only when the source had it.
    public sealed class CaptureTextExportPolicyTests
    {
        public static TheoryData<string> UnmarkedSources => new() { "unknown", "missing", "ambiguous-file" };

        [Theory]
        [MemberData(nameof(UnmarkedSources))]
        public async Task SanitizedExport_RefusesAnUnmarkedSourceBeforeWriting(string sourceKind)
        {
            var publisher = new RecordingExportPublisher();
            bool writerCreated = false;
            var snapshot = new FakeExportSnapshot(new[] { Session(1) }, new[] { Row(1) })
            {
                TextSource = sourceKind switch
                {
                    "unknown" => CaptureTextSource.Unknown,
                    "missing" => null,
                    _ => CaptureTextSource.CreateFile(
                        CaptureTextSemantics.AmbiguousUnmarked,
                        new SqLtFileIdentity(7, 11),
                        _ => new byte[32],
                        () => true),
                },
            };

            OperationResult<ExportCaptureResult> result = await Operation(publisher, () => writerCreated = true)
                .ExecuteAsync(Request(snapshot, CaptureExportFormat.SanitizedSaz, "result.saz"), OperationContext.None, CancellationToken.None);

            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == CaptureExportErrorCodes.TextAmbiguous);
            Assert.False(writerCreated);
            Assert.Equal(0, publisher.Calls);
            Assert.NotEqual(true, result.Value?.Published);
        }

        [Theory]
        [InlineData(nameof(CaptureExportFormat.SanitizedSaz), "literal")]
        [InlineData(nameof(CaptureExportFormat.Xmatcap), "literal")]
        [InlineData(nameof(CaptureExportFormat.Xmatcap), "unknown")]
        public async Task Export_OtherwiseProceeds(string formatName, string sourceKind)
        {
            CaptureExportFormat format = Enum.Parse<CaptureExportFormat>(formatName);
            var publisher = new RecordingExportPublisher();
            bool writerCreated = false;
            var snapshot = new FakeExportSnapshot(new[] { Session(1) }, new[] { Row(1) })
            {
                TextSource = sourceKind == "literal" ? CaptureTextSource.Literal : CaptureTextSource.Unknown,
            };

            OperationResult<ExportCaptureResult> result = await Operation(publisher, () => writerCreated = true)
                .ExecuteAsync(
                    Request(snapshot, format, format == CaptureExportFormat.Xmatcap ? "result.xmatcap" : "result.saz"),
                    OperationContext.None,
                    CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == CaptureExportErrorCodes.TextAmbiguous);
            Assert.True(writerCreated);
            Assert.Equal(1, publisher.Calls);
        }

        [Theory]
        [InlineData(CaptureTextFormat.AmbiguousUserVersion, false)]
        [InlineData(CaptureTextFormat.LiteralUserVersion, true)]
        public async Task CaptureFileExport_FollowsTheSourceMarker(int userVersion, bool literal)
        {
            using var directory = new ExportTestDirectory();
            string source = SyntheticCaptures.WriteXmatcap(
                directory.Root,
                "source.xmatcap",
                new SyntheticXmatcapOptions(RequestsPerDevice: 1, UserVersion: (uint)userVersion));
            string sanitized = directory.PathOf("sanitized.saz");
            string copy = directory.PathOf("copy.xmatcap");

            OperationResult<ExportCaptureResult> sanitizedResult = await Export(source, CaptureExportFormat.SanitizedSaz, sanitized);
            OperationResult<ExportCaptureResult> copyResult = await Export(source, CaptureExportFormat.Xmatcap, copy);

            Assert.Equal(literal ? OperationOutcome.Succeeded : OperationOutcome.InvalidInput, sanitizedResult.Outcome);
            Assert.Equal(literal, File.Exists(sanitized));
            Assert.Equal(OperationOutcome.Succeeded, copyResult.Outcome);
            using var exported = new SqLtReadonlyDatabase(copy);
            Assert.Equal(
                literal ? CaptureTextSemantics.Literal : CaptureTextSemantics.AmbiguousUnmarked,
                exported.CaptureTextSource.Semantics);
        }

        private static ExportCaptureSessionsOperation Operation(RecordingExportPublisher publisher, Action writerCreated) =>
            new()
            {
                Publisher = publisher,
                WriterFactory = (_, _, _, _) =>
                {
                    writerCreated();
                    return new RecordingExportWriter();
                },
            };

        private static ExportCaptureRequest Request(ICaptureExportSnapshot snapshot, CaptureExportFormat format, string output) =>
            new(null, snapshot, null, Selection(), format, output, ReplaceExisting: false, AnalysisLimits.Default,
                AllowUnprotectedOutput: false);

        private static Task<OperationResult<ExportCaptureResult>> Export(string source, CaptureExportFormat format, string output) =>
            new ExportCaptureSessionsOperation().ExecuteAsync(
                new ExportCaptureRequest(source, null, null, Selection(), format, output, ReplaceExisting: false,
                    AnalysisLimits.Default, AllowUnprotectedOutput: false),
                OperationContext.None,
                CancellationToken.None);
    }
}
