// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows.Controls;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.Tests.Operations.Analysis;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests
{
    // The same capture and selectors select the same rows in the GUI's capture tab and in the shared analysis that xmat
    // analyze runs: the row keys must match, not only their counts.
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class ProcessFilterParityTests : IDisposable
    {
        private readonly CaptureUiApplicationFixture _application;
        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory,
            "HeadlessTestArtifacts",
            "ProcessFilterParity-" + Guid.NewGuid().ToString("N"));

        public ProcessFilterParityTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Theory]
        [InlineData("game*.exe", "", "1,2,4,8")]
        [InlineData("", "svchost.exe", "1,2,4,5,7,8")]
        [InlineData("game*.exe;svchost", "4100", "3,6")]
        [InlineData("900", "", "3,6")]
        [InlineData("GAME", "", "1,2,4,8")]
        public async Task GuiAndCli_SelectTheSameRows(string include, string exclude, string expected)
        {
            string path = ProcessCaptures.WriteXmatcap(_directory, "parity.xmatcap", ProcessCaptures.MixedSessions);
            ProcessFilterSpec spec = Spec(Selectors(include), Selectors(exclude));
            long[] expectedRows = expected.Split(',').Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToArray();

            OperationResult<AnalysisResult> cli = await new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(AnalysisInput.FromFile(path), new AnalysisOptions(ProcessFilter: spec)),
                OperationContext.None,
                CancellationToken.None);
            long[] gui = GuiRows(path, spec);

            Assert.Equal(OperationOutcome.Succeeded, cli.Outcome);
            Assert.NotNull(cli.Value.IncludedKeys);
            Assert.Equal(expectedRows, RowIds(cli.Value.IncludedKeys));
            Assert.Equal(expectedRows, gui);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        private static string[] Selectors(string text) =>
            text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // The rows that the capture tab shows for the capture opened read-only, with the process filter set.
        private long[] GuiRows(string path, ProcessFilterSpec spec)
        {
            long[] rows = null;
            using var database = new SqLtReadonlyDatabase(path);
            _application.Run(window =>
            {
                var controller = new WebServiceDeviceCaptureController(ProcessCaptures.Device, DeviceType.Unknown, true);
                controller.LoadCaptureDataset(database.TableByName(WebServiceCaptureMethod.TableName).Dataset);
                var view = new CaptureConnectionView { AutoScroll = false };
                var grid = (DataGrid)view.FindName("ConnectionList");
                grid.IsSynchronizedWithCurrentItem = false;
                view.DataContext = new Context(controller);
                window.Width = 1280;
                window.Height = 720;
                WpfTestHost.Show(window, view);
                Assert.Equal(ProcessCaptures.MixedSessions.Count, grid.Items.Count);

                controller.SetProcessFilter(spec);
                WpfTestHost.Pump();

                rows = grid.Items.Cast<ProxyConnectionModel>().Select(model => model.RecordRowId).OrderBy(id => id).ToArray();
                window.Content = null;
                WpfTestHost.Pump();
            });
            return rows;
        }

        private sealed class Context(IDeviceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => ProcessCaptures.Device;
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; } = true;
            public System.Drawing.Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; } = controller;
        }
    }
}
