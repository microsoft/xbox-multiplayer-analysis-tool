// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    public class ReplayDestinationTests
    {
        private const int Limit = ReplayDestination.MaxCaptureDestinationChars;

        [Theory]
        [InlineData("http://[fe80::1%12]/")]
        [InlineData("http://[fe80::1%Ethernet]/")]
        [InlineData("http://[fe80::1%unknown-interface]/")]
        [InlineData("http://[fe80::1%2518]/")]
        [InlineData("http://[fe80::1%0]/")]
        [InlineData("http://[fe80::1%12]/items/#?next=#")]
        public void Detector_FindsEveryScopedIpv6Host(string url) =>
            Assert.True(ReplayDestination.IsScopedIpv6(url));

        [Theory]
        [InlineData("http://[fe80::1]/")]
        [InlineData("http://example.test/items/%25?q=%2518")]
        [InlineData("http://[2001:db8::1]/%25?zone=%12")]
        [InlineData("not a url%12")]
        [InlineData("")]
        [InlineData(null)]
        public void Detector_IgnoresEverythingButAParsedIpv6HostZone(string url) =>
            Assert.False(ReplayDestination.IsScopedIpv6(url));

        [Fact]
        public void UriDetector_UsesTheHostRatherThanOtherPercentSigns()
        {
            Assert.True(ReplayDestination.IsScopedIpv6(
                new Uri("https://[fe80::1%25Ethernet]/items/%25?q=%2518")));
            Assert.False(ReplayDestination.IsScopedIpv6(
                new Uri("https://[fe80::1]/items/%25?q=%2518")));
        }

        // What the destination decision relies on Uri for, pinned on the runtime that runs these tests. A target's head,
        // through the first '/', '?' or '#' that ends its authority, has the verdict and host of the whole target, whatever
        // follows it. A '\' there, or a '/' inside the brackets of an IPv6 address, fails both, while '?', '#', '\' and a
        // space inside them belong to the zone. Only spaces, tabs and line breaks before a URL are trimmed. Either slash
        // spelling may follow the scheme, and exactly two slashes must.
        [Theory]
        [InlineData("http://example.test/", "example.test")]
        [InlineData("http://example.test?", "example.test")]
        [InlineData("http://example.test#", "example.test")]
        [InlineData("http://example.test/\\", "example.test")]
        [InlineData("http://example.test\\", null)]
        [InlineData("http://[fe80::1%a?b#c\\d e]/", "fe80::1%a?b#c\\d e")]
        [InlineData("http://[fe80::1%a/", null)]
        [InlineData(" \t\r\nhttp://example.test/", "example.test")]
        [InlineData("\u000Bhttp://example.test/", null)]
        [InlineData("\u00A0http://example.test/", null)]
        [InlineData("\u0001http://example.test/", null)]
        [InlineData("HTTP:\\\\Example.TEST/", "example.test")]
        [InlineData("http:\\/example.test/", "example.test")]
        [InlineData("https:/\\example.test/", "example.test")]
        [InlineData("http:\\\\example.test\\", null)]
        [InlineData("http:/example.test/", null)]
        [InlineData("http:example.test/", null)]
        [InlineData("http:///example.test/", null)]
        public void TheUriFacts_ThatTheDecisionReadsAHeadBy(string head, string host)
        {
            foreach (string tail in new[] { "", "p", "q?r#s", "\\p", "]", "\uD800", "\u0000", "%zz", " \t", new string('\u00e9', 64) })
            {
                Assert.Equal(host != null, Uri.TryCreate(head + tail, UriKind.Absolute, out Uri uri));
                Assert.Equal(host, uri?.DnsSafeHost);
            }
        }

        // Under its limit, the decision gives every captured request the URL that its whole target gave it before
        // (FullTargetUrl, the former CaptureUrl), or refuses it as that did. That holds whatever the target's leading
        // whitespace, scheme case, slash spelling, user information, port, IPv6 zone, terminator and tail, and whatever
        // stored authority a target that is no URL falls back to: one that makes a URL, a scoped one, or none. History's
        // status and the scoped check follow that URL too.
        [Fact]
        public void UnderItsLimit_TheDecisionGivesEveryTargetTheUrlOfItsWholeTarget()
        {
            var mismatches = new List<string>();
            int compared = 0;
            foreach (string target in DifferentialTargets())
            {
                foreach ((string host, string port) in new[]
                {
                    ("example.test", "80"),
                    ("fe80::1%12", "8080"),
                    ("bad\u0007host", "x"),
                })
                {
                    ProxyConnectionModel capture = Capture(target, host, port);
                    string expected = Outcome(() => FullTargetUrl(capture));
                    string actual = Outcome(() => RequestReplayDraft.CaptureUrl(capture));
                    string expectedUse = UseOf(expected);
                    string actualUse =
                        ReplayDestination.ClassifyCaptureDestination(capture) + " " + ReplayDestination.IsScopedIpv6(capture);
                    compared++;
                    if (expected != actual || expectedUse != actualUse)
                    {
                        mismatches.Add(
                            $"[{Escape(target)} over {Escape(host)}:{port}] expected {Escape(expected)} ({expectedUse}), " +
                            $"got {Escape(actual)} ({actualUse})");
                    }
                }
            }

            Assert.True(compared > 80_000, $"Only {compared} combinations were compared.");
            Assert.True(
                mismatches.Count == 0,
                $"{mismatches.Count} of {compared} differ: {string.Join("; ", mismatches.Take(10))}");
        }

        // A head of MaxCaptureDestinationChars code units is read, whichever part of it is long: the leading whitespace, the
        // user information, a host, a port or an IPv6 zone, and also a target that ends without a terminator. One code unit
        // more makes the destination malformed for every use. It is never a reason to fall back to the stored authority,
        // which here would give another destination.
        [Theory]
        [InlineData("leading whitespace")]
        [InlineData("user information")]
        [InlineData("host")]
        [InlineData("port")]
        [InlineData("zone")]
        [InlineData("unterminated")]
        public void AHeadAtTheLimitIsRead_AndALongerOneIsMalformedWithoutAFallback(string part)
        {
            bool scoped = part == "zone";
            foreach (int length in new[] { Limit - 1, Limit, Limit + 1 })
            {
                (string target, string host) = part switch
                {
                    "leading whitespace" => (new string(' ', length - 20) + "http://example.test/p", "example.test"),
                    "user information" => ("http://" + new string('u', length - 21) + "@example.test/p", "example.test"),
                    "host" => ("http://" + ReplayCaptures.LongDnsName(length - 8) + "/p", ReplayCaptures.LongDnsName(length - 8)),
                    "port" => ("http://example.test:" + new string('0', length - 23) + "80/p", "example.test"),
                    "zone" => ("http://[fe80::1%" + new string('z', length - 18) + "]/p", "fe80::1%" + new string('z', length - 18)),
                    _ => ("http://" + new string('u', length - 20) + "@example.test", "example.test"),
                };
                ProxyConnectionModel capture = scoped ? Capture(target, "example.test", "80") : Capture(target, "fe80::1%12", "8080");
                CaptureDestination destination = ReplayDestination.ReadCaptureDestination(capture);
                if (length > Limit)
                {
                    AssertOverLimit(capture, destination);
                    continue;
                }

                Assert.Equal(CaptureDestinationKind.Absolute, destination.Kind);
                Assert.Equal(host, destination.Authority.DnsSafeHost);
                Assert.Equal(host, new Uri(RequestReplayDraft.CaptureUrl(capture)).DnsSafeHost);
                Assert.Equal(scoped, ReplayDestination.IsScopedIpv6(capture));
                Assert.Equal(
                    scoped ? ReplayDestination.ScopedIpv6Code : null,
                    ReplayOperation.CheckCaptureDestinations(new[] { capture })?.Code);
                Assert.Equal(
                    scoped ? ReplayCaptureDestinationStatus.ScopedIpv6 : ReplayCaptureDestinationStatus.Loadable,
                    ReplayDestination.ClassifyCaptureDestination(capture));
            }
        }

        // Leading whitespace counts toward the limit. Whitespace that reaches it makes the destination malformed, whatever
        // follows, as does a scheme that the limit cuts, since the decision never reads beyond its limit to tell. Whitespace
        // alone, or a path after whitespace within the limit, is no absolute URL and follows the stored authority.
        [Fact]
        public void LeadingWhitespace_CountsTowardTheLimit()
        {
            Assert.Equal(CaptureDestinationKind.Structured, Read(new string(' ', Limit - 1) + "/p").Kind);
            Assert.Equal(CaptureDestinationKind.Structured, Read(new string(' ', Limit)).Kind);
            Assert.Equal(CaptureDestination.OverLimit, Read(new string(' ', Limit) + "/p"));
            Assert.Equal(CaptureDestination.OverLimit, Read(new string(' ', Limit - 1) + "http://example.test/p"));
            Assert.Equal(CaptureDestination.OverLimit, Read(new string(' ', Limit - 1) + "hxyz"));
        }

        // The head ends at its authority's first terminator, however long the rest of the target: a '/', '?', '#' or '\'
        // outside brackets, and a '/' inside them, where Uri fails the target, so that it follows the stored authority. What
        // comes after it, here more than the limit, is never read.
        [Theory]
        [InlineData("http://example.test/", nameof(CaptureDestinationKind.Absolute), "example.test")]
        [InlineData("http://example.test?", nameof(CaptureDestinationKind.Absolute), "example.test")]
        [InlineData("http://example.test#", nameof(CaptureDestinationKind.Absolute), "example.test")]
        [InlineData("http:\\\\example.test/", nameof(CaptureDestinationKind.Absolute), "example.test")]
        [InlineData("http://example.test\\", nameof(CaptureDestinationKind.Structured), "stored.test")]
        [InlineData("http://[fe80::1%a?b#c\\d e]/", nameof(CaptureDestinationKind.Absolute), "fe80::1%a?b#c\\d e")]
        [InlineData("http://[fe80::1%a/", nameof(CaptureDestinationKind.Structured), "stored.test")]
        public void TheHeadEndsAtItsAuthoritysFirstTerminator(string head, string kind, string host)
        {
            CaptureDestination destination = ReplayDestination.ReadCaptureDestination(
                Capture(head + new string('x', Limit + 1), "stored.test", "80"));

            Assert.Equal(Enum.Parse<CaptureDestinationKind>(kind), destination.Kind);
            Assert.Equal(host, destination.Authority.DnsSafeHost);
        }

        // The stored host and port are read, up to the limit each, when the target is no absolute URL. A longer one, like a
        // longer stored scheme, makes the destination malformed before anything parses it.
        [Theory]
        [InlineData("host")]
        [InlineData("port")]
        public void AStoredFieldAtTheLimitIsRead_AndALongerOneIsMalformed(string field)
        {
            foreach (int length in new[] { Limit, Limit + 1 })
            {
                string host = field == "host" ? ReplayCaptures.LongDnsName(length) : "example.test";
                ProxyConnectionModel capture = Capture(
                    "/p", host, field == "port" ? new string('0', length - 2) + "80" : "");
                CaptureDestination destination = ReplayDestination.ReadCaptureDestination(capture);
                if (length > Limit)
                {
                    AssertOverLimit(capture, destination);
                    continue;
                }

                Assert.Equal(CaptureDestinationKind.Structured, destination.Kind);
                Assert.Equal("http://" + host + "/p", RequestReplayDraft.CaptureUrl(capture));
                Assert.Null(ReplayOperation.CheckCaptureDestinations(new[] { capture }));
                Assert.Equal(ReplayCaptureDestinationStatus.Loadable, ReplayDestination.ClassifyCaptureDestination(capture));
            }

            Assert.Equal(
                CaptureDestination.OverLimit,
                ReplayDestination.ReadCaptureDestination(Capture("/p", "example.test", "80", new string('h', Limit + 1))));
        }

        // An absolute target wins, and the stored fields that it leaves unused are never read: however long they are, they
        // do not make its destination malformed.
        [Fact]
        public void StoredFieldsThatAnAbsoluteTargetLeavesUnused_AreNotRead()
        {
            string huge = new('a', 4 * 1024 * 1024);
            ProxyConnectionModel capture = Capture("http://example.test/p", huge, huge);

            Assert.Equal(CaptureDestinationKind.Absolute, ReplayDestination.ReadCaptureDestination(capture).Kind);
            Assert.Equal("http://example.test/p", RequestReplayDraft.CaptureUrl(capture));
            Assert.Null(ReplayOperation.CheckCaptureDestinations(new[] { capture }));
            Assert.Equal(ReplayCaptureDestinationStatus.Loadable, ReplayDestination.ClassifyCaptureDestination(capture));
        }

        // The decision costs what its head and stored fields cost, never what the rest of a target costs. Reissue's
        // admission, History's status and the scoped check allocate a few kilobytes per row, measured apart from the rows'
        // making: rows whose path, query or fragment is megabytes of non-ASCII or escaped text, and rows whose leading
        // whitespace, user information, host, port, zone or stored field runs megabytes past the limit, unread.
        [Fact]
        public void TheDecisionsCost_DoesNotGrowWithTheTextThatItDoesNotRead()
        {
            const int Huge = 4 * 1024 * 1024;
            string nonAscii = new('\u00e9', Huge);
            string escaped = string.Concat(Enumerable.Repeat("%E9", Huge / 3));
            string zeros = new('0', Huge);
            var rows = new (ProxyConnectionModel Capture, CaptureDestinationKind Kind)[]
            {
                (Capture("http://example.test/" + nonAscii, "example.test", "80"), CaptureDestinationKind.Absolute),
                (Capture("http://example.test?" + escaped, "example.test", "80"), CaptureDestinationKind.Absolute),
                (Capture("http://[fe80::1%12]#" + nonAscii, "example.test", "80"), CaptureDestinationKind.Absolute),
                (Capture("http://example.test/p", nonAscii, zeros), CaptureDestinationKind.Absolute),
                (Capture("/" + nonAscii, "example.test", "80"), CaptureDestinationKind.Structured),
                (Capture("http://example.test\\" + escaped, "example.test", "80"), CaptureDestinationKind.Structured),
                (Capture(new string(' ', Huge) + "http://example.test/", "example.test", "80"), CaptureDestinationKind.Malformed),
                (Capture("http://" + nonAscii + "@example.test/", "example.test", "80"), CaptureDestinationKind.Malformed),
                (Capture("http://" + nonAscii + "/", "example.test", "80"), CaptureDestinationKind.Malformed),
                (Capture("http://[fe80::1%" + nonAscii + "]/", "example.test", "80"), CaptureDestinationKind.Malformed),
                (Capture("http://example.test:" + zeros + "80/", "example.test", "80"), CaptureDestinationKind.Malformed),
                (Capture("/" + nonAscii, nonAscii, "80"), CaptureDestinationKind.Malformed),
                (Capture("/" + nonAscii, "example.test", zeros), CaptureDestinationKind.Malformed),
            };

            foreach ((ProxyConnectionModel capture, CaptureDestinationKind kind) in rows)
            {
                ProxyConnectionModel[] selection = { capture };
                UseEveryWay(selection);
                long before = GC.GetAllocatedBytesForCurrentThread();
                CaptureDestination destination = UseEveryWay(selection);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.Equal(kind, destination.Kind);
                Assert.True(allocated < 16 * 1024, $"Using a {kind} destination allocated {allocated} bytes.");
            }
        }

        private static CaptureDestination UseEveryWay(ProxyConnectionModel[] selection)
        {
            _ = ReplayOperation.CheckCaptureDestinations(selection);
            _ = ReplayDestination.ClassifyCaptureDestination(selection[0]);
            _ = ReplayDestination.IsScopedIpv6(selection[0]);
            return ReplayDestination.ReadCaptureDestination(selection[0]);
        }

        // A destination beyond the limit is malformed for every use, as the limit's own refusal.
        private static void AssertOverLimit(ProxyConnectionModel capture, CaptureDestination destination)
        {
            Assert.Equal(CaptureDestination.OverLimit, destination);
            Assert.False(ReplayDestination.IsScopedIpv6(capture));
            Assert.Equal(
                ReplayOperation.RequestMalformedCode,
                ReplayOperation.CheckCaptureDestinations(new[] { capture })?.Code);
            Assert.Equal(ReplayCaptureDestinationStatus.Unreadable, ReplayDestination.ClassifyCaptureDestination(capture));
            Assert.Equal(
                RequestReplayDraft.MalformedDestinationMessage,
                Assert.Throws<UriFormatException>(() => RequestReplayDraft.CaptureUrl(capture)).Message);
        }

        private static CaptureDestination Read(string target) =>
            ReplayDestination.ReadCaptureDestination(Capture(target, "example.test", "80"));

        // The former RequestReplayDraft.CaptureUrl, which parsed the whole target: an absolute http or https target won, and
        // any other target followed the stored authority as a path.
        private static string FullTargetUrl(ProxyConnectionModel capture)
        {
            string path = capture.Path ?? string.Empty;
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            {
                return ReplayDestination.AbsoluteUriKeepingZone(absolute);
            }

            return RequestReplayDraft.CaptureAuthority(capture) + (path.StartsWith('/') ? path : "/" + path);
        }

        private static string Outcome(Func<string> url)
        {
            try
            {
                return url();
            }
            catch (UriFormatException exception)
            {
                return "refused: " + exception.Message;
            }
        }

        // History's status and the scoped check that a URL gives, or its refusal.
        private static string UseOf(string outcome)
        {
            if (!Uri.TryCreate(outcome, UriKind.Absolute, out Uri url))
            {
                return ReplayCaptureDestinationStatus.Unreadable + " " + false;
            }

            bool scoped = ReplayDestination.IsScopedIpv6(url);
            ReplayCaptureDestinationStatus status = scoped
                ? ReplayCaptureDestinationStatus.ScopedIpv6
                : ReplayAuthority.HasIdnHost(url) ? ReplayCaptureDestinationStatus.Loadable : ReplayCaptureDestinationStatus.Unreadable;
            return status + " " + scoped;
        }

        // Targets of every head that a capture can store: absolute http and https ones in every leading whitespace, scheme
        // case, slash spelling, authority, terminator and tail, and targets that are no such URL.
        private static IEnumerable<string> DifferentialTargets()
        {
            string[] leads = { "", " ", "\t\r\n ", "\u000B" };
            string[] schemes = { "http:", "HTTPS:", "hTtP:" };
            string[] slashes = { "//", "\\\\", "/\\", "\\/", "/", "", "///" };
            string[] authorities =
            {
                "example.test", "Example.TEST.", "user:pw@example.test", "@example.test", ":@example.test",
                "example.test:8080", "example.test:", "example.test:080", "example.test:65536", "example.test:8a",
                "[fe80::1%12]", "[fe80::1%25Eth]:8080", "[fe80::1%a?b#c\\d e]", "[fe80::1%a/b]", "[fe80::1%12",
                "[fe80::1%12]x", "[::1]", "@[fe80::1%12]", "[v1.x]", "ex ample.test", "ex\u00e4mple.test", "127.0.0.1",
                "0x7f.1", "", "%41.test", "a[b]c?d", "]x[",
            };
            string[] tails = { "", "/", "/p", "?q", "#f", "\\p", "/\\p", "/%zz", "/\uD800", "?\u0000", "#\uDC00x", " ", "?#[/" };
            foreach (string lead in leads)
            {
                foreach (string scheme in schemes)
                {
                    foreach (string slash in slashes)
                    {
                        foreach (string authority in authorities)
                        {
                            foreach (string tail in tails)
                            {
                                yield return lead + scheme + slash + authority + tail;
                            }
                        }
                    }
                }
            }

            foreach (string other in new[]
            {
                "/p", "p", "", "*", "?q", "//example.test/p", "\\\\example.test\\p", "C:\\p", "file:///c:/p",
                "ftp://example.test/p", "httpx://example.test/p", "https2://example.test/p", "ws://example.test/p",
                "\u00A0http://example.test/p", "http", "http:", "https:/", "h", "httpss://example.test/p",
            })
            {
                yield return other;
            }
        }

        private static string Escape(string text) =>
            string.Concat(text.Select(c => c < ' ' || c >= '\u007F' ? $"\\u{(int)c:X4}" : c.ToString()));

        // A captured row with this target and stored scheme, host and port, set as a capture file stores them, without the
        // parsing of a host-and-port string.
        private static ProxyConnectionModel Capture(string path, string host, string port, string scheme = "http")
        {
            var capture = new ProxyConnectionModel(
                1, "200", DateTime.UnixEpoch, TimeSpan.Zero, scheme, "example.test:80", "GET", path,
                "GET /p HTTP/1.1\r\n\r\n", Array.Empty<byte>(), "HTTP/1.1 200 OK\r\n\r\n", Array.Empty<byte>(), "127.0.0.1");
            typeof(ProxyConnectionModel).GetProperty(nameof(ProxyConnectionModel.Host)).SetValue(capture, host);
            typeof(ProxyConnectionModel).GetProperty(nameof(ProxyConnectionModel.Port)).SetValue(capture, port);
            return capture;
        }
    }
}
