// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using XMAT.Models;

namespace XMAT.SqlQueries
{
    internal static class SqlQueryDocumentDialogs
    {
        internal static async Task<bool> ConfirmUnsavedAsync(Window owner, SqlQueryDocumentModel document)
        {
            if (!document.IsDirty)
                return true;
            var answer = MessageBox.Show(owner,
                Localization.GetLocalizedString("SQL_UNSAVED_MESSAGE", document.Title),
                Localization.GetLocalizedString("SQL_UNSAVED_TITLE"), MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            return answer switch
            {
                MessageBoxResult.No => true,
                MessageBoxResult.Yes => await SaveAsync(owner, document, forClosing: true) && !document.IsDirty,
                _ => false
            };
        }

        internal static Task<bool> SaveAsync(
            Window owner, SqlQueryDocumentModel document, bool saveAs = false, bool forClosing = false)
        {
            var path = document.FilePath;
            if (saveAs || string.IsNullOrEmpty(path))
            {
                var dialog = new SaveFileDialog
                {
                    Title = Localization.GetLocalizedString("SQL_SAVE_TITLE"),
                    Filter = Localization.GetLocalizedString("SQL_FILE_FILTER"),
                    DefaultExt = ".sql",
                    FileName = path == null ? "query.sql" : Path.GetFileName(path),
                    AddExtension = true,
                    OverwritePrompt = true
                };
                if (dialog.ShowDialog(owner) != true)
                    return Task.FromResult(false);
                path = dialog.FileName;
            }
            return document.SaveFileAsync(path, File.Exists(path), forClosing);
        }

        internal static string PickSqlFile(Window owner)
        {
            var dialog = new OpenFileDialog
            {
                Title = Localization.GetLocalizedString("SQL_OPEN_TITLE"),
                Filter = Localization.GetLocalizedString("SQL_FILE_FILTER"),
                DefaultExt = ".sql",
                CheckFileExists = true,
                Multiselect = false
            };
            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }
    }
}
