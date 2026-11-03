// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using ICSharpCode.AvalonEdit.CodeCompletion;
using XMAT.Models;
using XMAT.SharedInterfaces;
using static XMAT.Tests.SqlQueryWorkspaceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class SqlQueryWorkspaceViewTests
    {
        private readonly CaptureUiApplicationFixture _application;
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        public SqlQueryWorkspaceViewTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void MainWorkspaceSwitchRetainsTheCaptureGridAndSqlEditorCaretUndoAndSuccessfulResult()
        {
            _application.Run(window =>
            {
                var sources = new Sources();
                var workspace = new SqlQueryWorkspaceModel(sources, window.Dispatcher);
                try
                {
                    Complete(workspace.RefreshSourcesAsync());
                    var document = workspace.NewDocument();
                    var root = ProductGrid();
                    var tabs = (TabControl)root.FindName("WorkspaceTabs");
                    var captureGrid = (Grid)root.FindName("MainClientArea");
                    var captureDevices = root.FindName("AllCaptureDevicesPanel");
                    var analysis = root.FindName("CaptureErrorsPanel");
                    var sql = (SqlQueryWorkspaceView)root.FindName("SqlWorkspaceView");
                    sql.AttachWorkspace(workspace);
                    WpfTestHost.Show(window, root);
                    tabs.SelectedIndex = 1;
                    WpfTestHost.Pump();
                    var editor = Descendants(sql).OfType<SqlQueryEditorView>().Single();
                    editor.Editor.Text = "SELECT 42";
                    editor.Editor.CaretOffset = 6;
                    editor.Editor.Document.Insert(editor.Editor.Document.TextLength, " ");
                    editor.Editor.CaretOffset = 6;
                    Assert.True(Complete(document.RunAsync()));
                    var result = document.ResultRun;
                    tabs.SelectedIndex = 0;
                    WpfTestHost.Pump();
                    Assert.Same(captureGrid, root.FindName("MainClientArea"));
                    Assert.Same(captureDevices, root.FindName("AllCaptureDevicesPanel"));
                    Assert.Same(analysis, root.FindName("CaptureErrorsPanel"));
                    tabs.SelectedIndex = 1;
                    WpfTestHost.Pump();
                    Assert.Same(editor, Descendants(sql).OfType<SqlQueryEditorView>().Single());
                    Assert.Equal("SELECT 42 ", editor.Editor.Text);
                    Assert.Equal(6, editor.Editor.CaretOffset);
                    Assert.Same(result, document.ResultRun);
                    Assert.True(editor.Editor.CanUndo);
                    editor.Editor.Undo();
                    Assert.Equal("SELECT 42", editor.Editor.Text);
                }
                finally { Complete(workspace.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void QueryTabsCacheTheirOwnEditorAndResultStateRatherThanRecreatingCaptureDevices()
        {
            _application.Run(window =>
            {
                var workspace = new SqlQueryWorkspaceModel(new Sources(), window.Dispatcher);
                try
                {
                    Complete(workspace.RefreshSourcesAsync());
                    var first = workspace.NewDocument();
                    var view = new SqlQueryWorkspaceView();
                    view.AttachWorkspace(workspace);
                    WpfTestHost.Show(window, view);
                    var firstView = Descendants(view).OfType<SqlQueryEditorView>().Single();
                    firstView.Editor.Text = "SELECT first";
                    firstView.Editor.Select(7, 5);
                    var second = workspace.NewDocument();
                    WpfTestHost.Pump();
                    var secondView = Descendants(view).OfType<SqlQueryEditorView>()
                        .Single(editor => ReferenceEquals(editor.DataContext, second));
                    secondView.Editor.Text = "SELECT second";
                    workspace.SelectedDocument = first;
                    WpfTestHost.Pump();
                    Assert.Same(firstView, Descendants(view).OfType<SqlQueryEditorView>()
                        .Single(editor => ReferenceEquals(editor.DataContext, first)));
                    Assert.Equal("SELECT first", firstView.Editor.Text);
                    Assert.Equal(7, firstView.Editor.SelectionStart);
                    Assert.Equal(5, firstView.Editor.SelectionLength);
                    Assert.Equal("SELECT second", second.Text);
                }
                finally { Complete(workspace.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void F5RunsOnlyTheEditorSelectionAndDoesNotInvokeCaptureCommands()
        {
            _application.Run(window =>
            {
                var sources = new Sources();
                var workspace = new SqlQueryWorkspaceModel(sources, window.Dispatcher);
                try
                {
                    Complete(workspace.RefreshSourcesAsync());
                    var document = workspace.NewDocument();
                    var view = new SqlQueryDocumentView { DataContext = document };
                    WpfTestHost.Show(window, view);
                    var captures = 0;
                    window.CommandBindings.Add(new CommandBinding(AppCommands.AnalyzeCaptures, (_, _) => captures++));
                    view.EditorView.Editor.Text = "SELECT 1;\r\nSELECT 2;";
                    view.EditorView.Editor.Select(11, 9);
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                        Environment.TickCount, Key.F5)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    view.EditorView.Editor.RaiseEvent(key);
                    PumpUntil(() => sources.ExecutedSql.Count == 1 && !document.IsBusy);
                    Assert.True(key.Handled);
                    Assert.Equal("SELECT 2;", sources.ExecutedSql[0]);
                    Assert.Equal(11, document.ResultRun.EditorOffset);
                    Assert.Equal(0, captures);
                }
                finally { Complete(workspace.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void SchemaButtonsInsertAndGenerateRealSqlWithoutAutomaticallyExecutingIt()
        {
            _application.Run(window =>
            {
                var sources = new Sources();
                var workspace = new SqlQueryWorkspaceModel(sources, window.Dispatcher);
                try
                {
                    Complete(workspace.RefreshSourcesAsync());
                    var document = workspace.NewDocument();
                    var view = new SqlQueryDocumentView { DataContext = document };
                    WpfTestHost.Show(window, view);
                    PumpUntil(() => view.SchemaTree.Items.Count != 0);
                    var table = (TreeViewItem)view.SchemaTree.ItemContainerGenerator.ContainerFromIndex(0);
                    table.IsExpanded = true;
                    table.IsSelected = true;
                    WpfTestHost.Pump();
                    var generate = Descendants(view).OfType<Button>().Single(button =>
                        Equals(button.Content, Localization.GetLocalizedString("SQL_GENERATE_SELECT")));
                    generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("SELECT \"id\", \"some value\"\r\nFROM \"A table\";", view.EditorView.Editor.Text);
                    var preview = Descendants(view).OfType<Button>().Single(button =>
                        Equals(button.Content, Localization.GetLocalizedString("SQL_PREVIEW_TABLE")));
                    preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.EndsWith("LIMIT 1000;", document.Text);
                    var column = (TreeViewItem)table.ItemContainerGenerator.ContainerFromIndex(1);
                    column.IsSelected = true;
                    view.EditorView.Editor.Select(0, view.EditorView.Editor.Document.TextLength);
                    var insert = Descendants(view).OfType<Button>().Single(button =>
                        Equals(button.Content, Localization.GetLocalizedString("SQL_INSERT")));
                    insert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("\"some value\"", document.Text);
                    Assert.Empty(sources.ExecutedSql);
                }
                finally { Complete(workspace.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void ResultsUseOrdinalColumnsAndVirtualizationAndKeepSelectionsAcrossPageChanges()
        {
            _application.Run(window =>
            {
                var result = new Result(2001)
                {
                    Columns = [new(0, "", null, null, null), new(1, "same", null, null, null), new(2, "same", null, null, null)],
                    Cells = row => [new(QueryValueKind.Null, null, 0, false),
                        new(QueryValueKind.Integer, row, 8, false), new(QueryValueKind.Text, "", 0, false)]
                };
                var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
                var workspace = new SqlQueryWorkspaceModel(sources, window.Dispatcher);
                try
                {
                    Complete(workspace.RefreshSourcesAsync());
                    var document = workspace.NewDocument();
                    document.Text = "SELECT arbitrary";
                    var view = new SqlQueryResultsView { DataContext = document };
                    WpfTestHost.Show(window, view);
                    Assert.True(Complete(document.RunAsync()));
                    WpfTestHost.Pump();
                    Assert.Equal(3, view.ResultGrid.Columns.Count);
                    Assert.Equal(3, view.ResultGrid.Columns.Select(column => column.Header).Distinct().Count());
                    Assert.False(view.ResultGrid.CanUserSortColumns);
                    Assert.True(view.ResultGrid.EnableColumnVirtualization);
                    Assert.True(view.ResultGrid.EnableRowVirtualization);
                    Assert.Equal("Cells[1].Preview", ((System.Windows.Data.Binding)
                        ((DataGridTextColumn)view.ResultGrid.Columns[1]).Binding).Path.Path);
                    var first = document.PageRows[0];
                    view.ResultGrid.SelectedItems.Add(first);
                    Assert.Contains(0, document.SelectedRows);
                    Assert.True(Complete(document.MovePageAsync(true)));
                    WpfTestHost.Pump();
                    Assert.Contains(0, document.SelectedRows);
                    view.ResultGrid.SelectedItems.Add(document.PageRows[1]);
                    Assert.Contains(1001, document.SelectedRows);
                    Assert.True(Complete(document.MovePageAsync(false)));
                    WpfTestHost.Pump();
                    Assert.Equal(new long[] { 0, 1001 }, document.SelectedRows.Order().ToArray());
                    Assert.Equal(0, Assert.Single(view.ResultGrid.SelectedItems.Cast<SqlQueryResultRow>()).Ordinal);
                }
                finally { Complete(workspace.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void LocalCompletionPopupAndThemeRefreshDoNotChangeTheDraftOrUndoState()
        {
            _application.Run(window =>
            {
                var sources = new Sources();
                var workspace = new SqlQueryWorkspaceModel(sources, window.Dispatcher);
                try
                {
                    Complete(workspace.RefreshSourcesAsync());
                    var document = workspace.NewDocument();
                    Assert.True(Complete(document.RefreshSchemaAsync()));
                    var view = new SqlQueryEditorView { DataContext = document };
                    WpfTestHost.Show(window, view);
                    view.Editor.Text = "SELECT t.so FROM \"A table\" AS t";
                    view.Editor.CaretOffset = "SELECT t.so".Length;
                    view.ShowCompletion();
                    WpfTestHost.Pump();
                    var completion = Application.Current.Windows.OfType<CompletionWindow>().Single();
                    Assert.Equal("some value", Assert.Single(completion.CompletionList.CompletionData).Text);
                    completion.CompletionList.RequestInsertion(EventArgs.Empty);
                    WpfTestHost.Pump();
                    Assert.Equal("SELECT t.\"some value\" FROM \"A table\" AS t", view.Editor.Text);
                    var text = view.Editor.Text;
                    var caret = view.Editor.CaretOffset;
                    var highlighting = view.Editor.SyntaxHighlighting;
                    window.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/DarkTheme.xaml")
                    });
                    WpfTestHost.Pump();
                    Assert.NotSame(highlighting, view.Editor.SyntaxHighlighting);
                    Assert.Equal(((SolidColorBrush)window.FindResource("TextBoxBackgroundBrush")).Color,
                        ((SolidColorBrush)view.Editor.Background).Color);
                    Assert.Equal(text, view.Editor.Text);
                    Assert.Equal(caret, view.Editor.CaretOffset);
                    view.Editor.Text = "-- never complete inside comments";
                    view.Editor.CaretOffset = view.Editor.Document.TextLength;
                    view.ShowCompletion();
                    Assert.Empty(Application.Current.Windows.OfType<CompletionWindow>());
                }
                finally { Complete(workspace.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void CaptureMenuCommandsKeepExplicitWindowTargetsWhileTheSqlEditorHasFocus()
        {
            _application.Run(window =>
            {
                var root = ProductGrid();
                WpfTestHost.Show(window, root);
                var captures = 0;
                window.CommandBindings.Add(new CommandBinding(ApplicationCommands.Open,
                    (_, e) => { captures++; e.Handled = true; }, (_, e) => e.CanExecute = true));
                var sql = (SqlQueryWorkspaceView)root.FindName("SqlWorkspaceView");
                var workspace = new SqlQueryWorkspaceModel(new Sources(), window.Dispatcher);
                try
                {
                    Complete(workspace.RefreshSourcesAsync());
                    sql.AttachWorkspace(workspace);
                    workspace.NewDocument();
                    ((TabControl)root.FindName("WorkspaceTabs")).SelectedIndex = 1;
                    WpfTestHost.Pump();
                    Descendants(sql).OfType<SqlQueryEditorView>().Single().Editor.Focus();
                    var menu = (MenuItem)root.FindName("FileLoadCaptures");
                    Assert.Same(window, menu.CommandTarget);
                    ((RoutedCommand)menu.Command).Execute(menu.CommandParameter, menu.CommandTarget);
                    Assert.Equal(1, captures);
                    window.CommandBindings.Add(new CommandBinding(AppCommands.ClearAllCaptures,
                        (_, _) => captures++, (_, e) => e.CanExecute = true));
                    Assert.True(AppCommands.ClearAllCaptures.CanExecute(null, window));
                    Assert.False(AppCommands.ClearAllCaptures.CanExecute(null, sql));
                    Assert.Equal(2, ((TabControl)root.FindName("WorkspaceTabs")).Items.Count);
                }
                finally { Complete(workspace.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void AppOwnedWorkspaceIsLazyAndItsCloseDrainAndDisposalDoNotInstantiateIt()
        {
            _application.Run(window =>
            {
                var application = new CaptureAppModel(window.Dispatcher);
                Assert.False(application.HasSqlQueryWorkspace);
                Complete(application.PauseAndDrainSqlWorkspaceAsync());
                Complete(application.DisposeSqlWorkspaceAsync());
                Assert.False(application.HasSqlQueryWorkspace);
                var workspace = application.SqlQueryWorkspace;
                Assert.Same(workspace, application.SqlQueryWorkspace);
                Assert.True(application.HasSqlQueryWorkspace);
                Complete(application.DisposeSqlWorkspaceAsync());
            });
        }

        private static Grid ProductGrid()
        {
            using var stream = typeof(SqlQueryWorkspaceViewTests).Assembly.GetManifestResourceStream("XMAT.Tests.MainWindow.xaml");
            var document = XDocument.Load(stream);
            var source = document.Root.Element(Presentation + "Grid");
            var element = new XElement(source);
            foreach (var attribute in element.DescendantsAndSelf().Attributes().Where(attribute =>
                attribute.Name.LocalName is "Click" or "DeviceTabClosed" or "AnalysisTabAdded" or "AnalysisTabClosed").ToArray())
            {
                attribute.Remove();
            }
            var namespaces = new Dictionary<string, string>();
            foreach (var declaration in document.Root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                var value = declaration.Value;
                if (value.StartsWith("clr-namespace:", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                {
                    value += ";assembly=XboxMultiplayerAnalysisTool";
                    namespaces[declaration.Value] = value;
                }
                element.SetAttributeValue(declaration.Name, value);
            }
            foreach (var child in element.DescendantsAndSelf())
            {
                if (namespaces.TryGetValue(child.Name.NamespaceName, out var value))
                    child.Name = XName.Get(child.Name.LocalName, value);
            }
            return (Grid)XamlReader.Parse(element.ToString());
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject source)
        {
            yield return source;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
            {
                foreach (var child in Descendants(VisualTreeHelper.GetChild(source, index)))
                    yield return child;
            }
        }

        private static void Complete(Task task)
        {
            PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }
        private static T Complete<T>(Task<T> task)
        {
            PumpUntil(() => task.IsCompleted);
            return task.GetAwaiter().GetResult();
        }
        private static void PumpUntil(Func<bool> done)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!done())
            {
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), "SQL UI work did not finish.");
                WpfTestHost.Pump();
                Thread.Sleep(1);
            }
            WpfTestHost.Pump();
        }
    }
}
