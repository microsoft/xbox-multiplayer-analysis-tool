// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Processes;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class CaptureSearchTests
    {
        private static readonly ClientProcessInfo Game = new("SampleGame.exe", 4100, null, ClientProcessStatus.Local);
        private static readonly ClientProcessInfo Host = new("svchost.exe", 912, null, ClientProcessStatus.Local);

        private static readonly (string Host, string Path, string Status, string Method, ClientProcessInfo Process)[] Rows =
        {
            ("title.mgt.xboxlive.com", "/titles/123/sessions", "200", "GET", Game),
            ("telemetry.example.com", "/events/batch", "503", "POST", Game),
            ("images.example.com", "/assets/avatar", "429", "GET", Host),
            ("profile.xboxlive.com", "/users/me/profile", "", "PUT", ClientProcessInfo.NotRecorded),
        };

        public CaptureSearchTests() => Localization.LoadLanguage("en-us");

        // The numbers, from 1, of the rows that SEARCH finds.
        private static int[] Found(string search)
        {
            CaptureSearch parsed = CaptureSearch.Parse(search);
            return Rows.Select((row, index) => (row, index))
                .Where(item => parsed.Matches(item.row.Host, item.row.Path, item.row.Status, item.row.Method, item.row.Process))
                .Select(item => item.index + 1)
                .ToArray();
        }

        [Theory]
        // Words are found in the host or the path, whatever their case, and every word must be found.
        [InlineData("xboxlive", new[] { 1, 4 })]
        [InlineData("EVENTS", new[] { 2 })]
        [InlineData("xboxlive profile", new[] { 4 })]
        [InlineData("-xboxlive", new[] { 2, 3 })]
        // Quotes keep a phrase together, and a name XMAT doesn't know is part of a word.
        [InlineData("\"assets/avatar\"", new[] { 3 })]
        [InlineData("\"-xboxlive\"", new int[0])]
        [InlineData("https://images", new int[0])]
        // A status is a code, a class with x for any digit, or none; terms of one name find any of them.
        [InlineData("status:503", new[] { 2 })]
        [InlineData("status:5xx", new[] { 2 })]
        [InlineData("status:4XX status:5xx", new[] { 2, 3 })]
        [InlineData("status:429,503", new[] { 2, 3 })]
        [InlineData("status:none", new[] { 4 })]
        [InlineData("-status:2xx", new[] { 2, 3, 4 })]
        // Terms of different names must all match.
        [InlineData("method:get", new[] { 1, 3 })]
        [InlineData("method:GET -status:2xx", new[] { 3 })]
        // A host without * is found in the host; with *, it matches as the Block List and --host match it.
        [InlineData("host:xboxlive", new[] { 1, 4 })]
        [InlineData("host:*.xboxlive.com", new[] { 1, 4 })]
        [InlineData("host:*.mgt.xboxlive.com", new[] { 1 })]
        [InlineData("-host:*.example.com", new[] { 1, 4 })]
        // A process without * is found in the name; with *, or as an ID, it matches as --process does. A request whose
        // process isn't known is shown only while no process is asked for.
        [InlineData("process:game", new[] { 1, 2 })]
        [InlineData("process:svc*", new[] { 3 })]
        [InlineData("process:912", new[] { 3 })]
        [InlineData("-process:svchost", new[] { 1, 2, 4 })]
        public void Search_FindsWordsInHostsAndPaths_AndTermsAsTheCommandLineNamesThem(string search, int[] found)
        {
            Assert.Equal(found, Found(search));
        }

        [Fact]
        public void TermsThatCantBeUsed_AreIgnoredAndSayWhy_AndTermsBeingTypedAreNeither()
        {
            CaptureSearch search = CaptureSearch.Parse("status:abc process:games/x.exe method:GET");
            Assert.Equal(new[] { "status:abc isn't a status", "process:games/x.exe isn't a process" }, search.Problems);
            Assert.Equal(new[] { 1, 3 }, Found("status:abc method:GET"));

            CaptureSearch typing = CaptureSearch.Parse("status:");
            Assert.Empty(typing.Problems);
            Assert.False(typing.IsActive);
            Assert.False(CaptureSearch.Parse("   ").IsActive);
            Assert.True(CaptureSearch.Parse("-telemetry").IsActive);
        }
    }
}
