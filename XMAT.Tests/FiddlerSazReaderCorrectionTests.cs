// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Text;
using CaptureAnalysisEngine;
using Xunit.Abstractions;
using XMAT.Operations.Analysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.Tests
{
    // The SAZ readers' corrections (FiddlerSazHandler.FromFiddlerFrame, which the GUI's import and the command line's reader
    // share): heads read as Latin-1, a head ended only by a line of ASCII whitespace, a header name with an apostrophe kept,
    // fields collected as WebHeaderCollection did, and timers read as instants whatever the machine's culture, which every
    // writer of the pair of timers keeps as instants.
    public class FiddlerSazReaderCorrectionTests
    {
        private readonly ITestOutputHelper _output;

        public FiddlerSazReaderCorrectionTests(ITestOutputHelper output)
        {
            _output = output;
            Localization.LoadLanguage("en-us");
        }

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

        // A line of Latin-1 spaces, 0x85 or 0xA0 alone or among ASCII whitespace, is not the blank line that ends a head, as
        // when heads were read as ASCII ('?'): it is a malformed line, skipped and counted, the headers after it stay
        // headers, in a request and a response, and each body is exactly the bytes after the blank line.
        [Theory]
        [InlineData("\u0085")]
        [InlineData("\u00a0")]
        [InlineData(" \u0085\t")]
        [InlineData("\u00a0\r\u0085")]
        public void ALineOfLatin1Spaces_DoesNotEndTheHead(string line)
        {
            ProxyConnectionModel record = Parse(
                $"POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\n{line}\r\nX-After: request\r\n\r\nrequest\r\n\u00a0body",
                $"HTTP/1.1 200 OK\r\n{line}\r\nX-After: response\r\n\r\nresponse\r\n\u0085body",
                Metadata("2026-01-01T00:00:00.0000000Z", "2026-01-01T00:00:01.0000000Z"),
                out int skipped);

            Assert.Equal(2, skipped);
            Assert.Equal(
                "POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\nX-After: request\r\n\r\n", record.RequestLineAndHeaders);
            Assert.Equal("HTTP/1.1 200 OK\r\nX-After: response\r\n\r\n", record.ResponseLineAndHeaders);
            Assert.Equal(Encoding.Latin1.GetBytes("request\r\n\u00a0body"), record.RequestBody);
            Assert.Equal(Encoding.Latin1.GetBytes("response\r\n\u0085body"), record.ResponseBody);
        }

        // A line of only ASCII whitespace still ends a head, as it always did: what follows it is the body.
        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("\t \v\f")]
        [InlineData("\r")]
        [InlineData("\n")]
        public void ALineOfAsciiWhitespace_EndsTheHead(string line)
        {
            ProxyConnectionModel record = Parse(
                $"POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\n{line}\r\nX-After: request\r\n\r\n",
                $"HTTP/1.1 200 OK\r\n{line}\r\nX-After: response\r\n\r\n",
                Metadata("2026-01-01T00:00:00.0000000Z", "2026-01-01T00:00:01.0000000Z"),
                out int skipped);

            Assert.Equal(0, skipped);
            Assert.Equal("POST http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n", record.RequestLineAndHeaders);
            Assert.Equal("HTTP/1.1 200 OK\r\n\r\n", record.ResponseLineAndHeaders);
            Assert.Equal("X-After: request\r\n\r\n"u8.ToArray(), record.RequestBody);
            Assert.Equal("X-After: response\r\n\r\n"u8.ToArray(), record.ResponseBody);
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

        // Read, save, read: a request across a transition of this machine's zone keeps its instants and its duration through
        // every writer and reader of the pair of timers (the SAZ export, the sanitized SAZ export, the capture table and its
        // reload, the analysis records and the call that analysis makes of them), which add the duration to the instant and
        // subtract instants, never local clock readings. A request on an ordinary day always runs; a zone without
        // transitions cannot show the rest, which the output says.
        [Fact]
        public void Timers_SurviveReadSaveRead_AcrossThisMachinesZoneTransitions()
        {
            var intervals = new List<(DateTimeOffset Begin, DateTimeOffset Done)>
            {
                (new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 4, 18, 0, 0, TimeSpan.Zero)),
            };
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            for (int count = 0; count < 2 && NextLocalTransition(start) is DateTimeOffset transition; count++)
            {
                intervals.Add((transition.AddHours(-3), transition.AddHours(3)));
                start = transition.AddMinutes(1);
            }

            _output.WriteLine(
                intervals.Count == 1
                    ? $"Transition assertions did not run: {TimeZoneInfo.Local.Id} has no transition in two years."
                    : $"Transition assertions ran: {TimeZoneInfo.Local.Id} changes its offset at " +
                        string.Join(" and ", intervals.Skip(1).Select(interval => Iso(interval.Begin.AddHours(3)))) + ".");
            foreach ((DateTimeOffset begin, DateTimeOffset done) in intervals)
            {
                ProxyConnectionModel read = ParseTimers(Iso(begin), Iso(done), "User-Agent: XboxServicesAPI\r\n");
                AssertTimes(read, begin, done);
                AssertTimes(SaveAndRead(read, sanitized: false), begin, done);
                AssertTimes(SaveAndRead(read, sanitized: true), begin, done);

                object[] row = read.ToDataTableValues("device");
                Assert.Equal(Iso(begin), (string)row[3]);
                Assert.Equal(Iso(done), (string)row[4]);
                var reloaded = new ProxyConnectionModel(1, 1, 1);
                reloaded.UpdateFromDataRecord(new Row(new Dictionary<string, string>
                {
                    [WebServiceCaptureMethod.FieldKey_RequestTimestamp] = (string)row[3],
                    [WebServiceCaptureMethod.FieldKey_ResponseTimestamp] = (string)row[4],
                }));
                AssertTimes(reloaded, begin, done);

                AnalyzeCaptureOperation.AnalysisDataRecord analysis = AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(1, read);
                Assert.Equal(Iso(begin), analysis.Str(WebServiceCaptureMethod.FieldKey_RequestTimestamp));
                Assert.Equal(Iso(done), analysis.Str(WebServiceCaptureMethod.FieldKey_ResponseTimestamp));
                ServiceCallItem call = WebServiceCaptureRecordConverter.Convert(analysis);
                Assert.NotNull(call);
                Assert.Equal((ulong)(done - begin).TotalMilliseconds, call.ElapsedCallTimeMs);
                Assert.Equal((ulong)begin.UtcDateTime.ToFileTimeUtc(), call.ReqTimeUTC);
            }
        }

        // A request time of no kind, such as an unknown DateTime.MinValue, names no instant: the end is the duration added to
        // it as it is, in its kind, and a reload's duration the difference of the two times, as before. Only a zone with
        // offsets can tell the two apart; this machine's spring transition does in Pacific time.
        [Fact]
        public void ARequestTimeOfNoKind_IsAddedToAsItIs()
        {
            var unknown = new ProxyConnectionModel(1, 1, 1);
            Assert.Equal(DateTime.MinValue, unknown.ResponseDoneTime);
            Assert.Equal(DateTimeKind.Unspecified, unknown.ResponseDoneTime.Kind);

            var start = new DateTime(2026, 3, 7, 23, 0, 0, DateTimeKind.Unspecified);
            DateTime end = ProxyConnectionModel.EndOf(start, TimeSpan.FromHours(6));
            Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0), end);
            Assert.Equal(DateTimeKind.Unspecified, end.Kind);
            Assert.Equal(
                TimeSpan.FromHours(6), ProxyConnectionModel.Elapsed(start, new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Local)));
        }

        // A timer that DateTime reads but that names no instant, its UTC outside years 1 to 9999, is no time, as such a value
        // never failed an import before timers were instants: the request time stays unknown and the duration is zero.
        // These have offsets, so every zone reads them alike.
        [Theory]
        [InlineData("0001-01-01T00:00:00.00000+09:00", "2026-01-01T00:00:01.0000000Z", false)]
        [InlineData("2026-01-01T00:00:00.0000000Z", "9999-12-31T23:59:59.99999-08:00", true)]
        [InlineData("0001-01-01T00:00:00.00000+09:00", "9999-12-31T23:59:59.99999-08:00", false)]
        public void ATimerThatNamesNoInstant_IsNoTime(string begin, string done, bool beginIsATime)
        {
            ProxyConnectionModel record = ParseTimers(begin, done);

            if (beginIsATime)
            {
                Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), record.RequestTime.ToUniversalTime());
            }
            else
            {
                Assert.Equal(DateTime.MinValue, record.RequestTime);
                Assert.Equal(DateTimeKind.Unspecified, record.RequestTime.Kind);
            }

            Assert.Equal(TimeSpan.Zero, record.Duration);
        }

        // A time without an offset that this machine's zone cannot hold: DateTime.MinValue east of UTC, which XMAT's export
        // writes for a time it does not know, and the last moment of year 9999 west of it. In UTC every time is an instant,
        // which the output says.
        [Fact]
        public void ATimeWithoutAnOffset_ThatThisZoneCannotHold_IsNoTime()
        {
            string text =
                TimeZoneInfo.Local.GetUtcOffset(DateTime.MinValue) > TimeSpan.Zero ? "0001-01-01T00:00:00.00000" :
                TimeZoneInfo.Local.GetUtcOffset(DateTime.MaxValue) < TimeSpan.Zero ? "9999-12-31T23:59:59.99999" :
                null;
            _output.WriteLine(
                text == null
                    ? $"Assertion did not run: {TimeZoneInfo.Local.Id} holds every time without an offset."
                    : $"Assertion ran: {TimeZoneInfo.Local.Id} cannot hold {text}.");
            if (text == null)
            {
                return;
            }

            ProxyConnectionModel record = ParseTimers(text, text);

            Assert.Equal(DateTime.MinValue, record.RequestTime);
            Assert.Equal(DateTimeKind.Unspecified, record.RequestTime.Kind);
            Assert.Equal(TimeSpan.Zero, record.Duration);
        }

        // XMAT's export writes a time it does not know as DateTime.MinValue without an offset, which reads back as unknown in
        // any zone; an import never fails on it.
        [Fact]
        public void AnUnknownTime_ExportedByXmat_ReadsBackUnknown()
        {
            using var request = new MemoryStream(
                Encoding.Latin1.GetBytes("GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n"), writable: false);
            ProxyConnectionModel unknown = FiddlerSazHandler.FromFiddlerFrame(1, request, null, null, out _);
            Assert.Equal(DateTime.MinValue, unknown.RequestTime);

            ProxyConnectionModel again = SaveAndRead(unknown, sanitized: false);

            Assert.Equal(DateTime.MinValue.Ticks, again.RequestTime.Ticks);
            Assert.Equal(TimeSpan.Zero, again.Duration);
        }

        // Text that is no time at all is still refused, as DateTime.Parse refused it.
        [Theory]
        [InlineData("not a time", "2026-01-01T00:00:00.0000000Z")]
        [InlineData("2026-01-01T00:00:00.0000000Z", "")]
        public void ATimerThatIsNoTimeAtAll_IsRefused(string begin, string done)
        {
            Assert.Throws<FormatException>(() => ParseTimers(begin, done));
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

        private static ProxyConnectionModel ParseTimers(string begin, string done, string requestHeaders = "") =>
            Parse(
                $"GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n{requestHeaders}\r\n",
                "HTTP/1.1 200 OK\r\n\r\n",
                Metadata(begin, done),
                out _);

        private static string Iso(DateTimeOffset instant) => instant.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

        // A request time in local time at begin, a duration from begin to done, and an end at done in local time too.
        private static void AssertTimes(ProxyConnectionModel record, DateTimeOffset begin, DateTimeOffset done)
        {
            Assert.Equal(DateTimeKind.Local, record.RequestTime.Kind);
            Assert.Equal(begin.UtcDateTime, record.RequestTime.ToUniversalTime());
            Assert.Equal(done - begin, record.Duration);
            Assert.Equal(DateTimeKind.Local, record.ResponseDoneTime.Kind);
            Assert.Equal(done.UtcDateTime, record.ResponseDoneTime.ToUniversalTime());
        }

        // The record saved by the SAZ export, or by the sanitized one, and read back by the GUI's import.
        private static ProxyConnectionModel SaveAndRead(ProxyConnectionModel record, bool sanitized)
        {
            string path = Path.Join(Path.GetTempPath(), "xmat-timers-" + Guid.NewGuid().ToString("N") + ".saz");
            try
            {
                if (sanitized)
                {
                    FiddlerSazHandler.CreateSanitizedSazFile(path, new[] { record });
                }
                else
                {
                    FiddlerSazHandler.CreateSazFile(path, new[] { record });
                }

                return Assert.Single(FiddlerSazHandler.ImportSazFileWithDiagnostics(path, CancellationToken.None).Records);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private sealed class Row(Dictionary<string, string> values) : IDataRecord
        {
            public int RowId => 1;

            public long Int(string fieldName) => 1;

            public string Str(string fieldName) => values.GetValueOrDefault(fieldName, string.Empty);
        }

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
