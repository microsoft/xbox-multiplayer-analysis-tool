// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using XMAT.Operations.Replay;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Build form: makes a multipart/form-data body (RFC 7578) for the composer's request from text and file parts, in
    /// order, with the builder that xmat uses too (MultipartForm). The window edits its own list of parts, which starts as
    /// the parts of the composer's form, if any, or empty, and changes the composer only when Use form succeeds, in one step.
    /// Nothing that the user types is measured on the UI thread: picking a file and Use form measure off it, cancellably.
    /// </summary>
    public partial class MultipartFormWindow : Window
    {
        // The most characters of the method, a part's name or type, and a file's name, that the window shows (UntrustedText).
        private const int MaxShownChars = 64;
        private const int MaxShownFileNameChars = 260;

        private readonly Func<string> _headersText;
        private readonly Action<MultipartFormBody, string> _commit;

        // The parts as listed. An entry is never changed: editing the list replaces entries, so the seed never changes.
        private readonly List<Entry> _entries = new();

        // The cancellation of the pick or the Use form in progress; the window starts nothing else meanwhile. And whether the
        // window is closed, as closing the composer can do while the user picks a file.
        private CancellationTokenSource _work;
        private bool _closed;

        // method is the request's, which a form never changes. seed is the parts of the composer's form, to start with, or
        // null to start empty: they are used as they are, and a file part's bytes are never read again. headersText reads the
        // composer's header text, when Use form starts; commit gives the composer the form, with that header text in which
        // the form's Content-Type replaces every Content-Type line, and the window then closes.
        internal MultipartFormWindow(
            string method,
            IReadOnlyList<MultipartPart> seed,
            Func<string> headersText,
            Action<MultipartFormBody, string> commit)
        {
            ArgumentNullException.ThrowIfNull(headersText);
            ArgumentNullException.ThrowIfNull(commit);
            _headersText = headersText;
            _commit = commit;
            InitializeComponent();
            Title = Localization.GetLocalizedString("FORM_TITLE");
            MethodText.Text = Localization.GetLocalizedString("FORM_METHOD", UntrustedText.Show(method ?? "", MaxShownChars));
            // A form never changes the method: a method that usually has no body keeps it, and the window says so.
            if (method is "GET" or "HEAD")
            {
                MethodHintText.Text = Localization.GetLocalizedString("FORM_BODILESS_METHOD", method);
                MethodHintText.Visibility = Visibility.Visible;
            }

            IntroText.Text = Localization.GetLocalizedString("FORM_INTRO");
            PartsLabel.Content = Localization.GetLocalizedString("FORM_PARTS");
            RemovePartButton.Content = Localization.GetLocalizedString("FORM_REMOVE");
            MoveUpButton.Content = Localization.GetLocalizedString("FORM_MOVE_UP");
            MoveDownButton.Content = Localization.GetLocalizedString("FORM_MOVE_DOWN");
            NameLabel.Content = Localization.GetLocalizedString("FORM_NAME");
            TypeLabel.Content = Localization.GetLocalizedString("FORM_TYPE");
            TypeBox.ToolTip = Localization.GetLocalizedString("FORM_TYPE_HINT");
            ValueLabel.Content = Localization.GetLocalizedString("FORM_VALUE");
            AddTextButton.Content = Localization.GetLocalizedString("FORM_ADD_TEXT");
            AddTextButton.ToolTip = Localization.GetLocalizedString("FORM_ADD_TEXT_HINT");
            AddFileButton.Content = Localization.GetLocalizedString("FORM_ADD_FILE");
            AddFileButton.ToolTip = Localization.GetLocalizedString("FORM_ADD_FILE_HINT");
            UseFormButton.Content = Localization.GetLocalizedString("FORM_USE");
            UseFormButton.ToolTip = Localization.GetLocalizedString("FORM_USE_HINT");
            CancelWorkButton.Content = Localization.GetLocalizedString("FORM_CANCEL");
            CloseButton.Content = Localization.GetLocalizedString("FORM_CLOSE");
            // The list and each field have their own name for automation, as their labels read: the name that LabeledBy gives
            // exists only once something has made the label's automation peer, which a screen reader that starts at the
            // focused field has not.
            AutomationProperties.SetName(PartsList, Localization.GetLocalizedString("FORM_PARTS_LIST"));
            AutomationProperties.SetName(NameBox, Localization.GetLocalizedString("FORM_NAME_FIELD"));
            AutomationProperties.SetName(TypeBox, Localization.GetLocalizedString("FORM_TYPE_FIELD"));
            AutomationProperties.SetName(ValueBox, Localization.GetLocalizedString("FORM_VALUE_FIELD"));
            foreach (MultipartPart part in seed ?? Array.Empty<MultipartPart>())
                _entries.Add(new Entry(part.Name, part.Value, part.MediaType, part));
            ShowEntries(_entries.Count > 0 ? 0 : -1);
            ShowFieldReasons();
        }

        // Test seams, null in production: the file to add, or null for none, by default asked in a dialog; how it is read,
        // at most the bytes that the form has room for, by default ReplayBodyFile.ReadAsync; how Use form builds the form,
        // off the UI thread, by default MultipartForm.Build with RandomBytes and Progress; the random bytes of the form's
        // boundary, by default MultipartForm.Build's own; and Progress, given to each measurement and build that the window
        // runs, which calls it after every unit of its work.
        internal Func<string> ChooseFile { get; set; }

        internal Func<string, long, CancellationToken, Task<ReplayBodyRead>> ReadFile { get; set; }

        internal Func<IReadOnlyList<MultipartPart>, CancellationToken, MultipartFormBody> BuildBody { get; set; }

        internal Func<byte[]> RandomBytes { get; set; }

        internal MultipartProgress Progress { get; set; }

        // Whether a pick or Use form is in progress.
        internal bool IsBusy => _work != null;

        // Add text part: a part with the name, the type and the value as typed, at the end of the list. The value is kept as
        // it is and not measured here: Use form measures it, off the UI thread, and refuses text that is not valid.
        internal void AddTextPart()
        {
            if (!CanAdd(out string name, out string type))
                return;
            _entries.Add(new Entry(name, ValueBox.Text, type, null));
            ValueBox.Text = "";
            ShowEntries(_entries.Count - 1);
            StatusText.Text = Localization.GetLocalizedString("FORM_ADDED_TEXT", UntrustedText.Show(name, MaxShownChars));
        }

        // Add file part: a part with the name and the type as typed, and the bytes of a file that the user picks, read once,
        // now. Only the file's name is kept, never its path, so later changes to the file are not sent. Off the UI thread,
        // the room that the form has left is counted first (Reserved), and the file is read only with that room as its
        // limit, so that a file that does not fit is refused before it is read. The window is busy meanwhile; Cancel, or
        // closing the window, stops the work, which then reads no file, and a file read by then is not added.
        internal async Task AddFilePartAsync()
        {
            if (!CanAdd(out string name, out string type))
                return;
            string path = (ChooseFile ?? ChooseFileInDialog)();
            if (string.IsNullOrEmpty(path) || _closed)
                return;

            string fileName = Path.GetFileName(path);
            string shown = UntrustedText.Show(fileName, MaxShownFileNameChars);
            if (!MultipartForm.IsValidFileName(fileName))
            {
                StatusText.Text = Localization.GetLocalizedString("FORM_FILE_NAME_INVALID", shown);
                return;
            }

            Entry[] entries = _entries.ToArray();
            MultipartProgress progress = Progress;
            Func<string, long, CancellationToken, Task<ReplayBodyRead>> read = ReadFile ?? ReplayBodyFile.ReadAsync;
            using var picking = new CancellationTokenSource();
            CancellationToken token = picking.Token;
            Func<Task<ReplayBodyRead>> pick = async () =>
            {
                long room = MultipartForm.MaxBodyBytes - Reserved(entries, name, fileName, type, progress, token);
                return room < 0
                    ? ReplayBodyRead.Failed(ReplayBodyFile.TooLargeCode)
                    : await read(path, room, token).ConfigureAwait(false);
            };
            BeginWork(picking, Localization.GetLocalizedString("FORM_READING", shown));
            ReplayBodyRead file = null;
            bool stopped;
            try
            {
                file = await Task.Run(pick, token);
            }
            catch (OperationCanceledException) when (picking.IsCancellationRequested)
            {
                // Cancelled, or the window closed: nothing is added.
            }
            finally
            {
                // A file read by the time that the user cancelled is not added either.
                stopped = EndWork(picking);
            }

            if (stopped || !file.Succeeded)
            {
                StatusText.Text = Localization.GetLocalizedString(
                    stopped ? "FORM_ADD_CANCELLED" : ReadRefusal(file.Code), shown);
                return;
            }

            _entries.Add(new Entry(name, null, type, MultipartPart.File(name, fileName, file.Body, type)));
            ShowEntries(_entries.Count - 1);
            StatusText.Text = Localization.GetLocalizedString("FORM_ADDED_FILE", shown, file.Body.LongLength);
        }

        // Remove, Move up and Move down: the selected part leaves the list, or changes places with its neighbor.
        internal void RemovePart()
        {
            int index = PartsList.SelectedIndex;
            if (_work != null || index < 0)
                return;
            _entries.RemoveAt(index);
            ShowEntries(Math.Min(index, _entries.Count - 1));
        }

        internal void MovePart(int offset)
        {
            int index = PartsList.SelectedIndex;
            int target = index + offset;
            if (_work != null || index < 0 || target < 0 || target >= _entries.Count)
                return;
            (_entries[index], _entries[target]) = (_entries[target], _entries[index]);
            ShowEntries(target);
        }

        // Use form: builds the form from the parts as listed, then gives it to the composer, with the composer's header text
        // as it is when Use form starts, in which the form's Content-Type replaces every Content-Type line
        // (MultipartForm.WithContentType), and closes. Off the UI thread, the text parts as typed are made
        // (MultipartPart.Text), which measures them, and the form is built; the parts of the seed and the files picked are
        // used as they are, never read again. The window is busy meanwhile; Cancel, or closing the window, stops the work,
        // and a form built by then is not used. A form that cannot be built, or header text that cannot take its
        // Content-Type, changes nothing, and the status says why; the parts stay as they are, to try again.
        internal async Task UseFormAsync()
        {
            if (_work != null)
                return;
            if (_entries.Count == 0)
            {
                StatusText.Text = Localization.GetLocalizedString("FORM_EMPTY");
                return;
            }

            Entry[] entries = _entries.ToArray();
            string headers = _headersText();
            MultipartProgress progress = Progress;
            Func<byte[]> randomBytes = RandomBytes;
            Func<IReadOnlyList<MultipartPart>, CancellationToken, MultipartFormBody> build = BuildBody ??
                ((parts, cancellationToken) => MultipartForm.Build(parts, cancellationToken, randomBytes, progress));
            using var building = new CancellationTokenSource();
            CancellationToken token = building.Token;
            Func<(MultipartFormBody, string)> prepare = () =>
            {
                var parts = new MultipartPart[entries.Length];
                for (int index = 0; index < entries.Length; index++)
                {
                    Entry entry = entries[index];
                    parts[index] = entry.Part ?? MultipartPart.Text(entry.Name, entry.Value, entry.MediaType, token, progress);
                }

                MultipartFormBody built = build(parts, token);
                return (built, MultipartForm.WithContentType(headers, built.ContentType));
            };
            BeginWork(building, Localization.GetLocalizedString("FORM_BUILDING"));
            MultipartFormBody form = null;
            string prepared = null;
            string refusal = null;
            bool stopped;
            try
            {
                (form, prepared) = await Task.Run(prepare, token);
            }
            catch (OperationCanceledException) when (building.IsCancellationRequested)
            {
                // Cancelled, or the window closed: the composer is unchanged.
            }
            catch (MultipartFormException exception)
            {
                refusal = FormRefusal(exception.Code);
            }
            catch (FormatException)
            {
                // The composer's header text is not a valid header block, so the form's Content-Type cannot be put in it.
                refusal = "FORM_HEADERS_INVALID";
            }
            finally
            {
                // A form built by the time that the user cancelled is not used either.
                stopped = EndWork(building);
            }

            if (stopped || form == null)
            {
                StatusText.Text = Localization.GetLocalizedString(stopped ? "FORM_USE_CANCELLED" : refusal);
                return;
            }

            _commit(form, prepared);
            Close();
        }

        // Cancel: stops the pick or the Use form in progress; what it did by then is not used, and the parts stay as they
        // were.
        internal void CancelWork() => _work?.Cancel();

        // Closing stops the work in progress, whose result is then not used, and keeps nothing of the parts: the composer
        // keeps the form that it took, if any.
        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            CancelWork();
            _entries.Clear();
            PartsList.Items.Clear();
            base.OnClosed(e);
        }

        // What the form takes besides the bytes of the file to add, counted before the file is read: the closing line, the
        // new part's framing, and each listed part's framing and content. A part already made, of the seed or a file, has its
        // exact lengths; a text as typed is counted (MultipartForm.CountText) under the token, and charged three bytes a
        // character when it is not valid text, which Use form then refuses. A name or type that is not valid is charged at
        // its most (MultipartForm.PartFramingLength).
        private static long Reserved(
            IReadOnlyList<Entry> entries,
            string name,
            string fileName,
            string type,
            MultipartProgress progress,
            CancellationToken token)
        {
            long reserved = MultipartForm.ClosingLength + MultipartForm.PartFramingLength(true, name, fileName, type);
            foreach (Entry entry in entries)
            {
                if (entry.Part != null)
                {
                    reserved += entry.Part.FramingLength + entry.Part.ContentLength;
                    continue;
                }

                long count = MultipartForm.CountText(entry.Value, token, progress);
                reserved += MultipartForm.PartFramingLength(false, entry.Name, null, entry.MediaType) +
                    (count < 0 ? 3L * entry.Value.Length : count);
            }

            return reserved;
        }

        // Whether a part can be added now, with the name and the type as typed: the window is idle, the form has room for
        // another part, and both are valid. When not, the status says why.
        private bool CanAdd(out string name, out string type)
        {
            name = NameBox.Text;
            type = TypeBox.Text.Length == 0 ? null : TypeBox.Text;
            if (_work != null)
                return false;
            string reason = _entries.Count >= MultipartForm.MaxParts ? "FORM_TOO_MANY_PARTS"
                : !MultipartForm.IsValidName(name) ? "FORM_NAME_INVALID"
                : type != null && !MultipartForm.IsValidMediaType(type) ? "FORM_TYPE_INVALID"
                : null;
            if (reason != null)
                StatusText.Text = Localization.GetLocalizedString(reason);
            return reason == null;
        }

        private void BeginWork(CancellationTokenSource work, string status)
        {
            _work = work;
            StatusText.Text = status;
            UpdateActions();
        }

        // The window is idle again. True when the work that has just ended was stopped, by Cancel or closing, which can come
        // after the work last checked its token: then nothing that it returned is used.
        private bool EndWork(CancellationTokenSource work)
        {
            _work = null;
            UpdateActions();
            return work.IsCancellationRequested;
        }

        // The list as it is, each part by its name, what it holds and its type, in an item of its own, since two parts can
        // read the same; then the part to select, or -1 for none.
        private void ShowEntries(int select)
        {
            PartsList.Items.Clear();
            foreach (Entry entry in _entries)
                PartsList.Items.Add(new ListBoxItem { Content = Describe(entry) });
            PartsList.SelectedIndex = select;
            UpdateActions();
        }

        // A part as the list shows it: a text by its length in characters, which costs nothing to know, and a file by its
        // name and size. What the user typed or picked is shown escaped and cut short (UntrustedText).
        private static string Describe(Entry entry)
        {
            string name = UntrustedText.Show(entry.Name, MaxShownChars);
            string described = entry.Part?.Kind == MultipartPartKind.File
                ? Localization.GetLocalizedString(
                    "FORM_FILE_PART",
                    name,
                    UntrustedText.Show(entry.Part.FileName, MaxShownFileNameChars),
                    entry.Part.ContentLength)
                : Localization.GetLocalizedString("FORM_TEXT_PART", name, entry.Value.Length);
            return entry.MediaType == null
                ? described
                : Localization.GetLocalizedString(
                    "FORM_PART_TYPE", described, UntrustedText.Show(entry.MediaType, MaxShownChars));
        }

        // The name and the type are checked as they are typed, which costs no more than their own few characters, and the
        // reason is shown under each one that is not valid. The value is not looked at here.
        private void ShowFieldReasons()
        {
            string name = NameBox.Text;
            string type = TypeBox.Text;
            ShowReason(NameReason, name.Length == 0 || MultipartForm.IsValidName(name) ? null : "FORM_NAME_INVALID");
            ShowReason(TypeReason, type.Length == 0 || MultipartForm.IsValidMediaType(type) ? null : "FORM_TYPE_INVALID");
            UpdateActions();
        }

        private static void ShowReason(TextBlock reason, string key)
        {
            reason.Text = key == null ? null : Localization.GetLocalizedString(key);
            reason.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateActions()
        {
            bool idle = _work == null;
            int index = PartsList.SelectedIndex;
            string type = TypeBox.Text;
            bool canAdd = idle && _entries.Count < MultipartForm.MaxParts && MultipartForm.IsValidName(NameBox.Text) &&
                (type.Length == 0 || MultipartForm.IsValidMediaType(type));
            AddTextButton.IsEnabled = canAdd;
            AddFileButton.IsEnabled = canAdd;
            RemovePartButton.IsEnabled = idle && index >= 0;
            MoveUpButton.IsEnabled = idle && index > 0;
            MoveDownButton.IsEnabled = idle && index >= 0 && index < _entries.Count - 1;
            UseFormButton.IsEnabled = idle && _entries.Count > 0;
            CancelWorkButton.IsEnabled = _work != null;
        }

        // Why a form was not used, by its code: each text is fixed and holds nothing that the user gave. The parts were
        // checked as they were added, so only these can arise.
        private static string FormRefusal(string code) => code switch
        {
            MultipartCodes.InvalidText => "FORM_TEXT_INVALID",
            MultipartCodes.TooLarge => "FORM_TOO_LARGE",
            MultipartCodes.BoundaryCollision => "FORM_BOUNDARY_COLLISION",
            MultipartCodes.ContentTypeDropped => "FORM_CONTENT_TYPE_DROPPED",
            _ => "FORM_FAILED",
        };

        // Why a file was not added, by the reader's code.
        private static string ReadRefusal(string code) => code switch
        {
            ReplayBodyFile.TooLargeCode => "FORM_FILE_TOO_LARGE",
            ReplayBodyFile.NotFoundCode => "FORM_FILE_NOT_FOUND",
            _ => "FORM_FILE_UNREADABLE",
        };

        private string ChooseFileInDialog()
        {
            var dialog = new OpenFileDialog
            {
                Title = Localization.GetLocalizedString("FORM_ADD_FILE_TITLE"),
                Filter = Localization.GetLocalizedString("COMPOSER_UPLOAD_FILTER"),
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        private void AddText_Click(object sender, RoutedEventArgs e) => AddTextPart();
        private async void AddFile_Click(object sender, RoutedEventArgs e) => await AddFilePartAsync();
        private void RemovePart_Click(object sender, RoutedEventArgs e) => RemovePart();
        private void MoveUp_Click(object sender, RoutedEventArgs e) => MovePart(-1);
        private void MoveDown_Click(object sender, RoutedEventArgs e) => MovePart(1);
        private async void UseForm_Click(object sender, RoutedEventArgs e) => await UseFormAsync();
        private void CancelWork_Click(object sender, RoutedEventArgs e) => CancelWork();
        private void Field_TextChanged(object sender, TextChangedEventArgs e) => ShowFieldReasons();
        private void PartsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

        // One part of the list: its name, its value for a text part, its type or null, and the part that it already is, if
        // any: one of the seed's, or a file part, whose bytes were read once, when it was picked. A text part as typed is
        // made only by Use form.
        private sealed record Entry(string Name, string Value, string MediaType, MultipartPart Part);
    }
}
