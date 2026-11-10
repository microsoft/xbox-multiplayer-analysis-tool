// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Xunit.Abstractions;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.CaptureTimelineTestData;

namespace XMAT.Tests
{
    public class CaptureTimelineLayoutTests
    {
        private readonly ITestOutputHelper _output;

        public CaptureTimelineLayoutTests(ITestOutputHelper output) => _output = output;

        // Across a transition of this machine's zone, the timeline orders, places and measures requests by the time that
        // passed, never by the local clock: a request an hour after the transition starts four hours after one that began
        // three hours before it, and the span is the six hours that the first one took. In the hour that the clock repeats, a
        // request in its second pass comes after one in its first, also when both read the same on the clock, and so does its
        // host; moving a request from one pass to the other moves it. A zone without transitions cannot show it, which the
        // output says.
        [Fact]
        public void AcrossThisMachinesTransitions_RequestsArePlacedByTheTimeThatPassed()
        {
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var ran = new List<string>();
            for (int count = 0; count < 2 && FiddlerSazReaderCorrectionTests.NextLocalTransition(start) is DateTimeOffset transition;
                count++)
            {
                start = transition.AddMinutes(1);
                ran.Add(transition.ToString("o"));
                var early = CreateBetween(1, transition.AddHours(-3), transition.AddHours(3), "early.test");
                var late = CreateBetween(2, transition.AddHours(1), transition.AddHours(1.5), "late.test");
                var layout = new CaptureTimelineLayout();
                layout.Reset([late, early]);
                var rows = Rows(layout, true, 0, 100);
                Assert.Equal(new[] { early, late }, rows.Select(row => row.Model));
                Assert.Equal(early.RequestTime, layout.Origin);
                Assert.Equal(TimeSpan.FromHours(6).TotalMilliseconds, layout.SpanMilliseconds);
                Assert.Equal(TimeSpan.FromHours(4).TotalMilliseconds * layout.Scale, layout.GetBarBounds(rows[1]).Left, 3);

                // Times of no kind name no instant, and are placed as they read: an hour either side of the transition, as
                // the clock reads them, they are not two hours apart.
                DateTime before = DateTime.SpecifyKind(transition.AddHours(-1).LocalDateTime, DateTimeKind.Unspecified);
                DateTime after = DateTime.SpecifyKind(transition.AddHours(1).LocalDateTime, DateTimeKind.Unspecified);
                Assert.NotEqual(TimeSpan.FromHours(2), after - before);
                var read = Create(7, before, "read.test", TimeSpan.FromMinutes(1));
                var readLater = Create(8, after, "read.test", TimeSpan.FromMinutes(1));
                layout.Reset([readLater, read]);
                rows = Rows(layout, true, 0, 100);
                Assert.Equal(new[] { read, readLater }, rows.Select(row => row.Model));
                Assert.Equal((after - before).TotalMilliseconds * layout.Scale, layout.GetBarBounds(rows[1]).Left, 3);

                TimeSpan back = TimeZoneInfo.Local.GetUtcOffset(transition.AddMinutes(-1)) -
                    TimeZoneInfo.Local.GetUtcOffset(transition.AddMinutes(1));
                if (back <= TimeSpan.Zero)
                {
                    continue;
                }

                TimeSpan minute = TimeSpan.FromMinutes(1);
                var firstPass = CreateBetween(3, transition - (back / 2), transition - (back / 2) + minute, "same.test");
                var between = CreateBetween(4, transition, transition + minute, "other.test");
                var secondPass = CreateBetween(5, transition + (back / 2), transition + (back / 2) + minute, "same.test");
                Assert.Equal(firstPass.RequestTime, secondPass.RequestTime);
                layout.Reset([secondPass, between]);
                Assert.Equal(new[] { "other.test", "same.test" }, Hosts(layout));
                layout.Add(firstPass);
                Assert.Equal(new[] { firstPass, between, secondPass }, Rows(layout, true, 0, 100).Select(row => row.Model));
                Assert.Equal(new[] { "same.test", "other.test" }, Hosts(layout));
                layout.Remove(firstPass);
                Assert.Equal(new[] { "other.test", "same.test" }, Hosts(layout));

                var moved = CreateBetween(6, transition - (back / 2), transition - (back / 2) + minute, "moved.test");
                layout.Reset([moved, between]);
                UpdateBetween(moved, transition + (back / 2), transition + (back / 2) + minute, "moved.test");
                Assert.True(layout.Update(moved));
                Assert.Equal(new[] { between, moved }, Rows(layout, true, 0, 100).Select(row => row.Model));
            }

            _output.WriteLine(
                ran.Count == 0
                    ? $"Assertions did not run: {TimeZoneInfo.Local.Id} has no transition in two years."
                    : $"Assertions ran: {TimeZoneInfo.Local.Id} changes its offset at {string.Join(" and ", ran)}.");
        }
        [Fact]
        public void EmptyLayout_HasNoRowsOrTicks()
        {
            var layout = new CaptureTimelineLayout();
            var rows = Rows(layout, true, 0, 100);
            var ticks = new List<CaptureTimelineLayout.Tick>();
            layout.GetVisibleTicks(0, 800, ticks);

            Assert.Empty(rows);
            Assert.Empty(ticks);
            Assert.Equal(200, layout.Width);
            Assert.Equal(0, layout.GetHeight(false));
        }

