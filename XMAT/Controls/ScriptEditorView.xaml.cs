// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.CodeAnalysis;
using Microsoft.Win32;
using XMAT.Scripting;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for ScriptEditorView.xaml
    /// </summary>
    public partial class ScriptEditorView : UserControl
    {
        private const string ScriptFileExtension = "txt";
        private readonly string ScriptFilter = $"{PublicUtilities.AppName} Script File (*.{ScriptFileExtension})|*.{ScriptFileExtension}";
        private ScriptModel _selectedScript;

        public ScriptCollection ScriptCollection
        {
            get { return (ScriptCollection)this.GetValue(ScriptCollectionProperty); }
            set { this.SetValue(ScriptCollectionProperty, value); }
        }
        public static readonly DependencyProperty ScriptCollectionProperty =
            DependencyProperty.Register("ScriptCollection", typeof(ScriptCollection), typeof(ScriptEditorView));

        public ScriptTypeCollection ScriptTypeCollection
        {
            get { return (ScriptTypeCollection)this.GetValue(ScriptTypeInfoProperty); }
            set { this.SetValue(ScriptTypeInfoProperty, value); }
        }
        public static readonly DependencyProperty ScriptTypeInfoProperty =
            DependencyProperty.Register("ScriptTypeCollection", typeof(ScriptTypeCollection), typeof(ScriptEditorView));

        public int TabSize
        {
            get { return (int)this.GetValue(TabSizeProperty); }
            set { this.SetValue(TabSizeProperty, value); }
        }
        public static readonly DependencyProperty TabSizeProperty =
            DependencyProperty.Register("TabSize", typeof(int), typeof(ScriptEditorView), new PropertyMetadata(4));

        // Follows ScriptLayout.IsStacked: whether the type reference is below the editor rather than beside it.
        private static readonly DependencyProperty TypesBelowEditorProperty =
            DependencyProperty.Register("TypesBelowEditor", typeof(bool), typeof(ScriptEditorView),
                new PropertyMetadata(false, (d, e) => ((ScriptEditorView)d).ArrangePanes((bool)e.NewValue)));

        // The type reference's size in the layout that is not in use, given back when the layout changes back. Below
        // the editor it starts tall enough for a few rows of types above their description.
        private (GridLength Size, double Min, bool IsNewLook) _typesBeside;
        private (GridLength Size, double Min) _typesBelow = (new GridLength(180), 120);

        public static string EnabledText
        {
            get { return Localization.GetLocalizedString("SCRIPT_CMD_ENABLED"); }
        }

        public static string DisabledText
        {
            get { return Localization.GetLocalizedString("SCRIPT_CMD_DISABLED"); }
        }

        public static string CompilingText
        {
            get { return Localization.GetLocalizedString("SCRIPT_CMD_COMPILING"); }
        }

        public ScriptEditorView()
        {
            InitializeComponent();

            ScriptLabel.Content = Localization.GetLocalizedString("SCRIPT_CONTENT_LABEL");
            EventSelectorLabel.Content = Localization.GetLocalizedString("SCRIPT_EVENT_LABEL");
            ScriptStatusLabel.Content = Localization.GetLocalizedString("SCRIPT_STATUS_LABEL");
            AutomationProperties.SetHelpText(ScriptEditor, Localization.GetLocalizedString("A11Y_SCRIPT_EDITOR_HELP"));
            AutomationProperties.SetName(TypeInfoTree, Localization.GetLocalizedString("A11Y_SCRIPT_TYPES_TREE"));
            AutomationProperties.SetName(TypeDescription, Localization.GetLocalizedString("A11Y_SCRIPT_TYPE_DESCRIPTION"));
            AutomationProperties.SetName(ScriptTypesSplitter, Localization.GetLocalizedString("A11Y_SPLITTER_SCRIPT_TYPES"));
            AutomationProperties.SetName(TypeDescriptionSplitter, Localization.GetLocalizedString("A11Y_SPLITTER_TYPE_DESCRIPTION"));
            SetBinding(TypesBelowEditorProperty, new Binding(nameof(ReflowPanel.IsStacked)) { Source = ScriptLayout });
            EventSelector.SelectionChanged += EventSelector_SelectionChanged;
            AttachSelectedScript(EventSelector.SelectedItem as ScriptModel);
        }

        // Beside the editor, the type reference and the divider before it have columns of their own; below it, rows.
        // The column or row not in use takes no room. SetCurrentValue keeps the column's look binding, so a later
        // switch to the original look still gives it the original proportions. A width kept from the other look is
        // dropped instead of restored, so the column takes this look's width.
        private void ArrangePanes(bool below)
        {
            if (below)
            {
                _typesBeside = (TypesColumn.Width, TypesColumn.MinWidth, LookState.Current.IsNewLook);
                TypesColumn.SetCurrentValue(ColumnDefinition.MinWidthProperty, 0.0);
                TypesColumn.SetCurrentValue(ColumnDefinition.WidthProperty, new GridLength(0));
                TypesRow.MinHeight = _typesBelow.Min;
                TypesRow.Height = _typesBelow.Size;
            }
            else
            {
                _typesBelow = (TypesRow.Height, TypesRow.MinHeight);
                TypesRow.MinHeight = 0;
                TypesRow.Height = new GridLength(0);
                TypesColumn.SetCurrentValue(ColumnDefinition.MinWidthProperty, _typesBeside.Min);
                if (_typesBeside.IsNewLook == LookState.Current.IsNewLook)
                {
                    TypesColumn.SetCurrentValue(ColumnDefinition.WidthProperty, _typesBeside.Size);
                }
                else
                {
                    TypesColumn.InvalidateProperty(ColumnDefinition.WidthProperty);
                }
            }

            Grid.SetRow(ScriptTypesSplitter, below ? 1 : 0);
            Grid.SetColumn(ScriptTypesSplitter, below ? 0 : 1);
            Grid.SetRow(TypeReferencePane, below ? 2 : 0);
            Grid.SetColumn(TypeReferencePane, below ? 0 : 2);
            ScriptTypesSplitter.ResizeDirection = below ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        }

        // The type reference takes at most half of the panes, so the editor stays the largest of them. A smaller tab
        // then shrinks a pane that was dragged larger instead of pushing the panes past its edge.
        private void ScriptPanes_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            TypesColumn.MaxWidth = Math.Max(0, (ScriptPanes.ActualWidth - ScriptPanes.ColumnDefinitions[1].ActualWidth) / 2);
            TypesRow.MaxHeight = Math.Max(0, (ScriptPanes.ActualHeight - ScriptPanes.RowDefinitions[1].ActualHeight) / 2);
        }

        private void LoadScript_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ScriptModel sm)
                return;

            var openFileDialog = new OpenFileDialog
            {
                Filter = ScriptFilter
            };

            if (openFileDialog.ShowDialog() == true && !string.IsNullOrEmpty(openFileDialog.FileName))
            {
                MessageBoxResult result = MessageBox.Show(Localization.GetLocalizedString("SCRIPT_OVERWRITE_MESSAGE"), Localization.GetLocalizedString("SCRIPT_OVERWRITE_TITLE"),
                                                          MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (result == MessageBoxResult.Yes)
                {
                    string script = File.ReadAllText(openFileDialog.FileName);
                    sm.Filename = openFileDialog.FileName;
                    sm.Script = script;
                }
            }
        }

        private void SaveScript_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ScriptModel sm)
                return;

            // if we don't already have a filename, get one, otherwise just overwrite the one we have
            if (string.IsNullOrEmpty(sm.Filename))
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = ScriptFilter
                };

                if (saveFileDialog.ShowDialog() == true && !string.IsNullOrEmpty(saveFileDialog.FileName))
                {
                    sm.Filename = saveFileDialog.FileName;
                }
            }

            if (!string.IsNullOrEmpty(sm.Filename))
                File.WriteAllText(sm.Filename, sm.Script);
        }

        private void RevertScript_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ScriptModel sm)
                return;

            MessageBoxResult result = MessageBox.Show(Localization.GetLocalizedString("SCRIPT_REVERT_MESSAGE"), Localization.GetLocalizedString("SCRIPT_REVERT_TITLE"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (result == MessageBoxResult.Yes)
            {
                sm.Script = File.ReadAllText(sm.Filename);
            }
        }

        private void RevertScript_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            if (e.Parameter is not ScriptModel sm)
                return;

            e.CanExecute = !string.IsNullOrEmpty(sm.Filename);
        }

        private async void ValidateScript_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ScriptModel sm)
                return;

            if (sm.IsCompiling)
                return;

            if (sm.IsEnabled)
            {
                sm.DisableScript();
                return;
            }

            await sm.EnableScriptAsync<WebServiceCaptureScriptParams>();
        }

        private void ValidateScript_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            if (e.Parameter is not ScriptModel sm)
                return;

            e.CanExecute = !sm.IsCompiling && !string.IsNullOrEmpty(sm.Script);
        }

        private void ScriptErrorSelected_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not Diagnostic diag)
                return;

            FileLinePositionSpan lineSpan = diag.Location.GetLineSpan();

            // set the caret to the right position in the textbox and select the proper amount of text
            ScriptEditor.CaretIndex = ScriptEditor.GetCharacterIndexFromLineIndex(lineSpan.StartLinePosition.Line) + lineSpan.StartLinePosition.Character;
            ScriptEditor.SelectionLength = lineSpan.EndLinePosition.Character - lineSpan.StartLinePosition.Character;
            ScriptEditor.Focus();
        }

        private void LoadSaveScript_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = e.Parameter is ScriptModel sm &&
                !sm.IsEnabled &&
                !sm.IsCompiling;
        }

        private void EventSelector_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            AttachSelectedScript(EventSelector.SelectedItem as ScriptModel);
            CommandManager.InvalidateRequerySuggested();
        }

        private void AttachSelectedScript(ScriptModel script)
        {
            if (ReferenceEquals(_selectedScript, script))
                return;

            if (_selectedScript != null)
            {
                PropertyChangedEventManager.RemoveHandler(
                    _selectedScript,
                    SelectedScript_PropertyChanged,
                    string.Empty);
            }

            _selectedScript = script;
            if (_selectedScript != null)
            {
                PropertyChangedEventManager.AddHandler(
                    _selectedScript,
                    SelectedScript_PropertyChanged,
                    string.Empty);
            }
        }

        private void SelectedScript_PropertyChanged(
            object sender,
            PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName is nameof(ScriptModel.IsEnabled) or
                    nameof(ScriptModel.IsCompiling) or
                    nameof(ScriptModel.Script) or
                    nameof(ScriptModel.Filename))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // always set focus back to the editor, even if we bail out below
            ScriptEditor.Focus();
            InsertTypePath(sender as TreeViewItem);
        }

        // Enter on a type or member inserts its path, as a double-click does.
        private void TreeViewItem_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TreeViewItem { IsSelected: true } tvi)
                return;

            e.Handled = true;
            ScriptEditor.Focus();
            InsertTypePath(tvi);
        }

        private void InsertTypePath(TreeViewItem tvi)
        {
            if (tvi == null || !tvi.IsSelected)
                return;

            if (tvi.Tag is not ScriptTypeInfo sti)
                return;

            object parent;
            DependencyObject child = tvi;
            string path = sti.Name;

            do
            {
                parent = VisualTreeHelper.GetParent(child);
                if (parent is TreeViewItem parentTvi)
                {
                    if (parentTvi.Tag is ScriptTypeInfo parentSti)
                        path = path.Insert(0, $"{parentSti.Name}.");
                }

                child = parent as DependencyObject;
            } while (parent != null);

            // TODO / FIXME: this is a hack to make WebCapture scriptiong work better,
            // and likely would be incorrect for other scripting usage.  It assumes the
            // "base" parameter object is named "Params".  Bleech.
            // Overall, this + ScriptTypeCollection/ScriptTypeInfo should be rewritten
            // to better setup the parent/child relationship.
            if (!path.StartsWith("Params."))
                path = "Params." + path;

            ScriptEditor.SelectedText = path;
            ScriptEditor.CaretIndex += path.Length;
        }

        private void ScriptEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // Tab indents the script, so Ctrl+Tab and Ctrl+Shift+Tab move focus out of the editor.
                e.Handled = true;
                ScriptEditor.MoveFocus(new TraversalRequest((Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    ? FocusNavigationDirection.Previous
                    : FocusNavigationDirection.Next));
                return;
            }

            if (e.Key == Key.Tab)
            {
                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    if (ScriptEditor.CaretIndex >= TabSize)
                    {
                        int start = ScriptEditor.CaretIndex - TabSize;
                        string s = ScriptEditor.Text.Substring(start, TabSize);
                        if (s == new string(' ', TabSize))
                        {
                            ScriptEditor.Text = ScriptEditor.Text.Remove(start, TabSize);
                            ScriptEditor.CaretIndex = start;
                        }
                    }
                }
                else
                {
                    int pos = ScriptEditor.CaretIndex;
                    ScriptEditor.Text = ScriptEditor.Text.Insert(ScriptEditor.CaretIndex, new string(' ', TabSize));
                    ScriptEditor.CaretIndex = pos + TabSize;
                }
                e.Handled = true;
            }
        }
    }
}
