// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using XMAT.Operations;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests.Operations.Export
{
    public sealed class CaptureSessionSelectorTests
    {
        [Fact]
        public void Select_ComposesEveryFilterAndMatchesHostPathGlobs()
        {
            CaptureExportSession[] sessions =
            [
                Session(1),
                Session(2, path: "/other"),
                Session(3, process: Process("Tool.exe", 4200)),
                Session(4, status: "500"),
                Session(5, method: "POST"),
                Session(6),
                Session(7, process: Process("Game.exe", 9999)),
                Session(8, path: "/api/private/settings"),
                Session(9, deviceId: "d2"),
            ];
            ProcessFilterSpec process = Spec(["game*.exe"], ["9999"]);
            var selection = Selection(
                process,
                new HostSelection(
                    HostMatchMode.Glob,
                    ImmutableArray.Create("api.example.test/api/*"),
                    ImmutableArray.Create("api.example.test/api/private*")),
                ImmutableArray.Create("200"),
                ImmutableArray.Create("GET"),
                ImmutableHashSet.Create(Key(1), Key(2), Key(3), Key(4), Key(5), Key(7), Key(8)));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: true);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new[] { Key(1) }, result.Value.Keys);
            Assert.Equal(
                new CaptureExportCounts(8, 2, 1, 1, 0, 0, 0, 0),
                result.Value.Counts);
            Assert.Equal(result.Value.Counts, result.Value.Devices["d1"]);
            Assert.False(result.Value.Devices.ContainsKey("d2"));
        }

        [Fact]
        public void Select_GlobIncludeMatchesAbsoluteAndOriginFormPaths()
        {
            CaptureExportSession[] sessions =
            [
                Session(1, path: "http://wire.example/api/private/absolute?token=1"),
                Session(2, path: "/api/private/origin?token=2"),
                Session(3, path: "https://wire.example/api/public"),
                Session(4, host: "other.example", path: "https://wire.example/api/private/other"),
            ];
            var selection = Selection(host: new HostSelection(
                HostMatchMode.Glob,
                ImmutableArray.Create("api.example.test/api/private/*"),
                ImmutableArray<string>.Empty));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: false);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new[] { Key(1), Key(2) }, result.Value.Keys);
        }

        [Fact]
        public void Select_GlobExcludeWithoutIncludeOmitsAbsoluteFormPath()
        {
            CaptureExportSession[] sessions =
            [
                Session(1, path: "https://wire.example/api/private/sensitive"),
                Session(2, path: "http://wire.example/api/public"),
                Session(3, host: "other.example", path: "http://wire.example/api/private/sensitive"),
            ];
            var selection = Selection(host: new HostSelection(
                HostMatchMode.Glob,
                null,
                ImmutableArray.Create("api.example.test/api/private/*")));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: false);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new[] { Key(2), Key(3) }, result.Value.Keys);
        }

        [Theory]
        [InlineData("/public/../private/dot")]
        [InlineData("/%70rivate/encoded")]
        public void Select_GlobIncludeCanonicalizesForwardedOriginPath(string path)
        {
            CaptureExportSession[] sessions =
            [
                Session(1, path: path),
                Session(2, path: "/%2Fprivate/reserved"),
                Session(3, path: "/public/item"),
                Session(4, host: "other.example", path: path),
                Session(5, path: "ftp://wire.example/private/item"),
                Session(6, path: "http://[broken/private/item"),
            ];
            var selection = Selection(host: new HostSelection(
                HostMatchMode.Glob,
                ImmutableArray.Create("api.example.test/private/*"),
                ImmutableArray<string>.Empty));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: false);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new[] { Key(1) }, result.Value.Keys);
        }

        [Fact]
        public void Select_GlobIncludeMatchesSuppliedOrAbsoluteUriHost()
        {
            CaptureExportSession[] sessions =
            [
                Session(1, host: "claimed.example", path: "http://destination.example/private/item"),
            ];
            OperationResult<CaptureSessionSelection> supplied = CaptureSessionSelector.Select(
                sessions,
                Selection(host: new HostSelection(
                    HostMatchMode.Glob,
                    ImmutableArray.Create("claimed.example/private/*"),
                    ImmutableArray<string>.Empty)),
                "d1",
                isLive: false);
            OperationResult<CaptureSessionSelection> destination = CaptureSessionSelector.Select(
                sessions,
                Selection(host: new HostSelection(
                    HostMatchMode.Glob,
                    ImmutableArray.Create("destination.example/private/*"),
                    ImmutableArray<string>.Empty)),
                "d1",
                isLive: false);
            OperationResult<CaptureSessionSelection> unrelated = CaptureSessionSelector.Select(
                sessions,
                Selection(host: new HostSelection(
                    HostMatchMode.Glob,
                    ImmutableArray.Create("other.example/private/*"),
                    ImmutableArray<string>.Empty)),
                "d1",
                isLive: false);

            Assert.Equal(new[] { Key(1) }, supplied.Value.Keys);
            Assert.Empty(unrelated.Value.Keys);
            Assert.Equal(new[] { Key(1) }, destination.Value.Keys);
        }

        [Fact]
        public void Select_GlobExcludeWithoutIncludeCanonicalizesForwardedOriginPaths()
        {
            CaptureExportSession[] sessions =
            [
                Session(1, path: "/public/../private/dot"),
                Session(2, path: "/%70rivate/encoded"),
                Session(3, path: "/%2Fprivate/reserved"),
                Session(4, path: "/public/item"),
            ];
            var selection = Selection(host: new HostSelection(
                HostMatchMode.Glob,
                null,
                ImmutableArray.Create("api.example.test/private/*")));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: false);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new[] { Key(3), Key(4) }, result.Value.Keys);
        }

        [Fact]
        public void Select_GlobExcludeWithoutIncludeMatchesAbsoluteUriHost()
        {
            CaptureExportSession[] sessions =
            [
                Session(1, host: "claimed.example", path: "https://destination.example/private/sensitive"),
                Session(2, host: "claimed.example", path: "https://other.example/private/safe"),
            ];
            var selection = Selection(host: new HostSelection(
                HostMatchMode.Glob,
                null,
                ImmutableArray.Create("destination.example/private/*")));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: false);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new[] { Key(2) }, result.Value.Keys);
        }

        [Theory]
        [InlineData("ws", "destination.example", "/public")]
        [InlineData("wss", "destination.example", "/public")]
        [InlineData("ws", "destination.example/private/*", "/private/item")]
        [InlineData("wss", "destination.example/private/*", "/private/item")]
        public void Select_GlobExcludeWithoutIncludeMatchesWebSocketDestination(
            string scheme,
            string pattern,
            string path)
        {
            CaptureExportSession[] sessions =
            [
                Session(1, host: "decoy.example", path: $"{scheme}://destination.example{path}"),
                Session(2, path: "/public/safe"),
            ];
            var selection = Selection(host: new HostSelection(
                HostMatchMode.Glob,
                null,
                ImmutableArray.Create(pattern)));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: false);

            Assert.Equal(new[] { Key(2) }, result.Value.Keys);
        }

        [Theory]
        [InlineData("api\u3002example.com", "api.example.com")]
        [InlineData("\uFF45\uFF58\uFF41\uFF4D\uFF50\uFF4C\uFF45.com", "example.com")]
        [InlineData("exam\u00ADple.com", "example.com")]
        public void Select_GlobExcludeWithoutIncludeUsesIdnDestinationSpelling(
            string destination,
            string canonical)
        {
            CaptureExportSession[] sessions =
            [
                Session(1, host: "decoy.example", path: $"http://{destination}/private/item"),
                Session(2, path: "/public/safe"),
            ];
            var selection = Selection(host: new HostSelection(
                HostMatchMode.Glob,
                null,
                ImmutableArray.Create(canonical + "/private/*")));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: false);

            Assert.Equal(new[] { Key(2) }, result.Value.Keys);
        }

        [Theory]
        [InlineData("0177.0.0.1")]
        [InlineData("127.1")]
        [InlineData("2130706433")]
        [InlineData("0x7f.0.0.1")]
        public void Select_GlobExcludeWithoutIncludeUsesNumericHostAlias(string host)
        {
            CaptureExportSession[] sessions =
            [
                Session(1, host: host, path: "/private/item"),
                Session(2, host: host, path: "/public/safe"),
            ];
            var selection = Selection(host: new HostSelection(
                HostMatchMode.Glob,
                null,
                ImmutableArray.Create("127.0.0.1/private/*")));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: false);

            Assert.Equal(new[] { Key(2) }, result.Value.Keys);
        }

        [Fact]
        public void Select_ExactModeUsesOrdinalEmptyDisplayValues()
        {
            CaptureExportSession[] sessions =
            [
                Session(1, host: "", status: "", method: "GET"),
                Session(2, host: "api.example.test", status: "", method: "GET"),
                Session(3, host: "API.example.test", status: "", method: "GET"),
            ];
            var selection = Selection(
                host: new HostSelection(
                    HostMatchMode.Exact,
                    ImmutableArray.Create(""),
                    ImmutableArray<string>.Empty),
                statuses: ImmutableArray.Create(""),
                methods: ImmutableArray.Create("GET"));

            OperationResult<CaptureSessionSelection> result =
                CaptureSessionSelector.Select(sessions, selection, "d1", isLive: false);
            OperationResult<CaptureSessionSelection> caseMismatch = CaptureSessionSelector.Select(
                sessions,
                Selection(host: new HostSelection(
                    HostMatchMode.Exact,
                    ImmutableArray.Create("api.example.test"),
                    ImmutableArray<string>.Empty)),
                "d1",
                isLive: false);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new[] { Key(1) }, result.Value.Keys);
            Assert.Equal(1, result.Value.Counts.Selected);
            Assert.Equal(1, result.Value.Counts.Eligible);
            Assert.Equal(new[] { Key(2) }, caseMismatch.Value.Keys);
        }

        [Fact]
        public void Select_DistinguishesDisabledEnabledEmptyAndExplicitEmpty()
        {
            CaptureExportSession[] sessions =
            [
                Session(1, status: ""),
                Session(2, status: "200"),
            ];

            OperationResult<CaptureSessionSelection> disabled = CaptureSessionSelector.Select(
                sessions,
                Selection(statuses: null),
                "d1",
                isLive: false);
            OperationResult<CaptureSessionSelection> enabledEmpty = CaptureSessionSelector.Select(
                sessions,
                Selection(statuses: ImmutableArray<string>.Empty),
                "d1",
                isLive: false);
            OperationResult<CaptureSessionSelection> explicitEmpty = CaptureSessionSelector.Select(
                sessions,
                Selection(statuses: ImmutableArray.Create("")),
                "d1",
                isLive: false);

            Assert.Equal(new[] { Key(1), Key(2) }, disabled.Value.Keys);
            Assert.Empty(enabledEmpty.Value.Keys);
            Assert.Equal(new[] { Key(1) }, explicitEmpty.Value.Keys);
        }

        [Fact]
        public void Select_MatchNoneAndLiveCompletionProduceHonestCounts()
        {
            CaptureExportSession[] sessions =
            [
                Session(1),
                Session(2, responseTimestamp: false),
                Session(3, responseHeaders: false, responseTimestamp: false),
            ];

            OperationResult<CaptureSessionSelection> live = CaptureSessionSelector.Select(
                sessions,
                Selection(),
                "d1",
                isLive: true);
            OperationResult<CaptureSessionSelection> none = CaptureSessionSelector.Select(
                sessions,
                Selection(matchNone: true),
                "d1",
                isLive: true);

            Assert.Equal(new[] { Key(1) }, live.Value.Keys);
            Assert.Equal(new CaptureExportCounts(3, 3, 3, 1, 0, 0, 2, 1), live.Value.Counts);
            Assert.Empty(none.Value.Keys);
            Assert.Equal(0, none.Value.Counts.Matched);
            Assert.Equal(0, none.Value.Counts.Selected);
        }

        [Fact]
        public void Select_SelectionValidationPrecedesSessionValidation()
        {
            var invalidProcess = new ProcessFilterSpec(
                ImmutableArray.Create(@"C:\private\game.exe"),
                ImmutableArray<string>.Empty);

            OperationResult<CaptureSessionSelection> result = CaptureSessionSelector.Select(
                [null],
                Selection(process: invalidProcess),
                "d1",
                isLive: false);

            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            Assert.Equal("export.selection.invalid-process", Assert.Single(result.Diagnostics).Code);
        }

        [Fact]
        public void Validate_CanonicalizesDefaultArraysAndPreservesDisabledArrays()
        {
            ImmutableArray<string>? defaultInclude = default(ImmutableArray<string>);
            ImmutableArray<string>? defaultStatuses = default(ImmutableArray<string>);
            var selection = Selection(
                host: new HostSelection(HostMatchMode.Exact, defaultInclude, default),
                statuses: defaultStatuses,
                methods: null);

            OperationResult<CaptureSelectionSpec> result = CaptureSelectionValidator.Validate(selection);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.True(result.Value.Host.Include.HasValue);
            Assert.False(result.Value.Host.Include.Value.IsDefault);
            Assert.Empty(result.Value.Host.Include.Value);
            Assert.False(result.Value.Host.Exclude.IsDefault);
            Assert.Empty(result.Value.Host.Exclude);
            Assert.True(result.Value.Statuses.HasValue);
            Assert.False(result.Value.Statuses.Value.IsDefault);
            Assert.Empty(result.Value.Statuses.Value);
            Assert.Null(result.Value.Methods);
        }

        [Fact]
        public void Validate_AcceptsExactEmptyButRejectsGlobEmptyWithoutEchoingIt()
        {
            CaptureSelectionSpec exact = Selection(
                host: new HostSelection(
                    HostMatchMode.Exact,
                    ImmutableArray.Create(""),
                    ImmutableArray.Create("")),
                statuses: ImmutableArray.Create(""),
                methods: ImmutableArray.Create(""));
            CaptureSelectionSpec glob = Selection(
                host: new HostSelection(
                    HostMatchMode.Glob,
                    ImmutableArray.Create(""),
                    ImmutableArray<string>.Empty));

            OperationResult<CaptureSelectionSpec> accepted = CaptureSelectionValidator.Validate(exact);
            OperationResult<CaptureSelectionSpec> rejected = CaptureSelectionValidator.Validate(glob);

            Assert.Equal(OperationOutcome.Succeeded, accepted.Outcome);
            Assert.Equal(OperationOutcome.InvalidInput, rejected.Outcome);
            Assert.Equal("export.selection.invalid-host", Assert.Single(rejected.Diagnostics).Code);
            Assert.DoesNotContain("\"\"", Assert.Single(rejected.Diagnostics).Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("process")]
        [InlineData("host")]
        public void Validate_RejectsInvalidOrOverlongBoundedSelectorsWithCodedDataFreeDiagnostics(string field)
        {
            const string canary = "selector-canary";
            string value = new string('x', 261) + canary;
            CaptureSelectionSpec selection = field == "process"
                ? Selection(process: new ProcessFilterSpec(
                    ImmutableArray.Create(value),
                    ImmutableArray<string>.Empty))
                : Selection(host: new HostSelection(
                    HostMatchMode.Glob,
                    ImmutableArray.Create(value),
                    ImmutableArray<string>.Empty));

            OperationResult<CaptureSelectionSpec> result = CaptureSelectionValidator.Validate(selection);

            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            OperationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal($"export.selection.invalid-{field}", diagnostic.Code);
            Assert.DoesNotContain(canary, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Validate_AcceptsUnlimitedLiteralExactValues()
        {
            string longValue = new('x', 300);
            const string controlValue = "value\u0001";
            ImmutableArray<string> hosts = Enumerable.Range(1, 65)
                .Select(index => $"host-{index}.example")
                .Append(longValue)
                .Append(string.Empty)
                .Append(controlValue)
                .ToImmutableArray();
            var selection = Selection(
                host: new HostSelection(HostMatchMode.Exact, hosts, hosts),
                statuses: ImmutableArray.Create(longValue, string.Empty, controlValue),
                methods: ImmutableArray.Create(controlValue, longValue, string.Empty));

            OperationResult<CaptureSelectionSpec> result =
                CaptureSelectionValidator.Validate(selection);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            CaptureSelectionSpec value = Assert.IsType<CaptureSelectionSpec>(result.Value);
            ImmutableArray<string> include = Assert.IsType<ImmutableArray<string>>(value.Host.Include);
            Assert.Equal(hosts, include);
            Assert.Equal(hosts, value.Host.Exclude);
            Assert.Equal(selection.Statuses, value.Statuses);
            Assert.Equal(selection.Methods, value.Methods);
        }

        private static ProcessFilterSpec Spec(IEnumerable<string> include, IEnumerable<string> exclude = null) =>
            ProcessFilter.Parse(include, exclude ?? Array.Empty<string>()).Value;
    }
}
