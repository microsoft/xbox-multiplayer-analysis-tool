// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Controls;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.CaptureTimelineTestData;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests
{
    // The timeline follows the process filter one item at a time: a late process resolution adds or removes that
    // request's bar without rebuilding the layout.
    [Collection(WpfTestHostCollection.Name)]
    public class CaptureTimelineProcessFilterTests
    {
        [Fact]
        public void Layout_FilterHidesRowsAndAnUpdateRevealsALateMatchWithoutAReset()
        {
            ProxyConnectionModel game = WithProcess(Create(1), Local("Game.exe", 10));
            ProxyConnectionModel tool = WithProcess(Create(2), Local("Tool.exe", 11));
            ProxyConnectionModel pending = WithProcess(Create(3), ClientProcessInfo.NotRecorded, resolving: true);
            var layout = new CaptureTimelineLayout();
            layout.Reset(new[] { game, tool, pending });
            Assert.Equal(3, layout.Count);

            layout.SetFilter(model => model.ClientProcess.Name == "Game.exe");
            Assert.Equal(new[] { game }, Models(layout));
            int resets = layout.ResetCount;

            pending.SetClientProcess(Local("Game.exe", 10));
            Assert.True(layout.Update(pending));
            Assert.False(layout.Update(tool));

            Assert.Equal(new[] { game, pending }, Models(layout));
            Assert.Equal(resets, layout.ResetCount);

            game.SetClientProcess(Local("Other.exe", 12));
            Assert.True(layout.Update(game));
            Assert.Equal(new[] { pending }, Models(layout));
            Assert.Equal(resets, layout.ResetCount);

            layout.SetFilter(null);
            Assert.Equal(new[] { game, tool, pending }, Models(layout));
        }

        [Fact]
        public void View_ShowsOnlyMatchingBarsAndAddsALateMatchWithoutRebuildingTheLayout()
        {
            WpfTestHost.Run(window =>
            {
                ProxyConnectionModel game = WithProcess(Create(1, start: Origin), Local("Game.exe", 10));
                ProxyConnectionModel tool = WithProcess(Create(2, start: Origin.AddMilliseconds(5)), Local("Tool.exe", 11));
                ProxyConnectionModel pending = WithProcess(
                    Create(3, start: Origin.AddMilliseconds(10)), ClientProcessInfo.NotRecorded, resolving: true);
                var view = new CaptureTimelineView
                {
                    ItemsSource = new ObservableCollection<ProxyConnectionModel> { game, tool, pending },
                    ProcessFilter = ProcessFilterTestSupport.Spec(new[] { "Game.exe" }),
                };
                WpfTestHost.Show(window, view);
                Assert.Equal(new[] { game }, BarModels(view));
                int resets = Layout(view).ResetCount;

                pending.SetClientProcess(Local("Game.exe", 10));
                WpfTestHost.Pump();

                Assert.Equal(new[] { game, pending }, BarModels(view));
                Assert.Equal(resets, Layout(view).ResetCount);

                view.ProcessFilter = null;
                WpfTestHost.Pump();
                Assert.Equal(new[] { game, tool, pending }, BarModels(view));
            });
        }

        [Fact]
        public void View_ExcludedProcessLeavesTheTimelineAndUnknownRequestsStay()
        {
            WpfTestHost.Run(window =>
            {
                ProxyConnectionModel game = WithProcess(Create(1, start: Origin), Local("Game.exe", 10));
                ProxyConnectionModel service = WithProcess(Create(2, start: Origin.AddMilliseconds(5)), Local("svchost.exe", 900));
                ProxyConnectionModel unknown = Create(3, start: Origin.AddMilliseconds(10));
                var view = new CaptureTimelineView
                {
                    ItemsSource = new ObservableCollection<ProxyConnectionModel> { game, service, unknown },
                };
                WpfTestHost.Show(window, view);
                Assert.Equal(new[] { game, service, unknown }, BarModels(view));

                view.ProcessFilter = ProcessFilterTestSupport.Spec(null, new[] { "svchost.exe" });
                WpfTestHost.Pump();

                Assert.Equal(new[] { game, unknown }, BarModels(view));
            });
        }

        private static ProxyConnectionModel[] Models(CaptureTimelineLayout layout)
        {
            var rows = new List<CaptureTimelineLayout.Row>();
            layout.GetVisibleRows(true, 0, 10_000, rows);
            return rows.Select(row => row.Model).ToArray();
        }

        private static ProxyConnectionModel[] BarModels(CaptureTimelineView view) =>
            ((Canvas)view.FindName("TimelineCanvas")).Children.OfType<Border>()
                .Select(bar => bar.Tag)
                .OfType<ProxyConnectionModel>()
                .OrderBy(model => model.RequestNumber)
                .ToArray();

        private static CaptureTimelineLayout Layout(CaptureTimelineView view) =>
            (CaptureTimelineLayout)typeof(CaptureTimelineView)
                .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(view);
    }
}
