// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.Scripting;
using System.Windows.Media;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for ScriptEditorView.xaml
    /// </summary>
    public partial class ScriptEditorView : UserControl
    {
        private const string ScriptFileExtension = "txt";
        private readonly string ScriptFilter = $"{PublicUtilities.AppName} Script File (*.{ScriptFileExtension})|*.{ScriptFileExtension}";

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

        public static string EnabledText
        {
            get { return Localization.GetLocalizedString("SCRIPT_CMD_ENABLED"); }
        }

        public static string DisabledText
        {
            get { return Localization.GetLocalizedString("SCRIPT_CMD_DISABLED"); }
        }

        public ScriptEditorView()
        {
            InitializeComponent();

            ScriptSuccess.Text = Localization.GetLocalizedString("SCRIPT_SUCCESS");
            ScriptLabel.Content = Localization.GetLocalizedString("SCRIPT_CONTENT_LABEL");
            EventSelectorLabel.Content = Localization.GetLocalizedString("SCRIPT_EVENT_LABEL");
            ScriptStatusLabel.Content = Localization.GetLocalizedString("SCRIPT_STATUS_LABEL");
        }

        private void LoadScript_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ScriptModel sm)
                return;

            var openFileDialog = new OpenFileDialog
            {
                Filter = ScriptFilter
            };

            if(openFileDialog.ShowDialog() == true && !string.IsNullOrEmpty(openFileDialog.FileName))
            {
                MessageBoxResult result = MessageBox.Show(Localization.GetLocalizedString("SCRIPT_OVERWRITE_MESSAGE"), Localization.GetLocalizedString("SCRIPT_OVERWRITE_TITLE"),
                                                          MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if(result == MessageBoxResult.Yes)
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
            if(string.IsNullOrEmpty(sm.Filename))
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

            if(!string.IsNullOrEmpty(sm.Filename))
                File.WriteAllText(sm.Filename, sm.Script);
        }

        private void RevertScript_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ScriptModel sm)
                return;

            MessageBoxResult result = MessageBox.Show(Localization.GetLocalizedString("SCRIPT_REVERT_MESSAGE"), Localization.GetLocalizedString("SCRIPT_REVERT_TITLE"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if(result == MessageBoxResult.Yes)
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
            if(ScriptEnabled.IsChecked.GetValueOrDefault())
            {
                if (e.Parameter is not ScriptModel sm)
                    return;

                await sm.CompileScriptAsync<WebServiceCaptureScriptParams>();

                if(sm.CompilationStatus.Any(x => x.Severity == DiagnosticSeverity.Error))
                {
                    ScriptEnabled.IsChecked = false;
                    ScriptSuccess.Visibility = Visibility.Collapsed;
                    ScriptOutput.Visibility = Visibility.Visible;
                }
                else
                {
                    ScriptOutput.Visibility = Visibility.Collapsed;
                    ScriptSuccess.Visibility = Visibility.Visible;
                }

                ScriptEnabled.IsEnabled = true;
            }
        }

        private void ValidateScript_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            if (e.Parameter is not ScriptModel sm)
                return;

            e.CanExecute = !(sm == null || string.IsNullOrEmpty(sm.Script));
        }

        private void ScriptErrorSelected_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if(e.Parameter is not Diagnostic diag)
                return;

            FileLinePositionSpan lineSpan = diag.Location.GetLineSpan();

            // set the caret to the right position in the textbox and select the proper amount of text
            ScriptEditor.CaretIndex = ScriptEditor.GetCharacterIndexFromLineIndex(lineSpan.StartLinePosition.Line) + lineSpan.StartLinePosition.Character;
            ScriptEditor.SelectionLength = lineSpan.EndLinePosition.Character - lineSpan.StartLinePosition.Character;
            ScriptEditor.Focus();
        }

        private void LoadSaveScript_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = !ScriptEnabled.IsChecked.GetValueOrDefault();
        }

        private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // always set focus back to the editor, even if we bail out below
            ScriptEditor.Focus();

            if(sender is not TreeViewItem tvi)
                return;

            if(!tvi.IsSelected)
                return;

            if(tvi.Tag is not ScriptTypeInfo sti)
                return;

            object parent;
            DependencyObject child = tvi;
            string path = sti.Name;

            do
            {
                parent = VisualTreeHelper.GetParent(child);
                if(parent is TreeViewItem parentTvi)
                {
                    if(parentTvi.Tag is ScriptTypeInfo parentSti)
                        path = path.Insert(0, $"{parentSti.Name}.");
                }
                
                child = parent as DependencyObject;
            } while (parent != null);

            // TODO / FIXME: this is a hack to make WebCapture scriptiong work better,
            // and likely would be incorrect for other scripting usage.  It assumes the
            // "base" parameter object is named "Params".  Bleech.
            // Overall, this + ScriptTypeCollection/ScriptTypeInfo should be rewritten
            // to better setup the parent/child relationship.
            if(!path.StartsWith("Params."))
                path = "Params." + path;

            ScriptEditor.SelectedText = path;
            ScriptEditor.CaretIndex  += path.Length;
        }

        private void ScriptEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if(e.Key == Key.Tab)
            {
                if(Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    if(ScriptEditor.CaretIndex >= TabSize)
                    {
                        int start = ScriptEditor.CaretIndex - TabSize;
                        string s = ScriptEditor.Text.Substring(start, TabSize);
                        if(s == new string(' ', TabSize))
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
