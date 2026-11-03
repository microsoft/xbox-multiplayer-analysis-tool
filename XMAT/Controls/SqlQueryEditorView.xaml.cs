// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using XMAT.Models;
using XMAT.SqlQueries;

namespace XMAT
{
    public partial class SqlQueryEditorView : UserControl
    {
        private static readonly DependencyProperty KeywordBrushProperty = DependencyProperty.Register(
            "KeywordBrush", typeof(SolidColorBrush), typeof(SqlQueryEditorView),
            new PropertyMetadata(Brushes.Blue, SyntaxBrushChanged));
        private static readonly DependencyProperty StringBrushProperty = DependencyProperty.Register(
            "StringBrush", typeof(SolidColorBrush), typeof(SqlQueryEditorView),
            new PropertyMetadata(Brushes.Brown, SyntaxBrushChanged));
        private static readonly DependencyProperty CommentBrushProperty = DependencyProperty.Register(
            "CommentBrush", typeof(SolidColorBrush), typeof(SqlQueryEditorView),
            new PropertyMetadata(Brushes.Green, SyntaxBrushChanged));
        private static readonly DependencyProperty NumberBrushProperty = DependencyProperty.Register(
            "NumberBrush", typeof(SolidColorBrush), typeof(SqlQueryEditorView),
            new PropertyMetadata(Brushes.DarkCyan, SyntaxBrushChanged));

        private SqlQueryDocumentModel _document;
        private CompletionWindow _completion;
        private bool _updatingText;
        public event EventHandler RunRequested;

        public SqlQueryEditorView()
        {
            InitializeComponent();
            Editor.Options.ConvertTabsToSpaces = true;
            Editor.Options.IndentationSize = 4;
            Editor.TextArea.TextEntered += TextArea_TextEntered;
            Editor.TextArea.TextEntering += TextArea_TextEntering;
            Editor.TextArea.Caret.PositionChanged += (_, _) => SaveEditorState();
            Editor.TextArea.SelectionChanged += (_, _) => SaveEditorState();
            Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SaveEditorState();
            SetResourceReference(KeywordBrushProperty, "SQL_KeywordBrush");
            SetResourceReference(StringBrushProperty, "SQL_StringBrush");
            SetResourceReference(CommentBrushProperty, "SQL_CommentBrush");
            SetResourceReference(NumberBrushProperty, "SQL_NumberBrush");
            RefreshHighlighting();
        }

        private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            SaveEditorState();
            if (_document != null)
                _document.PropertyChanged -= Document_PropertyChanged;
            _completion?.Close();
            _document = e.NewValue as SqlQueryDocumentModel;
            if (_document != null)
                _document.PropertyChanged += Document_PropertyChanged;
            RestoreEditorState();
        }

