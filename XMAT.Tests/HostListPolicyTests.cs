// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    // Shared host identity, exact list membership and immutable menu operations for the
    // application-owned Block and Bypass lists.
    public class HostListPolicyTests
    {
        public HostListPolicyTests() => Localization.LoadLanguage("en-us");

        // DNS spelling (case and final dot) survives; only IP literals are canonicalized.
        [Theory]
        [InlineData("api.example.com", "api.example.com", null)]
        [InlineData("API.Example.COM:8443", "API.Example.COM", 8443)]
        [InlineData("api.example.test.:443", "api.example.test.", 443)]
        [InlineData("127.0.0.1:80", "127.0.0.1", 80)]
        [InlineData("[::1]", "::1", null)]
        [InlineData("[::1]:443", "::1", 443)]
        [InlineData("[2001:DB8:0:0:0:0:0:1]:8080", "2001:db8::1", 8080)]
        [InlineData("inGameEvents", "inGameEvents", null)]
        [InlineData("example.test:000080", "example.test", 80)]
        [InlineData("[::1]:00443", "::1", 443)]
        public void Authority_KeepsDnsSpellingAndCanonicalIpWithOptionalPort(string authority, string host, int? port)
        {
            Assert.True(HostAuthority.TryParse(authority, out string parsedHost, out int? parsedPort));
            Assert.Equal(host, parsedHost);
            Assert.Equal(port, parsedPort);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("::1")]
        [InlineData("2001:db8::1:443")]
        [InlineData("[::1")]
        [InlineData("[::1]x")]
        [InlineData("[::1]:")]
        [InlineData("[::1]:notaport")]
        [InlineData("[::1]:70000")]
        [InlineData("[example.com]:443")]
        [InlineData("example.com:")]
        [InlineData("example.com:0")]
        [InlineData("example.com:-1")]
        [InlineData("exa]mple.test")]
        [InlineData("exa[mple.test:80")]
        [InlineData(":443")]
        [InlineData("example.test:0000065536")]
        [InlineData("example.test:00000")]
        [InlineData("example.test:99999999999")]
        public void Authority_RejectsMalformedInputWithoutSplittingBareIPv6(string authority)
        {
            Assert.False(HostAuthority.TryParse(authority, out _, out _));
        }

        // Authority structure only: a non-IP host keeps its trimmed spelling, without host-name
        // validation, case folding, percent-decoding or final-dot removal, including legacy
        // non-ASCII spellings that are not RFC 3986 reg-names. Such text is not a host identity.
        [Theory]
        [InlineData(" Reg~Name.Example.Test:8080 ", "Reg~Name.Example.Test", 8080)]
        [InlineData("Sub!Delims$&'()+;=.Example.Test", "Sub!Delims$&'()+;=.Example.Test", null)]
        [InlineData("Pct%2DEncoded.Example.Test:443", "Pct%2DEncoded.Example.Test", 443)]
        [InlineData("Legacy..Example.Test.", "Legacy..Example.Test.", null)]
        [InlineData("B\u00FCcher.Example.Test:8443", "B\u00FCcher.Example.Test", 8443)]
        [InlineData("exa mple.com", "exa mple.com", null)]
        public void Authority_KeepsNonIpSpellingWithoutHostNameValidation(string authority, string host, int? port)
        {
            Assert.True(HostAuthority.TryParse(authority, out string parsedHost, out int? parsedPort));
            Assert.Equal(host, parsedHost);
            Assert.Equal(port, parsedPort);
            Assert.False(HostAuthority.TryNormalizeHost(parsedHost, out _));
        }

        [Theory]
        [InlineData("Api.Example.com", "Api.Example.com")]
        [InlineData(" api.example.test. ", "api.example.test.")]
        [InlineData(" 10.0.0.1 ", "10.0.0.1")]
        [InlineData("::1", "::1")]
        [InlineData("[::1]", "::1")]
        [InlineData("0:0:0:0:0:0:0:1", "::1")]
        [InlineData("2001:DB8::0:1", "2001:db8::1")]
        [InlineData("inGameEvents", "inGameEvents")]
        public void HostIdentity_KeepsDnsSpellingAndCanonicalizesIpLiterals(string host, string canonical)
        {
            Assert.True(HostAuthority.TryNormalizeHost(host, out string normalized));
            Assert.Equal(canonical, normalized);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("example.com:443")]
        [InlineData("[::1]:443")]
        [InlineData("*.example.com")]
        [InlineData("example.com/path")]
        [InlineData("[::1")]
        [InlineData("Reg~Name.Example.Test")]
        [InlineData("Pct%2DEncoded.Example.Test")]
        [InlineData("Legacy..Example.Test.")]
        [InlineData("B\u00FCcher.Example.Test")]
        [InlineData("exa mple.com")]
        public void HostIdentity_RejectsValuesThatAreNotASingleHost(string text)
        {
            Assert.False(HostAuthority.TryNormalizeHost(text, out _));
        }

        [Fact]
        public void BlockList_TreatsIPv6SpellingsAsOneExactEntry()
        {
            var list = new BlockListModel();

            Assert.True(list.Add("[::1]"));
            Assert.False(list.Add("::1"));
            Assert.False(list.Add("0:0:0:0:0:0:0:1"));
            Assert.Equal("::1", Assert.Single(list.BlockedUrls));
            Assert.True(list.Contains("[0::1]"));
            Assert.True(list.IsBlocked("::1"));
            Assert.True(list.IsBlocked("::1", "/any/path"));

            Assert.True(list.Remove("0:0::1"));
            Assert.Empty(list.BlockedUrls);
            Assert.False(list.IsBlocked("::1"));
            Assert.False(list.Remove("::1"));
        }

        [Fact]
        public void BypassList_TreatsIPv6SpellingsAsOneExactEntry()
        {
            var list = new BypassListModel();

            Assert.True(list.Add("0:0:0:0:0:0:0:1"));
            Assert.Equal("::1", Assert.Single(list.BypassedUrls));
            Assert.False(list.Add("[::1]"));
            Assert.True(list.Contains("[0::1]"));
            Assert.True(list.IsBypassed("::1"));

            Assert.True(list.Remove("[::1]"));
            Assert.Empty(list.BypassedUrls);
            Assert.False(list.IsBypassed("::1"));
        }

        // A port attempt is rejected by its shape, whether or not the port itself is valid.
        [Theory]
        [InlineData("example.com:443")]
        [InlineData("10.0.0.1:8080")]
        [InlineData("[::1]:443")]
        [InlineData(" [2001:db8::1]:80 ")]
        [InlineData("example.com:0")]
        [InlineData("example.com:")]
        [InlineData("[::1]:70000")]
        [InlineData("[::1]:notaport")]
        [InlineData("[example.com]:443")]
        [InlineData("[127.0.0.1]:80")]
        public void StandalonePortEntries_AreRejectedWithLocalizedFeedback(string text)
        {
            var block = new BlockListModel();
            var bypass = new BypassListModel();

            Assert.False(block.Add(text));
            Assert.False(bypass.Add(text));
            Assert.Empty(block.BlockedUrls);
            Assert.Empty(bypass.BypassedUrls);
            Assert.False(HostListEntry.TryNormalize(text, out _, out string error));
            Assert.Equal(Localization.GetLocalizedString("HOST_LIST_PORT_NOT_ALLOWED", text.Trim()), error);
            Assert.DoesNotContain("NO_IMPL_", error);
        }

        // Complete bare IPv6 literals are hosts: their final segment is never read as a port.
        // Entries use .NET's canonical address text ("::1:8080" is "::0.1.128.128").
        [Theory]
        [InlineData("2001:db8::1:443", "2001:db8::1:443")]
        [InlineData("::1:8080", "::0.1.128.128")]
        [InlineData("[2001:DB8::443]", "2001:db8::443")]
        public void CompleteIPv6Literals_AreHostEntriesRatherThanPortAttempts(string text, string host)
        {
            var block = new BlockListModel();
            var bypass = new BypassListModel();

            Assert.True(block.Add(text));
            Assert.True(bypass.Add(text));
            Assert.Equal(host, Assert.Single(block.BlockedUrls));
            Assert.Equal(host, Assert.Single(bypass.BypassedUrls));
            Assert.True(block.IsBlocked(host));
            Assert.True(bypass.IsBypassed(host));
            Assert.True(HostListEntry.TryNormalize(text, out _, out string error));
            Assert.Null(error);
        }

        [Theory]
        [InlineData("*.example.com")]
        [InlineData("*.example.com:8080")]
        [InlineData("example.com:8080/api/*")]
        [InlineData("[::1]:*")]
        [InlineData("[example.com]:443/api/*")]
        [InlineData("192.168.1.*")]
        public void WildcardAndPathPatterns_RemainTrimmedOpaqueLiterals(string pattern)
        {
            var block = new BlockListModel();
            var bypass = new BypassListModel();

            Assert.True(block.Add("  " + pattern + "  "));
            Assert.True(bypass.Add(pattern));
            Assert.Equal(pattern, Assert.Single(block.BlockedUrls));
            Assert.Equal(pattern, Assert.Single(bypass.BypassedUrls));
            Assert.True(block.Contains(pattern));
            Assert.True(HostListEntry.TryNormalize(pattern, out string entry, out string error));
            Assert.Equal(pattern, entry);
            Assert.Null(error);
        }

        [Fact]
        public void PortLookingTextInsideAPattern_IsLiteralRatherThanAPortFilter()
        {
            var block = new BlockListModel();
            Assert.True(block.Add("*.example.com:8080"));
            Assert.True(block.Add("example.com:8080/api/*"));
            var bypass = new BypassListModel();
            Assert.True(bypass.Add("*.example.com:8080"));

            Assert.False(block.IsBlocked("api.example.com"));
            Assert.False(block.IsBlocked("example.com", "/api/items"));
            Assert.False(bypass.IsBypassed("api.example.com"));
        }

        // IPv6 hosts are matched in their canonical form without brackets, so IPv6 patterns are
        // written without brackets. A bracketed pattern is still accepted as opaque text, but it
        // never matches a canonical host and has to be rewritten.
        [Fact]
        public void IPv6Patterns_MatchTheCanonicalHostWithoutBrackets()
        {
            var policy = new HostListPolicy();
            Assert.True(policy.BlockList.Add("2001:db8::*"));
            Assert.True(policy.BlockList.Add("::1/api/*"));
            Assert.True(policy.BypassList.Add("2001:db8::*"));

            Assert.True(policy.BlockList.IsBlocked("2001:db8::1"));
            Assert.True(policy.BlockList.IsBlocked("[2001:DB8:0::1]"));
            Assert.False(policy.BlockList.IsBlocked("2001:db9::1"));
            Assert.True(policy.BlockList.IsBlocked("::1", "/api/items"));
            Assert.True(policy.BlockList.IsBlocked("[0:0::1]", "/api/items"));
            Assert.False(policy.BlockList.IsBlocked("::1", "/other"));
            Assert.False(policy.BlockList.IsBlocked("::1"));
            Assert.True(policy.BypassList.IsBypassed("[2001:db8::1]"));
            Assert.Equal(new[] { "2001:db8::*" }, HostListMenu.Create(policy, "[2001:db8::1]").CoveringBlockRules);

            var bracketed = new HostListPolicy();
            Assert.True(bracketed.BlockList.Add("[2001:db8::*]"));
            Assert.True(bracketed.BlockList.Add("[::1]/api/*"));
            Assert.True(bracketed.BypassList.Add("[2001:db8::*]"));
            Assert.Equal(new[] { "[2001:db8::*]", "[::1]/api/*" }, bracketed.BlockList.BlockedUrls);
            Assert.False(bracketed.BlockList.IsBlocked("2001:db8::1"));
            Assert.False(bracketed.BlockList.IsBlocked("[2001:db8::1]"));
            Assert.False(bracketed.BlockList.IsBlocked("::1", "/api/items"));
            Assert.False(bracketed.BypassList.IsBypassed("[2001:db8::1]"));
        }

        [Fact]
        public void DnsEntries_AreCaseInsensitiveWithoutSubdomainExpansion()
        {
            var block = new BlockListModel();

            Assert.True(block.Add("api.example.com"));
            Assert.False(block.Add(" API.EXAMPLE.COM "));
            Assert.Single(block.BlockedUrls);
            Assert.True(block.Contains("Api.Example.Com"));
            Assert.True(block.IsBlocked("API.example.com"));
            Assert.False(block.IsBlocked("sub.api.example.com"));
            Assert.False(block.IsBlocked("example.com"));
        }

        // A final dot is part of the textual host: dotted and dotless wildcards match only their
        // own spelling in both lists and in menu covering-rule notes.
        [Fact]
        public void TrailingDotWildcards_MatchOnlyHostsWithTheSameFinalDot()
        {
            var dotted = new HostListPolicy();
            Assert.True(dotted.BlockList.Add("*.example.test."));
            Assert.True(dotted.BypassList.Add("*.example.test."));
            var dotless = new HostListPolicy();
            Assert.True(dotless.BlockList.Add("*.example.test"));
            Assert.True(dotless.BypassList.Add("*.example.test"));

            Assert.True(dotted.BlockList.IsBlocked("api.example.test."));
            Assert.True(dotted.BlockList.IsBlocked("API.Example.Test."));
            Assert.False(dotted.BlockList.IsBlocked("api.example.test"));
            Assert.True(dotted.BypassList.IsBypassed("api.example.test."));
            Assert.False(dotted.BypassList.IsBypassed("api.example.test"));
            Assert.True(dotless.BlockList.IsBlocked("api.example.test"));
            Assert.False(dotless.BlockList.IsBlocked("api.example.test."));
            Assert.True(dotless.BypassList.IsBypassed("api.example.test"));
            Assert.False(dotless.BypassList.IsBypassed("api.example.test."));

            Assert.Equal(new[] { "*.example.test." }, HostListMenu.Create(dotted, "api.example.test.").CoveringBlockRules);
            Assert.Empty(HostListMenu.Create(dotted, "api.example.test").CoveringBlockRules);
            Assert.Equal(new[] { "*.example.test." }, HostListMenu.Create(dotted, "api.example.test.").CoveringBypassRules);
        }

        [Fact]
        public void TrailingDotPathPatterns_MatchOnlyTheDottedHostAndPath()
        {
            var block = new BlockListModel();
            Assert.True(block.Add("*.example.test./private/*"));

            Assert.True(block.IsBlocked("api.example.test.", "/private/item"));
            Assert.True(block.IsBlocked("API.EXAMPLE.TEST.", "private/item"));
            Assert.False(block.IsBlocked("api.example.test", "/private/item"));
            Assert.False(block.IsBlocked("api.example.test.", "/public/item"));
            Assert.False(block.IsBlocked("api.example.test."));
        }

        [Fact]
        public void BlockList_KeepsDottedAndDotlessEntriesDistinctAndCaseInsensitive()
        {
            var list = new BlockListModel();
            AssertDistinctDottedAndDotlessEntries(
                list.Add, list.Contains, list.Remove, host => list.IsBlocked(host), list.BlockedUrls);
        }

        [Fact]
        public void BypassList_KeepsDottedAndDotlessEntriesDistinctAndCaseInsensitive()
        {
            var list = new BypassListModel();
            AssertDistinctDottedAndDotlessEntries(
                list.Add, list.Contains, list.Remove, list.IsBypassed, list.BypassedUrls);
        }

        // Separating a port from an authority keeps the host's final dot for policy matching.
        [Fact]
        public void AuthorityCandidates_KeepTheFinalDotWhenSeparatingThePort()
        {
            var dotted = new HostListPolicy();
            Assert.True(dotted.BlockList.Add("api.example.test."));
            Assert.True(dotted.BypassList.Add("api.example.test."));
            var dotless = new HostListPolicy();
            Assert.True(dotless.BlockList.Add("api.example.test"));
            Assert.True(dotless.BypassList.Add("api.example.test"));

            Assert.True(dotted.BlockList.IsBlocked("api.example.test.:443"));
            Assert.True(dotted.BypassList.IsBypassed("Api.Example.Test.:443"));
            Assert.False(dotless.BlockList.IsBlocked("api.example.test.:443"));
            Assert.False(dotless.BypassList.IsBypassed("api.example.test.:443"));
            Assert.True(dotless.BlockList.IsBlocked("API.example.test:443"));
        }

        [Fact]
        public void PolicyPairs_HoldStableIndependentLists()
        {
            var first = new HostListPolicy();
            var second = new HostListPolicy();

            Assert.NotNull(first.BlockList);
            Assert.NotNull(first.BypassList);
            Assert.Same(first.BlockList, first.BlockList);
            Assert.NotSame(first.BlockList, second.BlockList);
            Assert.NotSame(first.BypassList, second.BypassList);

            Assert.True(first.BlockList.Add("api.example.com"));
            Assert.False(first.BypassList.Contains("api.example.com"));
            Assert.False(second.BlockList.Contains("api.example.com"));
        }

        [Fact]
        public void Menu_OffersExactAddOrRemoveForEachListIndependently()
        {
            var policy = new HostListPolicy();

            HostListMenu menu = HostListMenu.Create(policy, "API.Example.com");
            Assert.Equal("API.Example.com", menu.Host);
            Assert.Collection(menu.Actions,
                action => AssertAction(action, policy, HostListKind.Block, HostListOperation.Add, "HOST_LIST_ADD_BLOCK", "API.Example.com"),
                action => AssertAction(action, policy, HostListKind.Bypass, HostListOperation.Add, "HOST_LIST_ADD_BYPASS", "API.Example.com"));

            Assert.True(menu.Actions[0].Execute());
            Assert.Equal("API.Example.com", Assert.Single(policy.BlockList.BlockedUrls));
            menu = HostListMenu.Create(policy, "api.example.com");
            Assert.Collection(menu.Actions,
                action => AssertAction(action, policy, HostListKind.Block, HostListOperation.Remove, "HOST_LIST_REMOVE_BLOCK", "api.example.com"),
                action => AssertAction(action, policy, HostListKind.Bypass, HostListOperation.Add, "HOST_LIST_ADD_BYPASS", "api.example.com"));

            // Adding the other list's entry never removes the first one: both are retained and Block wins.
            Assert.True(menu.Actions[1].Execute());
            Assert.True(policy.BlockList.Contains("api.example.com"));
            Assert.True(policy.BypassList.Contains("api.example.com"));
            Assert.False(policy.BlockList.Add("api.example.com"));
        }

        [Fact]
        public void MenuActions_KeepTheirLabelledOperationWhenPolicyChangesBeforeExecution()
        {
            var policy = new HostListPolicy();
            HostListAction add = HostListMenu.Create(policy, "api.example.com").Actions
                .Single(action => action.List == HostListKind.Block);
            Assert.Equal(HostListOperation.Add, add.Operation);

            Assert.True(policy.BlockList.Add("API.example.com"));
            Assert.False(add.Execute());
            Assert.True(policy.BlockList.Contains("api.example.com"));
            Assert.Equal(HostListOperation.Add, add.Operation);

            HostListAction remove = HostListMenu.Create(policy, "api.example.com").Actions
                .Single(action => action.List == HostListKind.Block);
            Assert.Equal(HostListOperation.Remove, remove.Operation);
            Assert.True(policy.BlockList.Remove("api.example.com"));
            Assert.False(remove.Execute());
            Assert.False(policy.BlockList.Contains("api.example.com"));
            Assert.Equal(HostListOperation.Remove, remove.Operation);
        }

        [Fact]
        public void RemovingAnExactEntry_LeavesACoveringWildcardEffectiveAndReportsIt()
        {
            var policy = new HostListPolicy();
            Assert.True(policy.BlockList.Add("*.example.com"));
            Assert.True(policy.BlockList.Add("api.example.com"));
            Assert.True(policy.BlockList.Add("api.example.com/private/*"));

            HostListAction remove = HostListMenu.Create(policy, "api.example.com").Actions
                .Single(action => action.List == HostListKind.Block);
            Assert.Equal(HostListOperation.Remove, remove.Operation);
            Assert.True(remove.Execute());

            Assert.Equal(new[] { "*.example.com", "api.example.com/private/*" }, policy.BlockList.BlockedUrls);
            Assert.True(policy.BlockList.IsBlocked("api.example.com"));
            HostListMenu after = HostListMenu.Create(policy, "api.example.com");
            Assert.Equal(HostListOperation.Add, after.Actions.Single(action => action.List == HostListKind.Block).Operation);
            Assert.Equal(new[] { "*.example.com" }, after.CoveringBlockRules);
            Assert.Empty(after.CoveringBypassRules);
        }

        [Fact]
        public void IPv6MenuTargets_UseCanonicalIdentityForMembership()
        {
            var policy = new HostListPolicy();
            Assert.True(policy.BypassList.Add("[0:0::1]"));

            HostListMenu menu = HostListMenu.Create(policy, "[::1]");

            Assert.Equal("::1", menu.Host);
            HostListAction bypass = menu.Actions.Single(action => action.List == HostListKind.Bypass);
            Assert.Equal(HostListOperation.Remove, bypass.Operation);
            Assert.True(bypass.Execute());
            Assert.Empty(policy.BypassList.BypassedUrls);
        }

        [Fact]
        public void GenuineSingleLabelHostNamedInGameEvents_IsActionable()
        {
            var policy = new HostListPolicy();

            HostListMenu menu = HostListMenu.Create(policy, "inGameEvents");

            Assert.Equal("inGameEvents", menu.Host);
            Assert.True(menu.Actions.Single(action => action.List == HostListKind.Block).Execute());
            Assert.True(policy.BlockList.IsBlocked("inGameEvents"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("[::1")]
        [InlineData("example.com:443")]
        [InlineData("*.example.com")]
        [InlineData("Reg~Name.Example.Test")]
        [InlineData("Legacy..Example.Test.")]
        [InlineData("B\u00FCcher.Example.Test")]
        public void Menu_IsUnavailableWithoutAValidHost(string host)
        {
            Assert.Null(HostListMenu.Create(new HostListPolicy(), host));
        }

        [Theory]
        [InlineData("[2001:DB8::1]:8443", "2001:db8::1", "8443")]
        [InlineData("api.example.com:8080", "api.example.com", "8080")]
        [InlineData("api.example.com", "api.example.com", "")]
        [InlineData("Api.Example.Test.:8443", "Api.Example.Test.", "8443")]
        public void ImportedRows_ParseTheHostAuthorityWithoutSplittingIPv6(string authority, string host, string port)
        {
            var row = new ProxyConnectionModel(
                1, "200", DateTime.UtcNow, TimeSpan.Zero, "https", authority, "GET", "/path",
                string.Empty, Array.Empty<byte>(), string.Empty, Array.Empty<byte>(), "10.0.0.1");

            Assert.Equal(host, row.Host);
            Assert.Equal(port, row.Port);
            Assert.Equal(host, row.ActionableHost);
        }

        // A frame without a Host header is imported with no host to act on, and the frames
        // around it import as usual.
        [Fact]
        public void SazImport_KeepsAFrameWithoutAHostHeaderAndTheFramesAroundIt()
        {
            string path = Path.Combine(AppContext.BaseDirectory, $"xmat-host-list-import-{Guid.NewGuid():N}.saz");
            try
            {
                using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    AddSazRequest(archive, 1, "GET http://api.example.test:8080/first HTTP/1.1\r\nHost: api.example.test:8080\r\n\r\n");
                    AddSazRequest(archive, 2, "GET http://no-host.example.test/missing HTTP/1.0\r\nAccept: */*\r\n\r\n");
                    AddSazRequest(archive, 3, "GET http://Reg~Name.Example.Test/legacy HTTP/1.1\r\nHost: Reg~Name.Example.Test\r\n\r\n");
                    AddSazRequest(archive, 4, "GET http://[2001:db8::1]:8443/last HTTP/1.1\r\nHost: [2001:db8::1]:8443\r\n\r\n");
                }

                List<ProxyConnectionModel> rows = FiddlerSazHandler.ImportSazFile(path, CancellationToken.None)
                    .OrderBy(row => row.Id)
                    .ToList();

                Assert.Equal(new long[] { 1, 2, 3, 4 }, rows.Select(row => row.Id));
                Assert.Equal(
                    new[] { "api.example.test", string.Empty, "Reg~Name.Example.Test", "2001:db8::1" },
                    rows.Select(row => row.Host));
                Assert.Equal(new[] { "8080", string.Empty, string.Empty, "8443" }, rows.Select(row => row.Port));
                Assert.Equal(
                    new[] { "api.example.test", null, null, "2001:db8::1" },
                    rows.Select(row => row.ActionableHost));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void AddSazRequest(ZipArchive archive, int frame, string requestHead)
        {
            ZipArchiveEntry entry = archive.CreateEntry($"raw/{frame}_c.txt");
            using Stream stream = entry.Open();
            stream.Write(Encoding.ASCII.GetBytes(requestHead));
        }

        private static void AssertAction(
            HostListAction action,
            HostListPolicy policy,
            HostListKind list,
            HostListOperation operation,
            string headerKey,
            string host)
        {
            Assert.Same(policy, action.Policy);
            Assert.Equal(list, action.List);
            Assert.Equal(operation, action.Operation);
            Assert.Equal(host, action.Host);
            Assert.Equal(Localization.GetLocalizedString(headerKey, host), action.Header);
            Assert.DoesNotContain("NO_IMPL_", action.Header);
        }

        // Exact entries keep their trimmed spelling; equality ignores case but not a final dot.
        private static void AssertDistinctDottedAndDotlessEntries(
            Func<string, bool> add,
            Func<string, bool> contains,
            Func<string, bool> remove,
            Func<string, bool> matches,
            IReadOnlyList<string> entries)
        {
            Assert.True(add(" Api.Example.Test "));
            Assert.True(add("api.example.test."));
            Assert.False(add("API.EXAMPLE.TEST"));
            Assert.False(add(" API.Example.Test. "));
            Assert.Equal(new[] { "Api.Example.Test", "api.example.test." }, entries);
            Assert.True(contains("api.example.test"));
            Assert.True(contains("API.EXAMPLE.TEST."));
            Assert.True(matches("api.example.test"));
            Assert.True(matches("Api.Example.Test."));

            Assert.True(remove("API.EXAMPLE.TEST."));
            Assert.Equal(new[] { "Api.Example.Test" }, entries);
            Assert.False(contains("api.example.test."));
            Assert.False(matches("api.example.test."));
            Assert.True(contains("api.example.test"));
            Assert.True(matches("API.example.test"));
            Assert.False(remove("api.example.test."));
        }
    }
}
