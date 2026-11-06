// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Text;
using Xunit.Abstractions;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    // The SAZ readers' corrections (FiddlerSazHandler.FromFiddlerFrame, which the GUI's import and the command line's reader
    // share): heads read as Latin-1, a header name with an apostrophe kept, fields collected as WebHeaderCollection did, and
    // timers read as instants whatever the machine's culture.
    public class FiddlerSazReaderCorrectionTests
    {
        private readonly ITestOutputHelper _output;

        public FiddlerSazReaderCorrectionTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Heads_AreReadAsLatin1_KeepingEveryByte()
        {
            ProxyConnectionModel record = Parse(
                "GET http://example.test/caf\u00e9 HTTP/1.1\r\nHost: example.test\r\nX-Latin: caf\u00e9 \u00ff\u0080\r\n\r\n",
                "HTTP/1.1 200 \u00c9t\u00e9\r\nX-Byte: \u00a0\u00fe\r\n\r\nbody",
                Metadata("2026-01-01T00:00:00.0000000Z", "2026-01-01T00:00:01.0000000Z"),
                out int skipped);

            Assert.Equal(0, skipped);
            Assert.Contains("X-Latin: caf\u00e9 \u00ff\u0080\r\n", record.RequestLineAndHeaders, StringComparison.Ordinal);
            Assert.StartsWith("HTTP/1.1 200 \u00c9t\u00e9\r\nX-Byte: \u00a0\u00fe\r\n", record.ResponseLineAndHeaders, StringComparison.Ordinal);
            Assert.Equal("body"u8.ToArray(), record.ResponseBody);
        }

        // A name with an apostrophe is an HTTP token that replay sends; the readers keep it, in a request and a response.
        [Fact]
        public void AHeaderNameWithAnApostrophe_IsKept()
        {
            ProxyConnectionModel record = Parse(
                "GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\nX'Name: request\r\n\r\n",
                "HTTP/1.1 200 OK\r\nX'Name: response\r\nx'name: again\r\n\r\n",
                Metadata("2026-01-01T00:00:00.0000000Z", "2026-01-01T00:00:01.0000000Z"),
                out int skipped);

            Assert.Equal(0, skipped);
            Assert.Equal("GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\nX'Name: request\r\n\r\n", record.RequestLineAndHeaders);
            Assert.Equal("HTTP/1.1 200 OK\r\nX'Name: response,again\r\n\r\n", record.ResponseLineAndHeaders);
            Assert.True(FiddlerSazHandler.IsValidHeaderLine("X'Name: v"));
            Assert.False(FiddlerSazHandler.IsValidHeaderLine("X(Name: v"));
        }

        // SazHeaderFields collects every line that WebHeaderCollection takes as it did: the same text, and the same value for
        // each name, in any case.
        [Fact]
        public void HeaderFields_MatchWebHeaderCollection_ForEveryLineItTakes()
        {
            foreach (string line in CorpusLines())
            {
                if (!TryWebHeaderCollection(new[] { line }, out WebHeaderCollection expected))
                {
                    continue;
                }

                var fields = new SazHeaderFields();
                fields.Add(line);
                AssertSame(expected, fields, line);
            }
        }

        // Heads of several lines: repeated names in different cases, empty values, an empty name and Latin-1 values.
        [Fact]
        public void HeaderFields_MatchWebHeaderCollection_ForWholeHeads()
        {
            string[] alphabet =
            {
                "A: 1", "a: 2", "B:", "b:  ", "C: x,y", "c :bad", ":empty name", ": second", "D:\tcaf\u00e9\t", "d: \u00ff",
                "E: a: b", "Host: example.test", "HOST: other", "F:\u0120g", "x-Long: " + new string('v', 300),
            };
            var random = new Random(0x53415A);
            for (int sample = 0; sample < 5_000; sample++)
            {
                string[] lines = Enumerable.Range(0, random.Next(0, 12)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
                string[] accepted = lines.Where(FiddlerSazHandler.IsValidHeaderLine).ToArray();
                Assert.True(TryWebHeaderCollection(accepted, out WebHeaderCollection expected), string.Join(" | ", accepted));
                var fields = new SazHeaderFields();
                foreach (string line in accepted)
                {
                    fields.Add(line);
                }

                AssertSame(expected, fields, string.Join(" | ", accepted));
            }
        }

        // (a) Begin and end in different offsets: the duration is the time between the two instants.
        [Fact]
        public void Duration_IsTheTimeBetweenTheInstants_WhateverTheirOffsets()
        {
            ProxyConnectionModel record = ParseTimers("2026-03-08T01:30:00.0000000-08:00", "2026-03-08T03:30:00.0000000-07:00");

            Assert.Equal(TimeSpan.FromHours(1), record.Duration);
        }

        // (b) Seven fractional digits read exactly, as do Fiddler's five; (c) the request time is the first instant, in
        // this machine's local time.
        [Theory]
        [InlineData("2026-03-08T09:59:59.1234567Z", "2026-03-08T10:00:01.6234568Z", "2026-03-08T09:59:59.1234567Z", 2.5000001)]
        [InlineData("2015-04-14T15:29:54.69500-07:00", "2015-04-14T15:29:55.02100-07:00", "2015-04-14T22:29:54.6950000Z", 0.326)]
        public void Timers_ReadExactly_AsLocalTimes(string begin, string done, string instant, double seconds)
        {
            ProxyConnectionModel record = ParseTimers(begin, done);

            Assert.Equal(DateTimeKind.Local, record.RequestTime.Kind);
            Assert.Equal(
                DateTime.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                record.RequestTime.ToUniversalTime());
            Assert.Equal(TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)), record.Duration);
        }

        // (d) The machine's culture changes nothing: an ISO time reads the same in any culture, and a time in another form
        // is read with the invariant culture, never with the calendar or order of the machine's (here March 8, not August 3).
        [Theory]
        [InlineData("th-TH")]
        [InlineData("ar-SA")]
        [InlineData("en-GB")]
        public void Timers_AreReadWithTheInvariantCulture(string name)
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                ProxyConnectionModel iso = ParseTimers("2026-03-08T01:30:00.0000000Z", "2026-03-08T01:30:02.0000000Z");
                ProxyConnectionModel other = ParseTimers("03/08/2026 01:30:00 +00:00", "03/08/2026 01:30:02 +00:00");

                foreach (ProxyConnectionModel record in new[] { iso, other })
                {
                    Assert.Equal(new DateTime(2026, 3, 8, 1, 30, 0, DateTimeKind.Utc), record.RequestTime.ToUniversalTime());
                    Assert.Equal(TimeSpan.FromSeconds(2), record.Duration);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        // (e) A time without an offset, as XMAT's own export writes a time of unspecified kind, is this machine's local time.
        [Fact]
        public void ATimeWithoutAnOffset_IsThisMachinesLocalTime()
        {
            ProxyConnectionModel record = ParseTimers("2026-07-04T12:00:00.12345", "2026-07-04T12:00:01.12345");

            Assert.Equal(DateTimeKind.Local, record.RequestTime.Kind);
            Assert.Equal(new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Local).AddTicks(1234500), record.RequestTime);
            Assert.Equal(TimeSpan.FromSeconds(1), record.Duration);
        }

        // Across a transition of this machine's zone, the duration is still the time between the instants, never the
        // difference of the local clock times. A zone without transitions cannot show it, which the output says.
        [Fact]
        public void Duration_AcrossThisMachinesZoneTransition_IsTheTimeBetweenTheInstants()
        {
            DateTimeOffset? transition = NextLocalTransition(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            _output.WriteLine(
                transition == null
                    ? $"Transition assertion did not run: {TimeZoneInfo.Local.Id} has no transition in two years."
                    : $"Transition assertion ran: {TimeZoneInfo.Local.Id} changes its offset at {transition.Value:o}.");
            if (transition == null)
            {
                return;
            }

            DateTimeOffset begin = transition.Value.AddHours(-3);
            DateTimeOffset done = transition.Value.AddHours(3);
            ProxyConnectionModel record = ParseTimers(
                begin.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), done.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

            Assert.Equal(TimeSpan.FromHours(6), record.Duration);
            Assert.NotEqual(TimeSpan.FromHours(6), done.LocalDateTime - begin.LocalDateTime);
        }

        internal static IEnumerable<string> CorpusLines()
        {
            for (int value = 0; value <= char.MaxValue; value++)
            {
                char character = (char)value;
                yield return $"a{character}b:value";
                yield return $"Name:a{character}b";
            }

            foreach (object[] line in FiddlerSazHeaderValidationTests.StructuredHeaderLines())
            {
                yield return (string)line[0];
            }

            char[] alphabet =
            {
                'a', 'Z', '0', ':', ':', ' ', '\t', '\r', '\n', '\v', '\f', '(', ')', '\'', '"', (char)0, (char)127,
                '\u0080', '\u00e9', '\u0100', '\u0109', '\u010a', '\u010d', '\u0120', '\u20ac',
            };
            var random = new Random(0x48454144);
            for (int sample = 0; sample < 100_000; sample++)
            {
                var characters = new char[random.Next(0, 65)];
                for (int index = 0; index < characters.Length; index++)
                {
                    characters[index] = alphabet[random.Next(alphabet.Length)];
                }

                yield return new string(characters);
            }
        }

        private static bool TryWebHeaderCollection(IEnumerable<string> lines, out WebHeaderCollection headers)
        {
            headers = new WebHeaderCollection();
            try
            {
                foreach (string line in lines)
                {
                    headers.Add(line);
                }

                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void AssertSame(WebHeaderCollection expected, SazHeaderFields fields, string context)
        {
            if (expected.ToString() != fields.ToString() || expected.Count != fields.Count)
            {
                Assert.Fail("Head UTF-16: " + Convert.ToHexString(Encoding.Unicode.GetBytes(context ?? "")));
            }

            foreach (string name in expected.AllKeys)
            {
                Assert.Equal(expected[name], fields[name]);
                Assert.Equal(expected[name.ToUpperInvariant()], fields[name.ToUpperInvariant()]);
            }

            Assert.Null(fields["X-Absent-Name"]);
        }

        // The first instant after start at which this machine's zone changes its offset, to the minute; null when it does
        // not change within two years.
        private static DateTimeOffset? NextLocalTransition(DateTimeOffset start)
        {
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(start);
            for (DateTimeOffset hour = start.AddHours(1); hour < start.AddYears(2); hour = hour.AddHours(1))
            {
                if (TimeZoneInfo.Local.GetUtcOffset(hour) != offset)
                {
                    DateTimeOffset minute = hour.AddHours(-1);
                    while (TimeZoneInfo.Local.GetUtcOffset(minute) == offset)
                    {
                        minute = minute.AddMinutes(1);
                    }

                    return minute;
                }
            }

            return null;
        }

        private static ProxyConnectionModel ParseTimers(string begin, string done) =>
            Parse(
                "GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n",
                "HTTP/1.1 200 OK\r\n\r\n",
                Metadata(begin, done),
                out _);

        private static string Metadata(string begin, string done) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Session SID=\"1\" BitFlags=\"0\">" +
            $"<SessionTimers ClientBeginRequest=\"{begin}\" ClientDoneResponse=\"{done}\" />" +
            "<SessionFlags><SessionFlag N=\"x-clientip\" V=\"0.0.0.0\" /></SessionFlags></Session>";

        private static ProxyConnectionModel Parse(string request, string response, string metadata, out int skipped)
        {
            using var requestStream = new MemoryStream(Encoding.Latin1.GetBytes(request), writable: false);
            using var metadataStream = new MemoryStream(Encoding.UTF8.GetBytes(metadata), writable: false);
            using var responseStream = new MemoryStream(Encoding.Latin1.GetBytes(response), writable: false);
            ProxyConnectionModel record = FiddlerSazHandler.FromFiddlerFrame(1, requestStream, metadataStream, responseStream, out skipped);
            Assert.NotNull(record);
            return record;
        }
    }
}
