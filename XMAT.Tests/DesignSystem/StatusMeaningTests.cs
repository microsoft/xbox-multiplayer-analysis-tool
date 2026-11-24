// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.Tests.HeadlessParity;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using Path = System.Windows.Shapes.Path;

namespace XMAT.Tests.DesignSystem
{
    // Every state XMAT shows has a meaning (XMAT/Logic/StatusMeanings.cs), and the meaning, not the state, sets how it
    // shows: in a table a problem's or a warning's status cell takes color and its row a faint wash, the status stays in
    // view, and the charts color each status class alike (docs/design-system.md).
    [Collection(CaptureUiApplicationCollection.Name)]
    public class StatusMeaningTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly Regex DynamicReference = new(@"^\{DynamicResource\s+(\w+)\s*\}$", RegexOptions.CultureInvariant);

        private readonly CaptureUiApplicationFixture _application;

        public StatusMeaningTests(CaptureUiApplicationFixture application) => _application = application;

        // A state added without a meaning throws, citing STATUS-1, wherever a view asks for it.
        [Fact]
        public void EveryState_HasAMeaning()
        {
            var missing = new List<string>();
            Check<TrafficStatusClass>(status => StatusMeanings.Of(status));
            Check<TrafficStatusClass>(status => StatusMeanings.ChartFill(status));
            Check<DeviceStatusEnum>(state => StatusMeanings.Of(state));
            Check<ProxyConfigurationState>(state => StatusMeanings.Of(state));
            Check<ProxyState>(state => StatusMeanings.Of(state));
            Check<ResponseProgress>(progress => StatusMeanings.Of(progress));
            Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));

            void Check<T>(Func<T, object> meaning) where T : struct, Enum
            {
                foreach (T value in Enum.GetValues<T>())
                {
                    try
                    {
                        meaning(value);
                    }
                    catch (ArgumentOutOfRangeException exception)
                    {
                        missing.Add(exception.Message);
                    }
                }
            }
        }

        // In the new look, a problem's or a warning's status takes its color and icon, its row a faint wash of that color,
        // and the row's text stays body text. The original look colors the whole row's text, as it always has, and shows
        // the status as its other cells.
        [Fact]
        public void Table_WashesAProblemOrWarningRowAndColorsItsStatusCell()
        {
            _application.Run(window =>
            {
                bool wasNewLook = LookState.Current.IsNewLook;
                try
                {
                    foreach (bool newLook in new[] { true, false })
                    {
                        Color Of(string key) => ((SolidColorBrush)window.FindResource(key)).Color;
                        foreach (var (model, row, status, code) in ShowTable(window, newLook, "200", "429", "503", string.Empty))
                        {
                            Color rowText = ((SolidColorBrush)row.Foreground).Color;
                            Color codeText = ((SolidColorBrush)code.Foreground).Color;
                            string shown = model.Status.Length == 0 ? "no-status" : model.Status;
                            var grid = (DataGrid)ItemsControl.ItemsControlFromItemContainer(row);
                            if (newLook)
                            {
                                (string brush, bool icon, string wash) expected = StatusMeanings.OfHttpStatus(model.Status) switch
                                {
                                    StatusMeaning.Problem => ("ErrorTextBrush", true, "ProblemRowBrush"),
                                    StatusMeaning.Attention => ("WarningTextBrush", true, "AttentionRowBrush"),
                                    _ => ("TextPrimaryBrush", false, null),
                                };
                                var icon = (Path)status.Template.FindName("Icon", status);
                                Assert.True(rowText == Of("TextPrimaryBrush"),
                                    $"STATUS-2: the row of a {shown} request is in color; only its status cell may be.");
                                Assert.True(codeText == Of(expected.brush),
                                    $"STATUS-2: a {shown} status isn't in {expected.brush}.");
                                Assert.True((icon.Visibility == Visibility.Visible) == expected.icon,
                                    $"STATUS-2: a {shown} status {(expected.icon ? "has no" : "has an")} icon.");
                                object background = expected.wash == null ? grid.RowBackground : window.FindResource(expected.wash);
                                Assert.True(ReferenceEquals(background, row.Background),
                                    $"STATUS-2: the row of a {shown} request isn't on {expected.wash ?? "the table's row background"}.");
                            }
                            else
                            {
                                string expected = model.Status switch
                                {
                                    "" => "CapturePendingForegroundBrush",
                                    "429" or "503" => "ErrorTextBrush",
                                    _ => "TextPrimaryBrush",
                                };
                                Assert.Equal(Of(expected), rowText);
                                Assert.Equal(rowText, codeText);
                                Assert.Same(DataGridTextColumn.DefaultElementStyle, code.Style);
                                Assert.True(DependencyPropertyHelper.GetValueSource(row, Control.BackgroundProperty).BaseValueSource
                                    != BaseValueSource.StyleTrigger, $"The original look washes the row of a {shown} request.");
                            }
                        }

                        window.Content = null;
                    }
                }
                finally
                {
                    LookState.Current.IsNewLook = wasNewLook;
                }
            });
        }

        // A response still streaming is work under way, whatever its code. In the new look its status cell says
        // Streaming in secondary text, without an icon, and shows the code once the response ends. The original look
        // keeps the code, and shows the row as pending (CaptureConnectionViewTests). Screen readers hear both.
        [Fact]
        public void StreamingResponse_SaysStreamingInSecondaryText_UntilItEnds()
        {
            _application.Run(window =>
            {
                bool wasNewLook = LookState.Current.IsNewLook;
                try
                {
                    foreach (bool newLook in new[] { true, false })
                    {
                        LookState.Current.IsNewLook = newLook;
                        ResourceDictionary resources = ThemeManager.CreateLook(newLook);
                        ThemeManager.LoadTheme(resources, "Light", newLook, highContrast: false);
                        window.Resources = resources;
                        Color Of(string key) => ((SolidColorBrush)window.FindResource(key)).Color;
                        foreach (string code in new[] { "200", "503" })
                        {
                            var cell = new StatusText { Status = code, IsStreaming = true };
                            WpfTestHost.Show(window, cell);
                            TextBlock text = DesignSystemWindowTests.Descendants(cell).OfType<TextBlock>().Single();
                            Assert.True(cell.Meaning == StatusMeaning.Working,
                                $"STATUS-1: a {code} response still streaming means {cell.Meaning}, not Working.");
                            Assert.Equal($"{code}, still streaming", cell.SpokenText);
                            if (newLook)
                            {
                                var icon = (Path)cell.Template.FindName("Icon", cell);
                                Assert.True(text.Text == "Streaming" && ((SolidColorBrush)text.Foreground).Color == Of("TextSecondaryBrush"),
                                    $"STATUS-1: a {code} response still streaming shows \"{text.Text}\", not Streaming in secondary text.");
                                Assert.True(icon.Visibility != Visibility.Visible,
                                    $"STATUS-1: a {code} response still streaming has an icon.");
                            }
                            else
                            {
                                Assert.Equal(code, text.Text);
                            }

                            cell.IsStreaming = false;
                            WpfTestHost.Pump();
                            Assert.Equal(code, text.Text);
                            Assert.Equal(StatusMeanings.OfHttpStatus(code), cell.Meaning);
                            window.Content = null;
                        }
                    }
                }
                finally
                {
                    LookState.Current.IsNewLook = wasNewLook;
                }
            });
        }

        // In the new look every row keeps the icon's space, so a code starts in the same place whether or not its row
        // shows the icon. The original look has no icons, and its codes line up too.
        [Fact]
        public void Table_LinesUpEveryStatusCode()
        {
            _application.Run(window =>
            {
                bool wasNewLook = LookState.Current.IsNewLook;
                try
                {
                    foreach (bool newLook in new[] { true, false })
                    {
                        var codes = ShowTable(window, newLook, "200", "503", "302", "429")
                            .Select(shown => (shown.Model.Status, X: shown.Code.TranslatePoint(new Point(), shown.Row).X))
                            .ToList();
                        foreach (var (status, x) in codes.Skip(1))
                        {
                            Assert.True(Math.Abs(x - codes[0].X) < 0.5,
                                $"STATUS-4: in the {(newLook ? "new" : "original")} look a {status} status's code starts " +
                                $"{x:0.#}px into its row and a {codes[0].Status} status's {codes[0].X:0.#}px, so the codes " +
                                "don't line up.");
                        }

                        window.Content = null;
                    }
                }
                finally
                {
                    LookState.Current.IsNewLook = wasNewLook;
                }
            });
        }

        // The new look's capture table keeps # and Status in place when it scrolls sideways, so a row scrolled across to
        // its path still says which request it is and how it went. The original look's table scrolls as it always has.
        [Fact]
        public void Table_KeepsNumberAndStatusInView()
        {
            _application.Run(window =>
            {
                bool wasNewLook = LookState.Current.IsNewLook;
                try
                {
                    foreach (bool newLook in new[] { true, false })
                    {
                        var (_, row, status, _) = ShowTable(window, newLook, "503").Single();
                        var grid = (DataGrid)ItemsControl.ItemsControlFromItemContainer(row);
                        string look = newLook ? "new" : "original";
                        int kept = newLook ? 2 : 0;
                        Assert.True(grid.FrozenColumnCount == kept,
                            $"STATUS-5: the {look} look's capture table keeps {grid.FrozenColumnCount} columns in place, not {kept}.");
                        if (newLook)
                        {
                            Assert.Equal(new object[] { "#", "STATUS" },
                                grid.Columns.Where(column => column.IsFrozen).OrderBy(column => column.DisplayIndex)
                                    .Select(column => column.Header));

                            // Narrow enough that the table scrolls sideways, then scrolled all the way across.
                            window.Width = 760;
                            WpfTestHost.Pump();
                            ScrollViewer scroller = DesignSystemWindowTests.Descendants(grid).OfType<ScrollViewer>().First();
                            Assert.True(scroller.ScrollableWidth > 0, "The table doesn't scroll sideways at this width.");
                            double before = status.TranslatePoint(new Point(), grid).X;
                            scroller.ScrollToHorizontalOffset(scroller.ScrollableWidth);
                            WpfTestHost.Pump();
                            Assert.True(Math.Abs(status.TranslatePoint(new Point(), grid).X - before) < 0.5,
                                "STATUS-5: the status moves out of view when the table scrolls sideways.");
                        }

                        window.Content = null;
                    }
                }
                finally
                {
                    LookState.Current.IsNewLook = wasNewLook;
                }
            });
        }

        // The timeline takes its fills from StatusMeanings.ChartFill; the traffic overview and the column summaries name
        // theirs in their styles, which match it, and the column summaries sort statuses into the same classes.
        [Fact]
        public void Charts_ColorEachStatusClassTheSameWay()
        {
            var found = new List<string>();
            const string OverviewFile = "Styles/TrafficOverview.xaml";
            XDocument overview = Load(OverviewFile);
            var strip = new Dictionary<string, TrafficStatusClass>
            {
                ["SuccessBrush"] = TrafficStatusClass.Success,
                ["RedirectBrush"] = TrafficStatusClass.Redirect,
                ["ClientErrorBrush"] = TrafficStatusClass.ClientError,
                ["ServerErrorBrush"] = TrafficStatusClass.ServerError,
                ["NoStatusBrush"] = TrafficStatusClass.None,
            };
            foreach (XElement setter in overview.Descendants(Presentation + "Setter"))
            {
                if (strip.TryGetValue((string)setter.Attribute("Property") ?? string.Empty, out TrafficStatusClass status))
                {
                    Compare(OverviewFile, $"the strip's {status}", (string)setter.Attribute("Value"), status);
                }
            }

            XElement swatch = overview.Descendants(Presentation + "Border")
                .Single(border => (string)border.Attribute(Xaml + "Name") == "Swatch");
            Compare(OverviewFile, "the legend's Success", (string)swatch.Attribute("Background"), TrafficStatusClass.Success);
            foreach (XElement trigger in Triggers(overview, "{Binding StatusClass}"))
            {
                var status = Enum.Parse<TrafficStatusClass>((string)trigger.Attribute("Value"));
                Compare(OverviewFile, $"the legend's {status}", Background(trigger, "Swatch"), status);
            }

            const string DataVizFile = "Styles/DataViz.xaml";
            foreach (XElement trigger in Triggers(Load(DataVizFile), "{Binding Bar}"))
            {
                string bar = (string)trigger.Attribute("Value");
                if (bar == nameof(ColumnSummaryBar.Neutral))
                {
                    continue;
                }

                TrafficStatusClass status = bar == nameof(ColumnSummaryBar.NoStatus)
                    ? TrafficStatusClass.None
                    : Enum.Parse<TrafficStatusClass>(bar);
                Compare(DataVizFile, $"a column summary's {status} bar", Background(trigger, "Bar"), status);
            }

            foreach (string code in new[] { "200", "301", "404", "429", "503", "599", "100", string.Empty, "none" })
            {
                TrafficStatusClass status = TrafficSummary.ClassOf(code);
                string expected = status == TrafficStatusClass.None ? nameof(ColumnSummaryBar.NoStatus) : status.ToString();
                string bar = ColumnSummaryBars.ForHttpStatus(code).ToString();
                if (bar != expected)
                {
                    found.Add($"STATUS-3: a column summary puts \"{code}\" in its {bar} bar rather than {expected}, so " +
                        "it colors it unlike the other charts.");
                }
            }

            Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));

            void Compare(string file, string what, string value, TrafficStatusClass status)
            {
                string expected = StatusMeanings.ChartFill(status);
                Match key = DynamicReference.Match(value ?? string.Empty);
                if (!key.Success || key.Groups[1].Value != expected)
                {
                    found.Add($"STATUS-3: {file} colors {what} with {value}, not {{DynamicResource {expected}}} as " +
                        "StatusMeanings.ChartFill does.");
                }
            }
        }

        // A screen reader hears what a status means, since it doesn't see the color or the icon (COLOR-7).
        [Fact]
        public void StatusText_TellsScreenReadersWhatAProblemOrWarningMeans()
        {
            _application.Run(_ =>
            {
                Assert.Equal("503, problem", SpokenName("503"));
                Assert.Equal("429, needs attention", SpokenName("429"));
                Assert.Equal("200", SpokenName("200"));
                Assert.Equal(string.Empty, SpokenName(string.Empty));
            });

            static string SpokenName(string status) =>
                UIElementAutomationPeer.CreatePeerForElement(new StatusText { Status = status }).GetName();
        }

        private static XDocument Load(string file) => XDocument.Load(System.IO.Path.Combine(RepositoryPaths.Root, "XMAT", file));

        // Shows the capture table in a look, in the light theme, with a request for each status, and returns each
        // request's row, status cell and code.
        private static List<(ProxyConnectionModel Model, DataGridRow Row, StatusText Status, TextBlock Code)> ShowTable(
            Window window, bool newLook, params string[] statuses)
        {
            LookState.Current.IsNewLook = newLook;
            ResourceDictionary look = ThemeManager.CreateLook(newLook);
            ThemeManager.LoadTheme(look, "Light", newLook, highContrast: false);
            window.Resources = look;

            var controller = new WebServiceDeviceCaptureController("Test device", DeviceType.Unknown, true);
            for (int index = 0; index < statuses.Length; index++)
            {
                controller.ProxyConnections.Add(CaptureTimelineTestData.Create(index + 1, status: statuses[index]));
            }

            var view = new CaptureConnectionView { AutoScroll = false, DataContext = new Context(controller) };
            var grid = (DataGrid)view.FindName("ConnectionList");
            grid.IsSynchronizedWithCurrentItem = false;
            window.Width = 1200;
            window.Height = 720;
            WpfTestHost.Show(window, view);

            var shown = new List<(ProxyConnectionModel, DataGridRow, StatusText, TextBlock)>();
            foreach (ProxyConnectionModel model in controller.ProxyConnections)
            {
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(model);
                Assert.NotNull(row);
                StatusText status = DesignSystemWindowTests.Descendants(row).OfType<StatusText>().Single();
                shown.Add((model, row, status, DesignSystemWindowTests.Descendants(status).OfType<TextBlock>().Single()));
            }

            return shown;
        }

        private static IEnumerable<XElement> Triggers(XDocument document, string binding) =>
            document.Descendants(Presentation + "DataTrigger").Where(trigger => (string)trigger.Attribute("Binding") == binding);

        // The Background a trigger gives the element named TARGET.
        private static string Background(XElement trigger, string target) =>
            (string)trigger.Elements(Presentation + "Setter")
                .Single(setter => (string)setter.Attribute("TargetName") == target && (string)setter.Attribute("Property") == "Background")
                .Attribute("Value");

        private sealed class Context(IDeviceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType { get; set; } = DeviceType.Unknown;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "Test device";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public System.Drawing.Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => controller;
        }
    }
}
