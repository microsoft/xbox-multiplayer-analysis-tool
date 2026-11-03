// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using ICSharpCode.AvalonEdit.CodeCompletion;
using XMAT.Models;
using static XMAT.Tests.SqlQueryWorkspaceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class SqlQueryCompletionInsertionTests(CaptureUiApplicationFixture application)
    {
        [Theory]
        [InlineData("\"som\"")]
        [InlineData("`som`")]
        [InlineData("[som]")]
        public void CompletionReplacesTheEntireQuotedIdentifierAndSelectsTheInitialSuggestion(string partial)
        {
            application.Run(window =>
            {
                var sources = new Sources();
                var workspace = new SqlQueryWorkspaceModel(sources, window.Dispatcher);
                try
                {
                    workspace.RefreshSourcesAsync().GetAwaiter().GetResult();
                    var document = workspace.NewDocument();
                    Assert.True(document.RefreshSchemaAsync().GetAwaiter().GetResult());
                    var view = new SqlQueryEditorView { DataContext = document };
                    WpfTestHost.Show(window, view);
                    view.Editor.Text = "SELECT t." + partial + " FROM \"A table\" AS t";
                    view.Editor.CaretOffset = "SELECT t.".Length + partial.Length - 1;
                    view.ShowCompletion();
                    WpfTestHost.Pump();
                    var popup = Application.Current.Windows.OfType<CompletionWindow>().Single();
                    Assert.Equal("some value", Assert.Single(popup.CompletionList.CompletionData).Content);
                    popup.CompletionList.RequestInsertion(EventArgs.Empty);
                    WpfTestHost.Pump();
                    Assert.Equal("SELECT t.\"some value\" FROM \"A table\" AS t", document.Text);
                    Assert.Empty(sources.ExecutedSql);
                }
                finally { workspace.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            });
        }
    }
}
