// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.CaptureTimelineTestData;

namespace XMAT.Tests
{
    public class CaptureTimelineLayoutTests
    {
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

        [Fact]
        public void Volume_CountsShownRequestsPerSecondAsTheyAreAddedChangedAndRemoved()
        {
            var second = CaptureVolumeSeries.SecondOf(Origin);
            var first = Create(1, Origin.AddMilliseconds(200));
            var tied = Create(2, Origin.AddMilliseconds(700));
            var later = Create(3, Origin.AddMilliseconds(2100));
            var missing = Create(4, DateTime.MinValue);
            var layout = new CaptureTimelineLayout();
            layout.Reset([first, tied, later, missing]);
            var volume = layout.Volume;

            Assert.Equal((3, 2, 1, 2), (volume.Total, volume.CountAt(second), volume.CountAt(second + 2), volume.Peak));
            Assert.Equal((second, second + 2), (volume.FirstSecond, volume.LastSecond));
            layout.Add(Create(5, Origin.AddMilliseconds(2900)));
            Update(later, Origin.AddSeconds(5));
            Assert.True(layout.Update(later));
            Assert.Equal((1, 1), (volume.CountAt(second + 2), volume.CountAt(second + 5)));
            Assert.Equal(second + 5, volume.LastSecond);

            layout.Remove(first);
            layout.Remove(tied);
            Assert.Equal((2, 1, second + 2), (volume.Total, volume.Peak, volume.FirstSecond));
            Assert.Same(later, layout.FirstRequestBetween(Origin.AddSeconds(3), Origin.AddSeconds(6)));
            Assert.Null(layout.FirstRequestBetween(Origin, Origin.AddSeconds(2)));
            int resets = layout.ResetCount;
            Update(missing, Origin);
            Assert.True(layout.Update(missing));
            Assert.Equal((3, 1), (volume.Total, volume.CountAt(second)));
            Assert.Equal(resets, layout.ResetCount);
            layout.Reset([]);
            Assert.Equal((0, 0), (volume.Total, volume.Peak));
        }

        [Fact]
        public void Volume_LiveWindowIsTheLastMinuteOfCompletedSecondsNewestLast()
        {
            var start = CaptureVolumeSeries.SecondOf(Origin);
            var layout = new CaptureTimelineLayout();
            layout.Reset([
                Create(1, Origin.AddSeconds(39.5)), Create(2, Origin.AddSeconds(40.1)), Create(3, Origin.AddSeconds(98.2)),
                Create(4, Origin.AddSeconds(99.1)), Create(5, Origin.AddSeconds(99.9)), Create(6, Origin.AddSeconds(100.2))
            ]);
            var totals = new List<int>();

            var span = layout.Volume.GetLive(Origin.AddSeconds(100.5), totals);

            Assert.Equal(new CaptureVolumeSeries.PlotSpan(start + 40, start + 100, 1), span);
            Assert.Equal(CaptureVolumeSeries.LiveSeconds, totals.Count);
            Assert.Equal((1, 1, 2), (totals[0], totals[^2], totals[^1]));
            Assert.Equal(4, totals.Sum());
            layout.Volume.GetLive(Origin.AddSeconds(500), totals);
            Assert.All(totals, total => Assert.Equal(0, total));
        }

        [Fact]
        public void Volume_WholeCaptureUsesWiderStepsThatAverageTheirSecondsWhenSecondsDontFit()
        {
            // 3 min 37 s: two requests in each of the first 30 seconds, then one per second, and one in the last second.
            var models = Enumerable.Range(0, 30).SelectMany(index => new[] { index * 2, (index * 2) + 1 })
                .Select(number => Create(number + 1, Origin.AddSeconds(number / 2)))
                .Concat(Enumerable.Range(30, 170).Select(index => Create(index + 100, Origin.AddSeconds(index))))
                .Append(Create(999, Origin.AddSeconds(216.5)))
                .ToList();
            var layout = new CaptureTimelineLayout();
            layout.Reset(models);
            var totals = new List<int>();

            var fits = layout.Volume.GetWhole(651, totals);
            Assert.Equal((1L, 217), (fits.Step, fits.Count));

            var span = layout.Volume.GetWhole(640, totals);
            Assert.Equal((2L, 109), (span.Step, span.Count));
            Assert.Equal(models.Count, totals.Sum());
            Assert.Equal((4, 2.0), (totals[0], span.Average(0, totals[0])));
            Assert.Equal((2, 1.0), (totals[20], span.Average(20, totals[20])));
            Assert.Equal((0, 0.0), (totals[105], span.Average(105, totals[105])));
            // The last step covers only the capture's last second.
            Assert.Equal((1, 1.0), (totals[^1], span.Average(span.Count - 1, totals[^1])));
            Assert.Equal((10L, 1800L), (CaptureVolumeSeries.ChooseStep(2130, 640), CaptureVolumeSeries.ChooseStep(86400, 200)));
            Assert.Equal(5 * 86400L, CaptureVolumeSeries.ChooseStep(86400L * 1000, 640));
            Assert.Equal(CaptureVolumeSeries.PlotSpan.Empty, new CaptureTimelineLayout().Volume.GetWhole(640, totals));
            Assert.Empty(totals);
        }

        [Theory]
        [InlineData(0, 5)]
        [InlineData(3, 5)]
        [InlineData(5, 5)]
        [InlineData(7, 10)]
        [InlineData(15, 20)]
        [InlineData(38, 40)]
        [InlineData(52, 60)]
        [InlineData(61, 80)]
        [InlineData(100, 100)]
        [InlineData(100.5, 150)]
        [InlineData(700, 800)]
        [InlineData(1000, 1000)]
        [InlineData(1001, 1500)]
        [InlineData(4999.5, 5000)]
        [InlineData(8001, 10000)]
        [InlineData(10001, 15000)]
        public void Volume_TopValueRoundsUpThroughStepsThatKeepTheLineTall(double peak, double top) =>
            Assert.Equal(top, CaptureVolumeSeries.NiceTop(peak));

        [Fact]
        public void Volume_ElapsedTimeReadsAsMinutesAndSecondsThenHours() =>
            Assert.Equal(new[] { "0:00", "0:42", "2:10", "59:59", "1:02:05" },
                new long[] { 0, 42, 130, 3599, 3725 }.Select(CaptureVolumeSeries.FormatElapsed));

        private static List<CaptureTimelineLayout.Row> Rows(
            CaptureTimelineLayout layout, bool waterfall, double offset, double height)
        {
            var result = new List<CaptureTimelineLayout.Row>();
            layout.GetVisibleRows(waterfall, offset, height, result);
            return result;
        }
    }
}