        [Fact]
        public void Waterfall_OrdersOutOfOrderAndTiedRequestsWithoutUsingRequestNumberAsIdentity()
        {
            var later = Create(1, Origin.AddSeconds(2));
            var first = Create(1, Origin);
            var tied = Create(2, Origin);
            var layout = new CaptureTimelineLayout();
            layout.Reset([later, first, tied]);

            Assert.Equal(new[] { first, tied, later }, Rows(layout, true, 0, 100).Select(row => row.Model));
            Assert.Equal(3, layout.Count);
            Assert.Equal(Origin, layout.Origin);
        }

        [Fact]
        public void Update_ChangesBoundsAppearanceHostAndOrdering()
        {
            var first = Create(1, Origin, "first.test", TimeSpan.FromSeconds(20));
            var second = Create(2, Origin.AddSeconds(2), "second.test");
            var layout = new CaptureTimelineLayout();
            layout.Reset([first, second]);
            Assert.Equal(20000, layout.SpanMilliseconds);

            Update(first, Origin.AddSeconds(5), "third.test", TimeSpan.Zero, "503");
            Assert.True(layout.Update(first));

            var rows = Rows(layout, true, 0, 100);
            Assert.Equal(new[] { second, first }, rows.Select(row => row.Model));
            Assert.Equal(second.RequestTime, layout.Origin);
            Assert.Equal(3000, layout.SpanMilliseconds);
            Assert.Equal(3, layout.GetBarBounds(rows[1]).Width);
            Assert.Equal("third.test", Rows(layout, false, 0, 200).Last(row => row.Model == null).Host);
            Assert.False(layout.Update(first));
        }

        [Fact]
        public void DurationOnlyUpdate_RecalculatesMaximumWhenLongestResponseShrinks()
        {
            var first = Create(1, Origin, duration: TimeSpan.FromSeconds(50));
            var second = Create(2, Origin.AddSeconds(10), duration: TimeSpan.FromSeconds(5));
            var layout = new CaptureTimelineLayout();
            layout.Reset([first, second]);
            Update(first, duration: TimeSpan.FromSeconds(1));

            Assert.True(layout.Update(first));
            Assert.Equal(15000, layout.SpanMilliseconds);
            Assert.Equal(new[] { first, second }, Rows(layout, true, 0, 100).Select(row => row.Model));
        }

        [Fact]
        public void RemoveAndReset_ReleaseRowsGroupsAndBounds()
        {
            var first = Create(1, Origin, "first.test");
            var second = Create(2, Origin.AddDays(1), "second.test");
            var layout = new CaptureTimelineLayout();
            layout.Reset([first, second]);
            layout.Remove(first);

            Assert.Equal(second.RequestTime, layout.Origin);
            Assert.Single(Rows(layout, false, 0, 100), row => row.Model != null);
            layout.Reset([]);
            Assert.Equal(0, layout.Count);
            Assert.Equal(0, layout.GetHeight(false));
            Assert.Empty(Rows(layout, false, 0, 100));
        }

        [Fact]
        public void MissingTimestamp_CanBecomeValidAndReturnToInvalid()
        {
            var model = Create(start: DateTime.MinValue);
            var layout = new CaptureTimelineLayout();
            layout.Add(model);
            Assert.Equal(0, layout.Count);

            Update(model, Origin);
            Assert.True(layout.Update(model));
            Assert.Equal(1, layout.Count);
            Update(model, DateTime.MinValue);
            Assert.True(layout.Update(model));
            Assert.Equal(0, layout.Count);
        }

        // A request is placed from the first FILETIME, 1601, as analysis places it: one whose time is unknown is not, however
        // the capture table keeps that time, as it does now or as a UTC in the year 1, as XMAT kept it before, which reads
        // back as a known time later in the year 1 east of UTC, and neither is any other request before 1601. A time without
        // an offset is placed as analysis places it, as UTC.
        [Theory]
        [InlineData("0001-01-01T00:00:00.0000000", false)]
        [InlineData("0001-01-01T00:00:00.0000000Z", false)]
        [InlineData("0001-01-01T08:00:00.0000000Z", false)]
        [InlineData("1600-12-31T23:59:59.9999999Z", false)]
        [InlineData("1600-12-31T23:59:59.9999999", false)]
        [InlineData("1601-01-01T00:00:00.0000000Z", true)]
        [InlineData("1601-01-01T00:00:00.0000000", true)]
        public void ARequestBeforeTheFirstFileTime_IsNotPlaced(string stored, bool placed)
        {
            var model = Create();
            var layout = new CaptureTimelineLayout();
            layout.Add(model);
            Assert.Equal(1, layout.Count);

            UpdateStored(model, stored, stored);
            layout.Update(model);

            Assert.Equal(placed ? 1 : 0, layout.Count);
        }

