// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Export;
using XMAT.SharedInterfaces;
using XMAT.Tests.Cli;
using XMAT.Tests.Operations.Analysis;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests
{
    // A capture list exports what it shows. Its row menu exports the selected or the visible sessions. Visible keeps every
    // active filter (the Status, Host and Method checkboxes as exact values, and the process filter), so the shared export
    // writes exactly the rows that the grid shows and that xmat export writes for the equivalent command line, the empty
    // status choice included.
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class CaptureExportViewTests : IDisposable
    {
        private const string PresenceHost = "userpresence.xboxlive.com";

        // 1 Game POST 200, 2 Game GET 200, 3 svchost POST 503, 4 Game POST without a response, 5 unknown GET 200,
        // 6 Game POST 503.
        private static readonly ProcessSession[] Sessions =
        {
            new("Game.exe", 4100, "local"),
            new("Game.exe", 4100, "local", Analyzable: false),
            new("svchost.exe", 900, "local", HttpStatus: "503"),
            new("Game.exe", 4100, "local", HttpStatus: string.Empty),
            new(string.Empty, 0, string.Empty, Analyzable: false),
            new("Game.exe", 4100, "local", HttpStatus: "503"),
        };

        private readonly CaptureUiApplicationFixture _application;
        private readonly CliTestDirectory _directory = new();

        public CaptureExportViewTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        public void Dispose() => _directory.Dispose();

        [Fact]
        public void RowMenu_ExportsTheSelectedOrTheVisibleSessions()
        {
            using var database = new SqLtReadonlyDatabase(ProcessCaptures.WriteXmatcap(_directory.Root, "menu.xmatcap", Sessions));
            _application.Run(window =>
            {
                (CaptureConnectionView view, WebServiceDeviceCaptureController controller) = Show(window, database);
                controller.SetProcessFilter(Spec(new[] { "game*.exe" }));
                WpfTestHost.Pump();
                DataGrid grid = (DataGrid)view.FindName("ConnectionList");
                ProxyConnectionModel[] chosen = grid.Items.Cast<ProxyConnectionModel>()
                    .Where(row => row.RequestNumber is 2 or 6)
                    .ToArray();
                foreach (ProxyConnectionModel row in chosen)
                {
                    grid.SelectedItems.Add(row);
                }

                ContextMenu menu = CaptureProcessFilterViewTests.Open(CaptureProcessFilterViewTests.Container(grid, chosen[0]));
                try
                {
                    foreach (string name in new[] { "ExportSelectedSessions", "ExportVisibleSessions" })
                    {
                        Assert.Contains(
                            menu.Items.OfType<MenuItem>(),
                            item => item.Command is RoutedCommand command && command.Name == name &&
                                command.OwnerType == typeof(WebServiceProxyCommands) &&
                                command.CanExecute(item.CommandParameter, item.CommandTarget));
                    }
                }
                finally
                {
                    menu.IsOpen = false;
                    WpfTestHost.Pump();
                }

                CaptureSelectionSpec selected = view.ExportSelection(selectedOnly: true);
                Assert.Equal(Items(controller.ProcessFilter.Include), Items(selected.Process.Include));
                Assert.True(selected.Keys.SetEquals(chosen.Select(row => row.RowKey)));
                Assert.Null(view.ExportSelection(selectedOnly: false).Keys);
                window.Content = null;
                WpfTestHost.Pump();
            });
        }

        [Theory]
        [InlineData("empty-status", "--status=", "4")]
        [InlineData(
            "every-filter",
            "--process|game*.exe|--host-mode|exact|--host|" + PresenceHost + "|--method|POST|--status|200|--status=",
            "1,4")]
        public Task VisibleSessions_ExportWhatTheGridShowsAndXmatExportWrites(string filters, string arguments, string expected) =>
            AssertParity(
                ProcessCaptures.WriteXmatcap(_directory.Root, "parity.xmatcap", Sessions),
                (view, controller) => Filter(view, controller, filters),
                arguments.Split('|'),
                expected.Split(',').Select(row => long.Parse(row, CultureInfo.InvariantCulture)).ToArray());

        // Exact checkboxes are literal sets of any size: 65 checked hosts, an empty one and one longer than 260 characters
        // export what xmat export writes for the same --host values, and the unchecked host stays out of both.
        [Fact]
        public Task VisibleSessions_ExportAnyNumberOfExactHostsLikeXmatExport()
        {
            string[] checkedHosts = Enumerable.Range(1, 65).Select(index => $"host{index}.test")
                .Append(string.Empty).Append(new string('h', 300) + ".test").ToArray();
            DateTime start = new(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
            string input = WriteCapture(
                _directory.Root,
                "hosts.xmatcap",
                checkedHosts.Append("other.test").Select((host, index) => new ProxyConnectionModel(
                    index + 1, "200", start.AddSeconds(index), TimeSpan.FromMilliseconds(25), "https", host + ":443", "GET",
                    "/", "GET / HTTP/1.1\r\n\r\n", Array.Empty<byte>(), "HTTP/1.1 200 OK\r\n\r\n", Array.Empty<byte>(),
                    ProcessCaptures.ClientIp)));

            return AssertParity(
                input,
                (view, controller) => Keep(view, "HostFilters", controller.HostFilterList, checkedHosts),
                checkedHosts.Select(host => "--host=" + host).Prepend("exact").Prepend("--host-mode"),
                Enumerable.Range(1, checkedHosts.Length).Select(row => (long)row).ToArray());
        }

        // The grid's rows, the shared export of its Visible selection and xmat export with these arguments all give these
        // request numbers.
        private async Task AssertParity(
            string input,
            Action<CaptureConnectionView, WebServiceDeviceCaptureController> filter,
            IEnumerable<string> arguments,
            long[] rows)
        {
            string guiOutput = Path.Join(_directory.Root, "gui.xmatcap");
            string cliOutput = Path.Join(_directory.Root, "cli.xmatcap");
            long[] shown = null;
            CaptureSelectionSpec visible = null;
            ICaptureExportSnapshot snapshot = null;
            OperationResult<ExportCaptureResult> gui;
            using (var database = new SqLtReadonlyDatabase(input))
            {
                _application.Run(window =>
                {
                    (CaptureConnectionView view, WebServiceDeviceCaptureController controller) = Show(window, database);
                    filter(view, controller);
                    shown = ((DataGrid)view.FindName("ConnectionList")).Items.Cast<ProxyConnectionModel>()
                        .Select(row => row.RequestNumber)
                        .OrderBy(number => number)
                        .ToArray();
                    visible = view.ExportSelection(selectedOnly: false);
                    snapshot = controller.CreateExportSnapshot("d1");
                    window.Content = null;
                    WpfTestHost.Pump();
                });
                using (snapshot)
                {
                    gui = await new ExportCaptureSessionsOperation().ExecuteAsync(
                        new ExportCaptureRequest(
                            null,
                            snapshot,
                            null,
                            visible,
                            CaptureExportFormat.Xmatcap,
                            guiOutput,
                            false,
                            AnalysisLimits.Default,
                            AllowUnprotectedOutput: false),
                        OperationContext.None,
                        CancellationToken.None);
                }
            }

            CliParseResult parsed = CliCommandLine.Parse(new[] { "export", input, "--out", cliOutput }.Concat(arguments).ToArray());
            CliReport cli = parsed.Request == null
                ? null
                : await CliExportCommand.RunAsync(parsed.Request, "parity", CancellationToken.None);

            Assert.Equal(rows, shown);
            Assert.Equal(OperationOutcome.Succeeded, gui.Outcome);
            Assert.Equal(rows, XmatExportTests.RequestNumbers(guiOutput));
            Assert.Null(parsed.Error?.Code);
            CliReport completed = Assert.IsType<CliReport>(cli);
            Assert.Equal(CliExitCodes.Success, completed.ExitCode);
            Assert.Equal(rows, XmatExportTests.RequestNumbers(cliOutput));
        }

        // A capture file of these sessions from the capture PC, in the current schema.
        internal static string WriteCapture(string directory, string name, IEnumerable<ProxyConnectionModel> sessions)
        {
            string path = Path.Join(directory, name);
            using var database = new SqLtDatabase();
            database.Initialize();
            database.CreateTable(
                    "CaptureDevices", new Field<long>("DeviceType"), new Field<string>("DeviceName"), new Field<long>("CaptureType"))
                .AddRow((long)DeviceType.LocalPC, ProcessCaptures.Device, (long)CaptureType.WebProxy);
            IDataTable captures = database.CreateTable(WebServiceCaptureMethod.TableName, WebProxyCaptureSchema.CreateFields());
            foreach (ProxyConnectionModel session in sessions)
            {
                captures.AddRow(session.ToDataTableValues(ProcessCaptures.Device));
            }

            database.SaveToFile(path);
            return path;
        }

        // The capture opened read-only in a capture tab, as File > Load Captures shows it.
        private static (CaptureConnectionView View, WebServiceDeviceCaptureController Controller) Show(
            Window window,
            SqLtReadonlyDatabase database)
        {
            var controller = new WebServiceDeviceCaptureController(ProcessCaptures.Device, DeviceType.LocalPC, true);
            controller.LoadCaptureDataset(database.TableByName(WebServiceCaptureMethod.TableName).Dataset);
            CaptureConnectionView view = CaptureProcessFilterViewTests.View(controller);
            CaptureProcessFilterViewTests.ShowCaptureView(window, view);
            return (view, controller);
        }

        // The capture list's filters that match each case's command line.
        private static void Filter(CaptureConnectionView view, WebServiceDeviceCaptureController controller, string filters)
        {
            if (filters == "every-filter")
            {
                controller.SetProcessFilter(Spec(new[] { "game*.exe" }));
                Keep(view, "HostFilters", controller.HostFilterList, PresenceHost);
                Keep(view, "MethodFilters", controller.MethodFilterList, "POST");
                Keep(view, "StatusFilters", controller.StatusFilterList, "200", string.Empty);
                return;
            }

            Keep(view, "StatusFilters", controller.StatusFilterList, string.Empty);
        }

        // Checks only these values of one checkbox filter, then enables the filter.
        private static void Keep(CaptureConnectionView view, string filter, IEnumerable<CheckedListItem> items, params string[] values)
        {
            foreach (CheckedListItem item in items)
            {
                item.IsChecked = values.Contains(item.Text, StringComparer.Ordinal);
            }

            ((CheckBox)((CheckBoxFilters)view.FindName(filter)).FindName("EnableFilters")).IsChecked = true;
            WpfTestHost.Pump();
        }
    }
}