        private void Document_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SqlQueryDocumentModel.Text) && !_updatingText)
                RestoreEditorState();
            if (e.PropertyName == nameof(SqlQueryDocumentModel.Schema))
                _completion?.Close();
        }

        private void View_Loaded(object sender, RoutedEventArgs e) => RestoreEditorState();

        private void RestoreEditorState()
        {
            _updatingText = true;
            try
            {
                if (Editor.Text != (_document?.Text ?? string.Empty))
                {
                    Editor.Text = _document?.Text ?? string.Empty;
                    Editor.Document.UndoStack.ClearAll();
                }
                if (_document == null)
                    return;
                var caret = Math.Clamp(_document.CaretOffset, 0, Editor.Document.TextLength);
                var start = Math.Clamp(_document.SelectionStart, 0, Editor.Document.TextLength);
                Editor.Select(start, Math.Clamp(_document.SelectionLength, 0, Editor.Document.TextLength - start));
                Editor.CaretOffset = caret;
                Editor.ScrollToVerticalOffset(_document.VerticalOffset);
                Editor.ScrollToHorizontalOffset(_document.HorizontalOffset);
            }
            finally
            {
                _updatingText = false;
            }
        }

        private void SaveEditorState()
        {
            if (_updatingText || _document == null)
                return;
            _document.CaretOffset = Editor.CaretOffset;
            _document.SelectionStart = Editor.SelectionStart;
            _document.SelectionLength = Editor.SelectionLength;
            _document.VerticalOffset = Editor.VerticalOffset;
            _document.HorizontalOffset = Editor.HorizontalOffset;
        }

        private void Editor_TextChanged(object sender, EventArgs e)
        {
            if (_updatingText || _document == null)
                return;
            _updatingText = true;
            try
            { _document.Text = Editor.Text; }
            finally { _updatingText = false; }
            SaveEditorState();
        }

        internal void ReplaceSql(string sql)
        {
            Editor.Document.Replace(0, Editor.Document.TextLength, sql);
            Editor.CaretOffset = 0;
            Editor.Select(0, 0);
            Editor.Focus();
            SaveEditorState();
        }

        internal void InsertSql(string sql)
        {
            var start = Editor.SelectionStart;
            Editor.Document.Replace(start, Editor.SelectionLength, sql);
            Editor.Select(start + sql.Length, 0);
            Editor.CaretOffset = start + sql.Length;
            Editor.Focus();
            SaveEditorState();
        }

        private void View_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _completion != null)
            {
                _completion.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.F5 || e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                RunRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ShowCompletion();
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.A or Key.X)
            {
                if (e.Key == Key.A)
                    Editor.SelectAll();
                else
                    Editor.Cut();
                e.Handled = true;
            }
        }

        private void TextArea_TextEntered(object sender, TextCompositionEventArgs e)
        {
            if (e.Text == "." && _completion == null)
                ShowCompletion();
        }

        private void TextArea_TextEntering(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Length != 0 && _completion != null &&
                !char.IsLetterOrDigit(e.Text[0]) && e.Text[0] is not ('_' or '$' or '"'))
            {
                _completion.CompletionList.RequestInsertion(e);
            }
        }

        internal void ShowCompletion()
        {
            _completion?.Close();
            if (_document == null)
                return;
            var context = SqlQueryText.Complete(Editor.Text, Editor.CaretOffset, _document.Schema);
            if (context.Items.Count == 0)
                return;
            var window = new CompletionWindow(Editor.TextArea)
            {
                StartOffset = context.Start,
                Background = (Brush)FindResource("PanelBackgroundBrush"),
                Foreground = (Brush)FindResource("PrimaryForegroundBrush"),
                CloseAutomatically = true
            };
            var quote = context.Start < Editor.CaretOffset && Editor.Text[context.Start] is '"' or '`' or '['
                ? (char?)Editor.Text[context.Start] : null;
            foreach (var item in context.Items)
            {
                window.CompletionList.CompletionData.Add(new Completion(item, quote));
            }
            _completion = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_completion, window))
                    _completion = null;
            };
            window.Show();
            window.CompletionList.SelectItem(Editor.Text[context.Start..Editor.CaretOffset]);
        }

        private static void SyntaxBrushChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
            ((SqlQueryEditorView)sender).RefreshHighlighting();

        private void RefreshHighlighting()
        {
            if (Editor == null)
                return;
            _completion?.Close();
            Editor.SyntaxHighlighting = new SqlHighlighting(
                ((SolidColorBrush)GetValue(KeywordBrushProperty)).Color,
                ((SolidColorBrush)GetValue(StringBrushProperty)).Color,
                ((SolidColorBrush)GetValue(CommentBrushProperty)).Color,
                ((SolidColorBrush)GetValue(NumberBrushProperty)).Color);
        }

        private sealed class Completion(SqlQueryCompletion completion, char? quote) : ICompletionData
        {
            public ImageSource Image => null;
            public string Text => quote switch
            {
                '"' => SqlQueryText.QuoteIdentifier(completion.Text),
                '`' => "`" + completion.Text.Replace("`", "``") + "`",
                '[' => "[" + completion.Text + "]",
                _ => completion.Text
            };
            public object Content => completion.Text;
            public object Description => completion.Description;
            public double Priority => 0;

            public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            {
                var context = SqlQueryText.Complete(textArea.Document.Text, textArea.Caret.Offset, []);
                var length = context.Items.Count == 0 && context.Length == 0
                    ? completionSegment.Length : context.Length;
                var start = context.Length == 0 ? completionSegment.Offset : context.Start;
                textArea.Document.Replace(start, length, completion.Insertion);
                textArea.Caret.Offset = start + completion.Insertion.Length;
            }
        }

        private sealed class SqlHighlighting : IHighlightingDefinition
        {
            public string Name => "SQLite";
            public HighlightingRuleSet MainRuleSet { get; } = new();
            public IEnumerable<HighlightingColor> NamedHighlightingColors => [];
            public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
            public HighlightingColor GetNamedColor(string name) => null;
            public HighlightingRuleSet GetNamedRuleSet(string name) => null;

            internal SqlHighlighting(Color keyword, Color text, Color comment, Color number)
            {
                MainRuleSet.Spans.Add(new HighlightingSpan
                {
                    StartExpression = new Regex("--", RegexOptions.CultureInvariant),
                    EndExpression = new Regex("$", RegexOptions.CultureInvariant),
                    SpanColor = Color(comment)
                });
                MainRuleSet.Spans.Add(new HighlightingSpan
                {
                    StartExpression = new Regex(@"/\*", RegexOptions.CultureInvariant),
                    EndExpression = new Regex(@"\*/", RegexOptions.CultureInvariant),
                    SpanColor = Color(comment)
                });
                foreach (var quotes in new[] { ("'", "'"), ("\"", "\""), ("`", "`"), ("\\[", "\\]") })
                {
                    MainRuleSet.Spans.Add(new HighlightingSpan
                    {
                        StartExpression = new Regex(quotes.Item1, RegexOptions.CultureInvariant),
                        EndExpression = new Regex(quotes.Item2, RegexOptions.CultureInvariant),
                        SpanColor = Color(text)
                    });
                }
                MainRuleSet.Rules.Add(new HighlightingRule
                {
                    Regex = new Regex(@"\b(?:" + string.Join("|", SqlQueryText.Keywords.Select(Regex.Escape)) + @")\b",
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
                    Color = Color(keyword)
                });
                MainRuleSet.Rules.Add(new HighlightingRule
                {
                    Regex = new Regex(@"\b(?:0x[0-9a-f]+|\d+(?:\.\d+)?(?:e[+-]?\d+)?)\b",
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
                    Color = Color(number)
                });
            }

            private static HighlightingColor Color(Color value) => new() { Foreground = new SimpleHighlightingBrush(value) };
        }
    }
}