        [Fact]
        public void ByHost_TracksGroupOrderAndOffsetsAcrossInsertionAndRemoval()
        {
            var a = Create(1, Origin.AddSeconds(5), "a.test");
            var b = Create(2, Origin.AddSeconds(10), "b.test");
            var earlierB = Create(3, Origin, "b.test");
            var layout = new CaptureTimelineLayout();
            layout.Reset([a, b]);
            layout.Add(earlierB);

            var rows = Rows(layout, false, 0, 200);
            Assert.Equal(new[] { "b.test", "a.test" }, rows.Where(row => row.Model == null).Select(row => row.Host));
            Assert.Equal(new[] { earlierB, b, a }, rows.Where(row => row.Model != null).Select(row => row.Model));
            Assert.Equal(54, rows.Last(row => row.Model == null).Top);
            Assert.Equal(92, layout.GetHeight(false));

            layout.Remove(earlierB);
            Assert.Equal(new[] { "a.test", "b.test" },
                Rows(layout, false, 0, 200).Where(row => row.Model == null).Select(row => row.Host));
            Assert.Equal(76, layout.GetHeight(false));
        }

        [Fact]
        public void ByHost_PrefixHeightsHandleAnAppendToAnEarlierGroup()
        {
            var models = Enumerable.Range(0, 1000)
                .Select(index => Create(index + 1, Origin.AddSeconds(index), $"host-{index}")).ToList();
            var layout = new CaptureTimelineLayout();
            layout.Reset(models);
            var previousHeight = layout.GetHeight(false);
            layout.Add(Create(1001, Origin.AddHours(1), "host-0"));

            Assert.Equal(previousHeight + 16, layout.GetHeight(false));
            Assert.Contains(Rows(layout, false, layout.GetHeight(false) - 20, 20),
                row => row.Model == models[^1]);
            layout.Remove(models[500]);
            Assert.Equal(previousHeight + 16 - 38, layout.GetHeight(false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ViewportRows_AreBoundedRegardlessOfRetainedCount(bool waterfall)
        {
            var layout = new CaptureTimelineLayout();
            layout.Reset(Enumerable.Range(0, 10000)
                .Select(index => Create(index + 1, Origin.AddSeconds(index), $"host-{index % 8}")));

            var rows = Rows(layout, waterfall, layout.GetHeight(waterfall) / 2, 80);
            Assert.InRange(rows.Count, 1, 14);
            Assert.All(rows, row => Assert.InRange(row.Top,
                (layout.GetHeight(waterfall) / 2) - 50, (layout.GetHeight(waterfall) / 2) + 130));
        }

        [Fact]
        public void LongHistory_TicksBeginAtVisibleOffsetAndRemainBounded()
        {
            var layout = new CaptureTimelineLayout();
            layout.Reset([Create(1, Origin), Create(2, Origin.AddDays(1))]);
            var ticks = new List<CaptureTimelineLayout.Tick>();
            layout.GetVisibleTicks(layout.Width - 800, 800, ticks);

            Assert.InRange(ticks.Count, 1, 12);
            Assert.All(ticks, tick => Assert.True(tick.Milliseconds > TimeSpan.FromHours(23).TotalMilliseconds));
            Assert.True(layout.Width > 40000000);
        }

        [Fact]
        public void ShortHistory_PreservesMinimumCanvasWidthAndStretchedScale()
        {
            var layout = new CaptureTimelineLayout();
            var model = Create(1, Origin, duration: TimeSpan.FromMilliseconds(10));
            layout.Add(model);

            Assert.Equal(200, layout.Width);
            Assert.Equal(20, layout.Scale);
            Assert.Equal(200, layout.GetBarBounds(Rows(layout, true, 0, 100)[0]).Width);
        }

        private static List<CaptureTimelineLayout.Row> Rows(
            CaptureTimelineLayout layout, bool waterfall, double offset, double height)
        {
            var result = new List<CaptureTimelineLayout.Row>();
            layout.GetVisibleRows(waterfall, offset, height, result);
            return result;
        }

        // The hosts of the timeline by host, in their order.
        private static string[] Hosts(CaptureTimelineLayout layout) =>
            Rows(layout, false, 0, 1000).Where(row => row.Model == null).Select(row => row.Host).ToArray();
    }
}
