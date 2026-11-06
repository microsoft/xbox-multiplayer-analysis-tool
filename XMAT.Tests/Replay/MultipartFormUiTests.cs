// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // Build form (Slice I4): the composer's multipart form dialog. What Use form puts in the composer, and what it never
    // changes; the work that picking a file and Use form run off the UI thread, the room that a file is read with, and how
    // that work stops; how long the composer keeps a form's parts; and the refusal to send or save a form whose
    // Content-Type a Connection header would drop. Requests go to loopback servers only, and files to folders under the
    // test output.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class MultipartFormUiTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        // The random bytes of a boundary, the same for every draw, so that a form's bytes can be built again here.
        private static readonly byte[] Random16 = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();

        // A text that measuring, scanning and writing each take three units of work for (MultipartForm.WorkChunk).
        private static readonly string LargeText = new('a', 3 * MultipartForm.WorkChunk);

        // A template request file, as XMAT saves one: it keeps the method and the HTTP version, and leaves out the URL, the
        // headers and the body.
        private const string TemplateFile =
            "{ \"schema\": \"xmat-request/1\", \"profile\": \"template\", \"method\": \"POST\", \"url\": null, " +
            "\"httpVersion\": \"auto\", \"headers\": null, \"body\": null, \"omitted\": [\"url\", \"headers\", \"body\"] }";

        // A request file with its content: a GET without headers or a body.
        private const string ContentFile =
            "{ \"schema\": \"xmat-request/1\", \"profile\": \"content\", \"method\": \"GET\", " +
            "\"url\": \"http://127.0.0.1:9/opened\", \"httpVersion\": \"auto\", \"headers\": [], \"body\": \"\", \"omitted\": [] }";

        private readonly CaptureUiApplicationFixture _application;

        public MultipartFormUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static MultipartFormUiTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // Use form puts the form in the composer in one step: the header text, with the form's Content-Type in place of the
        // Content-Type line and the other lines as they were; the body, held as the form's exact bytes and shown by its parts
        // and size; and the parts, to build it from again. It is an edit: the method, the URL, the transport options, Inspect
        // session, the response, where the request comes from and the request as loaded stay as they were. The file was read
        // off the UI thread when it was added, and a send sends exactly the form that the parts make with that boundary.
        [Fact]
        public Task UseForm_PutsTheHeadersBodyAndPartsInTheComposer_AsOneEdit_AndASendSendsExactlyThatForm() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("stored"));
            using var client = new RequestReplayClient();
            var window = new RequestComposerWindow(Capture(server.Port, "POST"), client, _ => true);
            byte[] file = { 0, 1, 2, 13, 10, 255 };
            int uiThread = Environment.CurrentManagedThreadId;
            var readOn = new List<int>();
            try
            {
                ChooseVersion(window, ReplayHttpVersion.Http11);
                Find<CheckBox>(window, "InspectSessionBox").IsChecked = true;
                await window.SendAsync();
                Assert.NotNull(window.LastResult);
                string headers = Find<TextBox>(window, "HeadersBox").Text;
                string unformed = Unformed(window);
                var kept = KeptBy(window);

                MultipartFormWindow dialog = OpenForm(window);
                dialog.RandomBytes = () => Random16.ToArray();
                dialog.ReadFile = (_, _, _) =>
                {
                    lock (readOn)
                    {
                        readOn.Add(Environment.CurrentManagedThreadId);
                    }

                    return Task.FromResult(new ReplayBodyRead(file.ToArray(), null));
                };
                string note = AddText(dialog, "note", "h\u00e9llo\r\nworld");
                await AddFileAsync(dialog, "upload", Path.Join(AppContext.BaseDirectory, "data.bin"), "application/x-test");

                await dialog.UseFormAsync();

                MultipartFormBody expected = MultipartForm.Build(
                    new[]
                    {
                        MultipartPart.Text("note", note, null, CancellationToken.None),
                        MultipartPart.File("upload", "data.bin", file, "application/x-test"),
                    },
                    CancellationToken.None,
                    () => Random16.ToArray());
                string parts = Localization.GetLocalizedString("COMPOSER_FORM_PARTS", 2);
                Assert.Null(Field<MultipartFormWindow>(window, "_formDialog"));
                Assert.Equal(MultipartForm.WithContentType(headers, expected.ContentType), Find<TextBox>(window, "HeadersBox").Text);
                Assert.Equal(expected.Body, Field<byte[]>(window, "_fileBody"));
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_BODY_FORM", parts, expected.Body.LongLength),
                    Find<TextBlock>(window, "BodyFileText").Text);
                Assert.Equal(Visibility.Collapsed, Find<TextBox>(window, "BodyBox").Visibility);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_FORM_USED", parts, expected.Body.LongLength), Status(window));
                IReadOnlyList<MultipartPart> seed = Field<IReadOnlyList<MultipartPart>>(window, "_formSeed");
                Assert.Equal(new[] { "note", "upload" }, seed.Select(part => part.Name));
                Assert.Equal(file, seed[1].Content.ToArray());
                Assert.Equal(unformed, Unformed(window));
                Assert.Equal(kept, KeptBy(window));
                Assert.NotEqual(uiThread, Assert.Single(readOn));

                await window.SendAsync();

                Assert.Equal(2, server.Requests.Count);
                LoopbackRequest sent = server.Requests[^1];
                Assert.Equal(("POST", "/upload", "canary"), (sent.Method, sent.Target, sent.Header("X-Token")));
                Assert.Equal(
                    expected.ContentType,
                    Assert.Single(sent.Headers, header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value);
                Assert.Equal(expected.Body, sent.Body);
            }
            finally
            {
                window.Close();
            }
        });

        // The dialog shows the request's method, which a form never changes: for GET and HEAD, which usually have no body,
        // with a hint that says so. A form is built for them as for any other method.
        [Fact]
        public Task TheDialog_ShowsTheMethod_WithAHintForGetAndHead_AndUseFormKeepsTheMethod() => _application.RunAsync(async () =>
        {
            var window = new RequestComposerWindow(Capture(9, "GET"), confirm: NoQuestion);
            try
            {
                MultipartFormWindow dialog = OpenForm(window);
                var hint = Find<TextBlock>(dialog, "MethodHintText");
                Assert.Equal(Localization.GetLocalizedString("FORM_METHOD", "GET"), Find<TextBlock>(dialog, "MethodText").Text);
                Assert.Equal(
                    (Visibility.Visible, Localization.GetLocalizedString("FORM_BODILESS_METHOD", "GET")), (hint.Visibility, hint.Text));
                AddText(dialog, "a", "1");

                await dialog.UseFormAsync();

                Assert.Equal("GET", Find<TextBox>(window, "MethodBox").Text);
                Assert.NotNull(Field<byte[]>(window, "_fileBody"));
                foreach ((string method, Visibility shown) in new[] { ("HEAD", Visibility.Visible), ("POST", Visibility.Collapsed) })
                {
                    MultipartFormWindow other = Dialog(method: method);
                    Assert.Equal(shown, Find<TextBlock>(other, "MethodHintText").Visibility);
                    other.Close();
                }
            }
            finally
            {
                window.Close();
            }
        });

        // Header text that is not valid, or whose Connection header names Content-Type, cannot take the form's Content-Type:
        // Use form then changes nothing in the composer, says why, and keeps the parts. Once the headers are fixed, Use form
        // builds the form from the same parts, without reading the file again.
        [Theory]
        [InlineData("malformed")]
        [InlineData("dropped")]
        public Task HeadersThatCannotTakeTheContentType_ChangeNothing_AndRetryingReadsNoFile(string headers) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            int reads = 0;
            try
            {
                var headersBox = Find<TextBox>(window, "HeadersBox");
                headersBox.Text = headers == "malformed" ? "not a header line" : "Connection: keep-alive, CONTENT-TYPE";
                string unformed = Unformed(window);
                string request = HeadersAndBody(window);
                var kept = KeptBy(window);
                MultipartFormWindow dialog = OpenForm(window);
                dialog.ReadFile = (_, _, _) =>
                {
                    Interlocked.Increment(ref reads);
                    return Task.FromResult(new ReplayBodyRead(new byte[] { 1, 2, 3 }, null));
                };
                await AddFileAsync(dialog, "upload", Path.Join(AppContext.BaseDirectory, "data.bin"));

                await dialog.UseFormAsync();

                Assert.Equal(
                    Localization.GetLocalizedString(headers == "malformed" ? "FORM_HEADERS_INVALID" : "FORM_CONTENT_TYPE_DROPPED"),
                    Status(dialog));
                Assert.Same(dialog, Field<MultipartFormWindow>(window, "_formDialog"));
                Assert.Single(Listed(dialog));
                Assert.Equal((unformed, request), (Unformed(window), HeadersAndBody(window)));
                Assert.Equal(kept, KeptBy(window));
                Assert.Null(Field<byte[]>(window, "_fileBody"));
                Assert.Null(Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));

                headersBox.Text = "X-Kept: yes";
                await dialog.UseFormAsync();

                Assert.Null(Field<MultipartFormWindow>(window, "_formDialog"));
                Assert.StartsWith("X-Kept: yes\r\nContent-Type: multipart/form-data; boundary=xmat-form-", headersBox.Text);
                Assert.Single(Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
                Assert.Equal(1, reads);
            }
            finally
            {
                window.Close();
            }
        });

        // Cancel while Use form measures a large text stops it within that unit of work: nothing is built, and the composer
        // keeps its request, its form and the form's parts.
        [Fact]
        public Task CancellingUseFormWhileItMeasuresText_BuildsAndCommitsNothing_AndKeepsTheFormsParts() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var units = new List<MultipartStage>();
            try
            {
                MultipartFormWindow first = OpenForm(window);
                AddText(first, "kept", "1");
                await first.UseFormAsync();
                string request = HeadersAndBody(window);
                byte[] body = Field<byte[]>(window, "_fileBody");
                IReadOnlyList<MultipartPart> seed = Field<IReadOnlyList<MultipartPart>>(window, "_formSeed");
                Assert.NotNull(seed);

                MultipartFormWindow dialog = OpenForm(window);
                AddText(dialog, "large", LargeText);
                dialog.Progress = (stage, _, _) =>
                {
                    lock (units)
                    {
                        units.Add(stage);
                    }

                    dialog.Dispatcher.Invoke(dialog.CancelWork);
                };

                await dialog.UseFormAsync();

                Assert.Equal(new[] { MultipartStage.Measure }, units);
                Assert.Equal(Localization.GetLocalizedString("FORM_USE_CANCELLED"), Status(dialog));
                Assert.Equal(2, Listed(dialog).Length);
                Assert.Equal(request, HeadersAndBody(window));
                Assert.Same(body, Field<byte[]>(window, "_fileBody"));
                Assert.Same(seed, Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
            }
            finally
            {
                window.Close();
            }
        });

        // Cancel while a pick measures what the form holds, or while it reads the file, stops it: a file is read only with the
        // room that measuring left, so a pick cancelled while it measures reads none, and a file read by then is not added.
        [Theory]
        [InlineData("measure")]
        [InlineData("read")]
        public Task CancellingAPick_ReadsNoFileOnceCancelled_AndAddsNothing(string phase) => _application.RunAsync(async () =>
        {
            MultipartFormWindow dialog = Dialog();
            var units = new List<MultipartStage>();
            try
            {
                if (phase == "measure")
                {
                    AddText(dialog, "large", LargeText);
                    dialog.ReadFile = (_, _, _) => throw new Xunit.Sdk.XunitException("No file is read once the pick is cancelled.");
                    dialog.Progress = (stage, _, _) =>
                    {
                        lock (units)
                        {
                            units.Add(stage);
                        }

                        dialog.Dispatcher.Invoke(dialog.CancelWork);
                    };

                    await AddFileAsync(dialog, "upload", Path.Join(AppContext.BaseDirectory, "data.bin"));

                    Assert.Equal(new[] { MultipartStage.Measure }, units);
                    Assert.Single(Listed(dialog));
                }
                else
                {
                    var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    dialog.ReadFile = async (_, _, token) =>
                    {
                        reading.SetResult();
                        await UntilCancelledAsync(token);
                        return new ReplayBodyRead(new byte[] { 1 }, null);
                    };

                    Task pick = AddFileAsync(dialog, "upload", Path.Join(AppContext.BaseDirectory, "data.bin"));
                    await reading.Task.WaitAsync(Patience);
                    Assert.True(dialog.IsBusy);
                    Click(dialog, "CancelWorkButton");
                    await pick.WaitAsync(Patience);

                    Assert.Empty(Listed(dialog));
                }

                Assert.Equal(Localization.GetLocalizedString("FORM_ADD_CANCELLED", "data.bin"), Status(dialog));
                Assert.False(dialog.IsBusy);
            }
            finally
            {
                dialog.Close();
            }
        });

        // A dialog closed while the user picks a file, as closing the composer closes it, reads no file and adds nothing.
        [Fact]
        public Task ADialogClosedWhileAFileIsPicked_ReadsNoFile() => _application.RunAsync(async () =>
        {
            MultipartFormWindow dialog = Dialog();
            dialog.ReadFile = (_, _, _) => throw new Xunit.Sdk.XunitException("No file is read once the dialog is closed.");
            Find<TextBox>(dialog, "NameBox").Text = "upload";
            dialog.ChooseFile = () =>
            {
                dialog.Close();
                return Path.Join(AppContext.BaseDirectory, "data.bin");
            };

            await dialog.AddFilePartAsync();

            Assert.False(dialog.IsBusy);
            Assert.Empty(Listed(dialog));
        });

        // Cancel while the form is built, in a unit of scanning for the boundary or of writing the body, from a text or a
        // file, stops it within that unit: nothing more is scanned or written, and the composer is unchanged. Measuring,
        // scanning and writing all run off the UI thread.
        [Theory]
        [InlineData("scan", "text")]
        [InlineData("scan", "file")]
        [InlineData("write", "text")]
        [InlineData("write", "file")]
        public Task CancellingWhileTheFormIsBuilt_StopsWithinThatUnit(string stage, string part) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            MultipartStage cancelled = stage == "scan" ? MultipartStage.Scan : MultipartStage.Write;
            int uiThread = Environment.CurrentManagedThreadId;
            var units = new List<(MultipartStage Stage, int Thread)>();
            try
            {
                string request = HeadersAndBody(window);
                MultipartFormWindow dialog = OpenForm(window);
                if (part == "text")
                {
                    AddText(dialog, "large", LargeText);
                }
                else
                {
                    dialog.ReadFile = (_, _, _) => Task.FromResult(new ReplayBodyRead(new byte[3 * MultipartForm.WorkChunk], null));
                    await AddFileAsync(dialog, "large", Path.Join(AppContext.BaseDirectory, "large.bin"));
                }

                dialog.Progress = (unit, _, _) =>
                {
                    lock (units)
                    {
                        units.Add((unit, Environment.CurrentManagedThreadId));
                    }

                    if (unit == cancelled)
                        dialog.Dispatcher.Invoke(dialog.CancelWork);
                };

                await dialog.UseFormAsync();

                Assert.Single(units, unit => unit.Stage == cancelled);
                if (cancelled == MultipartStage.Scan)
                    Assert.DoesNotContain(units, unit => unit.Stage == MultipartStage.Write);
                Assert.DoesNotContain(units, unit => unit.Thread == uiThread);
                Assert.Equal(Localization.GetLocalizedString("FORM_USE_CANCELLED"), Status(dialog));
                Assert.Equal(request, HeadersAndBody(window));
                Assert.Null(Field<byte[]>(window, "_fileBody"));
                Assert.Null(Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
            }
            finally
            {
                window.Close();
            }
        });

        // A form built, or a file read, by the time that the user cancelled, even when the work then ends normally, is not
        // used: the composer and the parts stay as they were.
        [Theory]
        [InlineData("use")]
        [InlineData("pick")]
        public Task AResultThatArrivesAfterCancel_IsDiscarded(string work) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                string request = HeadersAndBody(window);
                MultipartFormWindow dialog = OpenForm(window);
                AddText(dialog, "a", "1");
                if (work == "use")
                {
                    dialog.BuildBody = (parts, _) =>
                    {
                        dialog.Dispatcher.Invoke(dialog.CancelWork);
                        return MultipartForm.Build(parts, CancellationToken.None);
                    };

                    await dialog.UseFormAsync();

                    Assert.Equal(Localization.GetLocalizedString("FORM_USE_CANCELLED"), Status(dialog));
                    Assert.Same(dialog, Field<MultipartFormWindow>(window, "_formDialog"));
                }
                else
                {
                    dialog.ReadFile = (_, _, _) =>
                    {
                        dialog.Dispatcher.Invoke(dialog.CancelWork);
                        return Task.FromResult(new ReplayBodyRead(new byte[] { 1 }, null));
                    };

                    await AddFileAsync(dialog, "late", Path.Join(AppContext.BaseDirectory, "late.bin"));

                    Assert.Equal(Localization.GetLocalizedString("FORM_ADD_CANCELLED", "late.bin"), Status(dialog));
                }

                Assert.Single(Listed(dialog));
                Assert.Equal(request, HeadersAndBody(window));
                Assert.Null(Field<byte[]>(window, "_fileBody"));
                Assert.Null(Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
            }
            finally
            {
                window.Close();
            }
        });

        // While Use form works, the dialog is busy: only Cancel is available. Cancel stops the work, and so does closing the
        // dialog, or the composer, which closes the dialog: the form, built by then, is not used, and a closed dialog keeps
        // none of its parts. The composer can build a form again once the dialog is closed, unless it is closed itself.
        [Theory]
        [InlineData("cancel")]
        [InlineData("dialog")]
        [InlineData("composer")]
        public Task CancelOrClosingTheDialogOrTheComposer_StopsUseForm_AndChangesNothing(string stop) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            bool windowClosed = false;
            try
            {
                string request = HeadersAndBody(window);
                MultipartFormWindow dialog = OpenForm(window);
                bool dialogClosed = false;
                dialog.Closed += (_, _) => dialogClosed = true;
                AddText(dialog, "a", "1");
                var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool stopped = false;
                dialog.BuildBody = (parts, token) =>
                {
                    building.SetResult();
                    stopped = token.WaitHandle.WaitOne(Patience);
                    return MultipartForm.Build(parts, CancellationToken.None);
                };

                Task use = dialog.UseFormAsync();
                await building.Task.WaitAsync(Patience);
                Assert.True(dialog.IsBusy);
                Assert.Equal(
                    (true, false, false, false),
                    (Find<Button>(dialog, "CancelWorkButton").IsEnabled, Find<Button>(dialog, "UseFormButton").IsEnabled,
                        Find<Button>(dialog, "AddTextButton").IsEnabled, Find<Button>(dialog, "AddFileButton").IsEnabled));
                switch (stop)
                {
                    case "cancel":
                        Click(dialog, "CancelWorkButton");
                        break;
                    case "dialog":
                        dialog.Close();
                        break;
                    default:
                        window.Close();
                        windowClosed = true;
                        break;
                }

                await use.WaitAsync(Patience);

                Assert.True(stopped);
                Assert.Equal(stop != "cancel", dialogClosed);
                Assert.Equal(request, HeadersAndBody(window));
                Assert.Null(Field<byte[]>(window, "_fileBody"));
                Assert.Null(Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
                if (stop == "cancel")
                {
                    Assert.Equal(Localization.GetLocalizedString("FORM_USE_CANCELLED"), Status(dialog));
                    Assert.False(dialog.IsBusy);
                    Assert.Single(Listed(dialog));
                }
                else
                {
                    Assert.Empty(Listed(dialog));
                    Assert.Null(Field<MultipartFormWindow>(window, "_formDialog"));
                    if (stop == "dialog")
                        Assert.NotNull(OpenForm(window));
                }
            }
            finally
            {
                if (!windowClosed)
                    window.Close();
            }
        });

        // A form that cannot be built is not used, and the status says why: one without parts; a text that is not valid
        // Unicode, which is refused, never sent another way; a boundary that every draw finds in the parts; or more than 64
        // MiB. The dialog keeps its parts.
        [Theory]
        [InlineData("empty")]
        [InlineData("invalid-text")]
        [InlineData("collision")]
        [InlineData("too-large")]
        public Task AFormThatCannotBeBuilt_IsNotUsed_AndTheStatusSaysWhy(string failure) => _application.RunAsync(async () =>
        {
            MultipartPart[] seed = failure == "too-large"
                ? new[] { MultipartPart.File("large", "large.bin", new byte[MultipartForm.MaxBodyBytes]) }
                : Array.Empty<MultipartPart>();
            MultipartFormWindow dialog = Dialog(seed);
            try
            {
                dialog.RandomBytes = () => Random16.ToArray();
                string boundary = "xmat-form-" + Convert.ToHexString(Random16).ToLowerInvariant();
                string text = failure switch
                {
                    "invalid-text" => "lone \uD800 surrogate",
                    "collision" => "holds " + boundary,
                    _ => "1",
                };
                if (failure != "empty")
                    Assert.Equal(text, AddText(dialog, "text", text));

                await dialog.UseFormAsync();

                Assert.Equal(
                    Localization.GetLocalizedString(failure switch
                    {
                        "empty" => "FORM_EMPTY",
                        "invalid-text" => "FORM_TEXT_INVALID",
                        "collision" => "FORM_BOUNDARY_COLLISION",
                        _ => "FORM_TOO_LARGE",
                    }),
                    Status(dialog));
                Assert.Equal(seed.Length + (failure == "empty" ? 0 : 1), Listed(dialog).Length);
                Assert.False(dialog.IsBusy);
            }
            finally
            {
                dialog.Close();
            }
        });

        // A file that cannot be added adds nothing, and the status says why, by its name: one that was not found, cannot be
        // read or is too large to read; one that does not fit in what is left of 64 MiB, which is not read at all; and one
        // whose name a form cannot hold, with a control character or a lone surrogate, which is not read either and whose
        // name is shown escaped.
        [Theory]
        [InlineData("not-found")]
        [InlineData("unreadable")]
        [InlineData("too-large")]
        [InlineData("no-room")]
        [InlineData("file-name")]
        [InlineData("lone-surrogate")]
        public Task AFileThatCannotBeAdded_AddsNothing_AndTheStatusSaysWhy(string failure) => _application.RunAsync(async () =>
        {
            MultipartPart[] seed = failure == "no-room"
                ? new[] { MultipartPart.File("large", "large.bin", new byte[MultipartForm.MaxBodyBytes]) }
                : Array.Empty<MultipartPart>();
            MultipartFormWindow dialog = Dialog(seed);
            int reads = 0;
            try
            {
                dialog.ReadFile = (_, _, _) =>
                {
                    Interlocked.Increment(ref reads);
                    return Task.FromResult(failure switch
                    {
                        "not-found" => ReplayBodyRead.Failed(ReplayBodyFile.NotFoundCode),
                        "unreadable" => ReplayBodyRead.Failed(ReplayBodyFile.UnreadableCode),
                        "too-large" => ReplayBodyRead.Failed(ReplayBodyFile.TooLargeCode),
                        _ => throw new Xunit.Sdk.XunitException("This file is not read."),
                    });
                };
                string fileName = failure switch
                {
                    "file-name" => "bad\u0007name.bin",
                    "lone-surrogate" => "bad\uD800name.bin",
                    _ => "data.bin",
                };

                await AddFileAsync(dialog, "upload", Path.Join(AppContext.BaseDirectory, fileName));

                bool badName = failure is "file-name" or "lone-surrogate";
                string key = failure switch
                {
                    "not-found" => "FORM_FILE_NOT_FOUND",
                    "unreadable" => "FORM_FILE_UNREADABLE",
                    _ when badName => "FORM_FILE_NAME_INVALID",
                    _ => "FORM_FILE_TOO_LARGE",
                };
                Assert.Equal(Localization.GetLocalizedString(key, UntrustedText.Show(fileName, 260)), Status(dialog));
                Assert.Equal(seed.Length, Listed(dialog).Length);
                Assert.Equal(failure == "no-room" || badName ? 0 : 1, reads);
                Assert.False(dialog.IsBusy);
                if (badName)
                {
                    Assert.Contains(
                        failure == "file-name" ? "bad\\u0007name.bin" : "bad\\uD800name.bin", Status(dialog), StringComparison.Ordinal);
                    Assert.DoesNotContain(fileName, Status(dialog), StringComparison.Ordinal);
                }
            }
            finally
            {
                dialog.Close();
            }
        });

        // A file is read off the UI thread, cancellably, with exactly the room that the form has left as its limit: 64 MiB
        // less the closing line, the new part's framing, and each listed part's framing and content, where a typed text
        // counts its UTF-8 bytes, or three bytes a character when it is not valid text. Measuring runs off the UI thread too.
        [Fact]
        public Task APickedFile_IsReadOffTheUiThread_WithExactlyTheRoomThatTheFormHasLeft() => _application.RunAsync(async () =>
        {
            MultipartPart seedText = MultipartPart.Text("s", "seed", null, CancellationToken.None);
            MultipartPart seedFile = MultipartPart.File("f", "a.bin", new byte[10], "text/plain");
            MultipartFormWindow dialog = Dialog(new[] { seedText, seedFile });
            int uiThread = Environment.CurrentManagedThreadId;
            var reads = new List<(long Room, int Thread, bool Cancellable)>();
            var measured = new List<int>();
            try
            {
                AddText(dialog, "t", "h\u00e9llo");
                Assert.Equal("\uD800x", AddText(dialog, "bad", "\uD800x"));
                dialog.Progress = (_, _, _) =>
                {
                    lock (measured)
                    {
                        measured.Add(Environment.CurrentManagedThreadId);
                    }
                };
                dialog.ReadFile = (_, limit, token) =>
                {
                    lock (reads)
                    {
                        reads.Add((limit, Environment.CurrentManagedThreadId, token.CanBeCanceled));
                    }

                    return Task.FromResult(new ReplayBodyRead(new byte[] { 1, 2, 3 }, null));
                };

                await AddFileAsync(dialog, "c", Path.Join(AppContext.BaseDirectory, "pic.png"), "image/png");

                long room = MultipartForm.MaxBodyBytes - MultipartForm.ClosingLength -
                    MultipartForm.PartFramingLength(true, "c", "pic.png", "image/png") -
                    seedText.FramingLength - seedText.ContentLength - seedFile.FramingLength - seedFile.ContentLength -
                    MultipartForm.PartFramingLength(false, "t", null, null) - 6 -
                    MultipartForm.PartFramingLength(false, "bad", null, null) - 3 * 2;
                (long read, int thread, bool cancellable) = Assert.Single(reads);
                Assert.Equal(room, read);
                Assert.NotEqual(uiThread, thread);
                Assert.True(cancellable);
                Assert.NotEmpty(measured);
                Assert.DoesNotContain(uiThread, measured);
                Assert.Equal(Localization.GetLocalizedString("FORM_ADDED_FILE", "pic.png", 3), Status(dialog));
                Assert.Equal(
                    Localization.GetLocalizedString(
                        "FORM_PART_TYPE", Localization.GetLocalizedString("FORM_FILE_PART", "c", "pic.png", 3), "image/png"),
                    Listed(dialog)[^1]);
            }
            finally
            {
                dialog.Close();
            }
        });

        // A file, as read from disk, one byte larger than the room that the form has left is refused before it is read; one
        // that fits exactly is added.
        [Fact]
        public Task AFileOnDiskLargerThanTheRoomLeft_IsRefused_AndOneThatFitsExactly_IsAdded() => _application.RunAsync(async () =>
        {
            MultipartPart large = MultipartPart.File("large", "large.bin", new byte[MultipartForm.MaxBodyBytes - 1024]);
            MultipartFormWindow dialog = Dialog(new[] { large });
            string directory = FixtureDirectory();
            try
            {
                long room = MultipartForm.MaxBodyBytes - MultipartForm.ClosingLength - large.FramingLength - large.ContentLength -
                    MultipartForm.PartFramingLength(true, "upload", "fits.bin", null);
                Assert.InRange(room, 1, 1024);
                string over = Path.Join(directory, "over.bin");
                string fits = Path.Join(directory, "fits.bin");
                File.WriteAllBytes(over, new byte[room + 1]);
                File.WriteAllBytes(fits, Enumerable.Repeat((byte)7, (int)room).ToArray());

                await AddFileAsync(dialog, "upload", over);

                Assert.Equal(Localization.GetLocalizedString("FORM_FILE_TOO_LARGE", "over.bin"), Status(dialog));
                Assert.Single(Listed(dialog));

                await AddFileAsync(dialog, "upload", fits);

                Assert.Equal(Localization.GetLocalizedString("FORM_ADDED_FILE", "fits.bin", room), Status(dialog));
                Assert.Equal(2, Listed(dialog).Length);
            }
            finally
            {
                dialog.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // A file is read once, when it is added: changing it afterwards changes nothing that is sent. Build form then opens
        // with the form's parts, and reads no file again, also once the file is gone. A re-edit that is closed changes neither
        // the form nor its parts; one that is used builds the form from the same file part.
        [Fact]
        public Task ReopeningBuildForm_ListsTheFormsParts_ReadsNoFileAgain_AndAClosedReeditKeepsThem() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = new RequestComposerWindow(Capture(server.Port, "POST"), client, _ => true);
            string directory = FixtureDirectory();
            try
            {
                string path = Path.Join(directory, "data.txt");
                File.WriteAllText(path, "first");
                MultipartFormWindow dialog = OpenForm(window);
                AddText(dialog, "note", "1");
                await AddFileAsync(dialog, "upload", path);
                File.WriteAllText(path, "second");
                await dialog.UseFormAsync();
                File.Delete(path);
                IReadOnlyList<MultipartPart> seed = Field<IReadOnlyList<MultipartPart>>(window, "_formSeed");
                byte[] body = Field<byte[]>(window, "_fileBody");
                string request = HeadersAndBody(window);
                string[] parts =
                {
                    Localization.GetLocalizedString("FORM_TEXT_PART", "note", 1),
                    Localization.GetLocalizedString("FORM_FILE_PART", "upload", "data.txt", 5),
                };

                MultipartFormWindow reedit = OpenForm(window);
                reedit.ReadFile = (_, _, _) => throw new Xunit.Sdk.XunitException("No file is read again.");
                Assert.Equal(parts, Listed(reedit));
                Find<ListBox>(reedit, "PartsList").SelectedIndex = 0;
                reedit.RemovePart();
                AddText(reedit, "added", "2");
                reedit.Close();

                Assert.Same(seed, Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
                Assert.Same(body, Field<byte[]>(window, "_fileBody"));
                Assert.Equal(request, HeadersAndBody(window));

                MultipartFormWindow again = OpenForm(window);
                again.ReadFile = (_, _, _) => throw new Xunit.Sdk.XunitException("No file is read again.");
                Assert.Equal(parts, Listed(again));
                await again.UseFormAsync();

                IReadOnlyList<MultipartPart> rebuilt = Field<IReadOnlyList<MultipartPart>>(window, "_formSeed");
                Assert.NotSame(body, Field<byte[]>(window, "_fileBody"));
                Assert.Equal(new[] { "note", "upload" }, rebuilt.Select(part => part.Name));
                Assert.Same(seed[1], rebuilt[1]);

                await window.SendAsync();

                string sent = Encoding.UTF8.GetString(Assert.Single(server.Requests).Body);
                Assert.Contains("\r\n\r\nfirst\r\n", sent, StringComparison.Ordinal);
                Assert.DoesNotContain("second", sent, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // Every other body forgets the form's parts, so Build form starts empty again: Clear body, Empty body, Upload File,
        // Open request, Load from the history, and a dropped request.
        [Theory]
        [InlineData("clear")]
        [InlineData("empty")]
        [InlineData("upload")]
        [InlineData("open")]
        [InlineData("load")]
        [InlineData("drop")]
        public Task EveryOtherBody_ForgetsTheFormsParts(string replacement) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            Record(target, Exchange(9, "/recorded"));
            var window = RequestComposerWindow.ForNewRequest(confirm: _ => true, recording: target);
            try
            {
                if (replacement == "empty")
                {
                    window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "template.xmat-request.json");
                    window.ReadRequestFile = (_, _) => Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(TemplateFile)));
                    await window.OpenRequestAsync();
                }

                MultipartFormWindow dialog = OpenForm(window);
                AddText(dialog, "a", "1");
                await dialog.UseFormAsync();
                Assert.NotNull(Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));

                switch (replacement)
                {
                    case "clear":
                        window.ClearBody();
                        break;
                    case "empty":
                        await window.SupplyBodyAsync(empty: true);
                        break;
                    case "upload":
                        window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "upload.bin");
                        window.ReadBodyFile = (_, _) => Task.FromResult(new ReplayBodyRead(new byte[] { 1 }, null));
                        await window.UploadBodyAsync();
                        break;
                    case "open":
                        window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "opened.xmat-request.json");
                        window.ReadRequestFile = (_, _) => Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(ContentFile)));
                        await window.OpenRequestAsync();
                        break;
                    case "load":
                        await window.LoadFromHistoryAsync(Assert.Single(model.ReplayHistory.Snapshot()).Entry);
                        break;
                    default:
                        using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(Capture(9, "GET"), "Source tab"))
                        {
                            Assert.True(window.DropSession(drag.Data));
                        }

                        break;
                }

                Assert.Null(Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
                Assert.Empty(Listed(OpenForm(window)));
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A form supplies nothing that a request file left out. Use this body supplies the form's own bytes and keeps its
        // parts; so do edits to the URL, the method and the headers, which never build the form again.
        [Fact]
        public Task UseThisBody_AndEditsToTheUrlMethodOrHeaders_KeepTheFormsParts_AndBuildNothingAgain() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "template.xmat-request.json");
            window.ReadRequestFile = (_, _) => Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(TemplateFile)));
            try
            {
                await window.OpenRequestAsync();
                RequestOmissions omitted = Field<RequestReplayDraft>(window, "_draft").Omissions;
                Assert.True(omitted.Body && omitted.Headers);
                MultipartFormWindow dialog = OpenForm(window);
                AddText(dialog, "a", "1");
                await dialog.UseFormAsync();
                byte[] body = Field<byte[]>(window, "_fileBody");
                IReadOnlyList<MultipartPart> seed = Field<IReadOnlyList<MultipartPart>>(window, "_formSeed");

                Assert.Equal(omitted, Field<RequestReplayDraft>(window, "_draft").Omissions);
                Assert.Equal(Visibility.Visible, Find<Button>(window, "CompleteBodyButton").Visibility);
                Assert.StartsWith("Content-Type: multipart/form-data; boundary=", Find<TextBox>(window, "HeadersBox").Text);

                await window.SupplyBodyAsync(empty: false);
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/edited";
                Find<TextBox>(window, "MethodBox").Text = "PUT";
                Find<TextBox>(window, "HeadersBox").Text += "\r\nX-Edited: yes";

                Assert.Equal(omitted with { Body = false }, Field<RequestReplayDraft>(window, "_draft").Omissions);
                Assert.Same(body, Field<byte[]>(window, "_fileBody"));
                Assert.Same(seed, Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
                Assert.Single(Listed(OpenForm(window)));
            }
            finally
            {
                window.Close();
            }
        });

        // Closing the composer releases the form's parts, and with them the bytes of the files that they hold, which the
        // composer keeps until then.
        [Fact]
        public Task ClosingTheComposer_ReleasesTheFormsParts() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            bool closed = false;
            try
            {
                WeakReference snapshot = await UseAFileFormAsync(window);
                await CollectAsync(snapshot);
                Assert.True(snapshot.IsAlive);

                window.Close();
                closed = true;
                await CollectAsync(snapshot);

                Assert.False(snapshot.IsAlive);
            }
            finally
            {
                if (!closed)
                    window.Close();
            }
        });

        // A form that is not used leaves the request unedited, so Open request asks nothing; one that is used is an edit,
        // which Open request and Load ask about, and declining keeps it.
        [Fact]
        public Task AFormThatIsNotUsed_LeavesTheRequestUnedited_AndOneThatIsUsed_MakesOpenAndLoadAsk() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            ProxyConnectionModel row = Assert.Single(Record(target, Exchange(9, "/recorded")));
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(
                confirm: message =>
                {
                    questions.Add(message);
                    return false;
                },
                recording: target);
            window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "opened.xmat-request.json");
            window.ReadRequestFile = (_, _) => Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(ContentFile)));
            try
            {
                MultipartFormWindow unused = OpenForm(window);
                Assert.Equal("\uD800", AddText(unused, "a", "\uD800"));
                await unused.UseFormAsync();
                Assert.Equal(Localization.GetLocalizedString("FORM_TEXT_INVALID"), Status(unused));
                unused.Close();

                await window.OpenRequestAsync();

                Assert.Empty(questions);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_OPENED", "opened.xmat-request.json"), Status(window));

                MultipartFormWindow used = OpenForm(window);
                AddText(used, "a", "1");
                await used.UseFormAsync();
                string request = HeadersAndBody(window);
                IReadOnlyList<MultipartPart> seed = Field<IReadOnlyList<MultipartPart>>(window, "_formSeed");

                await window.OpenRequestAsync();
                await window.LoadFromHistoryAsync(Assert.Single(model.ReplayHistory.Snapshot()).Entry);

                Assert.Equal(
                    new[]
                    {
                        Localization.GetLocalizedString("COMPOSER_REPLACE_EDITED_FILE", "opened.xmat-request.json"),
                        Localization.GetLocalizedString(
                            "COMPOSER_REPLACE_EDITED_HISTORY", CaptureAppModel.ReplayContextName, row.RequestNumber),
                    },
                    questions);
                Assert.Equal(request, HeadersAndBody(window));
                Assert.Same(seed, Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // While the body is a form, a Connection header that names Content-Type, which would drop the form's Content-Type,
        // refuses Send, Copy as cURL, Save as cURL script and Save request as, before anything is asked, sent, copied or
        // saved, and rewrites no header. Without it, each goes ahead.
        [Theory]
        [InlineData("send")]
        [InlineData("copy")]
        [InlineData("script")]
        [InlineData("save")]
        public Task AConnectionHeaderNamingContentType_RefusesSendAndExportsOfAForm(string action) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            var window = new RequestComposerWindow(
                Capture(server.Port, "POST"),
                client,
                message =>
                {
                    questions.Add(message);
                    return true;
                });
            var steps = new List<string>();
            window.CopyText = _ => steps.Add("copy");
            window.ChooseCurlScriptPath = () =>
            {
                steps.Add("script");
                return null;
            };
            window.ChooseSavePath = () =>
            {
                steps.Add("save");
                return null;
            };
            Func<Task> act = action switch
            {
                "send" => window.SendAsync,
                "copy" => window.CopyAsCurlAsync,
                "script" => window.SaveCurlScriptAsync,
                _ => window.SaveRequestAsync,
            };
            try
            {
                MultipartFormWindow dialog = OpenForm(window);
                AddText(dialog, "a", "1");
                await dialog.UseFormAsync();
                var headers = Find<TextBox>(window, "HeadersBox");
                string formed = headers.Text;
                headers.Text = formed + "\r\nConnection: keep-alive, content-TYPE";
                string nominated = headers.Text;

                await act();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_FORM_CONTENT_TYPE_DROPPED"), Status(window));
                Assert.Equal(nominated, headers.Text);
                Assert.Empty(questions);
                Assert.Empty(steps);
                Assert.Empty(server.Requests);

                headers.Text = formed;
                await act();

                if (action == "send")
                {
                    Assert.Single(questions);
                    Assert.StartsWith("multipart/form-data; boundary=xmat-form-", Assert.Single(server.Requests).Header("Content-Type"));
                }
                else
                {
                    Assert.Equal(new[] { action }, steps);
                    Assert.Empty(server.Requests);
                }
            }
            finally
            {
                window.Close();
            }
        });

        // Send refuses a form whose Content-Type the headers would drop first: also for a URL with '#', before the numbers are
        // asked for, and before an endpoint that is not one either. Nothing is asked or sent, and no header changes.
        [Fact]
        public Task ANumberedFormsContentTypeRefusal_ComesBeforeItsNumbersAndItsEndpoint() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            var window = new RequestComposerWindow(
                Capture(server.Port, "POST"),
                client,
                message =>
                {
                    questions.Add(message);
                    return true;
                });
            int ranges = 0;
            window.ChooseSequence = () =>
            {
                ranges++;
                return "1-2";
            };
            try
            {
                await UseAFormAsync(window);
                var headers = Find<TextBox>(window, "HeadersBox");
                headers.Text += "\r\nConnection: Content-Type";
                string nominated = headers.Text;
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/upload/#";
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = "not an endpoint";

                await window.SendAsync();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_FORM_CONTENT_TYPE_DROPPED"), Status(window));
                Assert.Equal(0, ranges);
                Assert.Empty(questions);
                Assert.Empty(server.Requests);
                Assert.Equal(nominated, headers.Text);
            }
            finally
            {
                window.Close();
            }
        });

        // Once the body is no longer a form, a Connection header that names Content-Type is the request's own, as always: Send
        // sends the request, Copy as cURL copies it, and Save as cURL script and Save request as go on to ask where.
        [Theory]
        [InlineData("send")]
        [InlineData("copy")]
        [InlineData("script")]
        [InlineData("save")]
        public Task WithoutAForm_AConnectionHeaderNamingContentType_RefusesNothing(string action) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = new RequestComposerWindow(Capture(server.Port, "GET"), client, NoQuestion);
            List<string> steps = RecordSteps(window);
            try
            {
                await UseAFormAsync(window);
                window.ClearBody();
                Find<TextBox>(window, "HeadersBox").Text += "\r\nConnection: Content-Type";

                await ActionOf(window, action)();

                Assert.NotEqual(Localization.GetLocalizedString("COMPOSER_FORM_CONTENT_TYPE_DROPPED"), Status(window));
                if (action == "send")
                {
                    Assert.Single(server.Requests);
                    Assert.Empty(steps);
                }
                else
                {
                    Assert.Equal(new[] { action }, steps);
                    Assert.Empty(server.Requests);
                }
            }
            finally
            {
                window.Close();
            }
        });

        // Save request as and both cURL exports check a form's Content-Type off the UI thread, over the request and the form
        // flag that they froze, as the first step of their work; without a Connection nomination, each then goes ahead.
        [Theory]
        [InlineData("copy")]
        [InlineData("script")]
        [InlineData("save")]
        public Task TheContentTypeCheckOfAForm_RunsOffTheUiThread(string action) => _application.RunAsync(async () =>
        {
            var window = new RequestComposerWindow(Capture(9, "POST"), confirm: NoQuestion);
            List<string> steps = RecordSteps(window);
            int uiThread = Environment.CurrentManagedThreadId;
            var checks = new List<(bool Form, int Thread)>();
            window.CheckFormContentType = (form, draft, token) =>
            {
                lock (checks)
                {
                    checks.Add((form, Environment.CurrentManagedThreadId));
                }

                return RequestComposerWindow.FormContentTypeDropped(form, draft, token);
            };
            try
            {
                await UseAFormAsync(window);

                await ActionOf(window, action)();

                (bool form, int thread) = Assert.Single(checks);
                Assert.True(form);
                Assert.NotEqual(uiThread, thread);
                Assert.Equal(new[] { action }, steps);
            }
            finally
            {
                window.Close();
            }
        });

        // Cancel request, or closing the window, while Save request as or a cURL export checks a form's Content-Type stops the
        // check, which is given the action's token. Nothing is asked, inspected, prepared, copied or saved, and the request in
        // the editors stays as it was; the check's result, a refusal that arrives after the cancel, is discarded: the status
        // says that the action was cancelled.
        [Theory]
        [InlineData("copy", false)]
        [InlineData("copy", true)]
        [InlineData("script", false)]
        [InlineData("script", true)]
        [InlineData("save", false)]
        [InlineData("save", true)]
        public Task CancelOrClose_StopsTheContentTypeCheck_AndDiscardsALateResult(string action, bool close) => _application.RunAsync(async () =>
        {
            var questions = new List<string>();
            var window = new RequestComposerWindow(
                Capture(9, "POST"),
                confirm: message =>
                {
                    questions.Add(message);
                    return true;
                });
            List<string> steps = RecordSteps(window);
            window.PrepareCurl = (_, _, _, _) => throw new Xunit.Sdk.XunitException("Nothing is prepared once the check stopped.");
            window.InspectRequest = (_, _) => throw new Xunit.Sdk.XunitException("Nothing is inspected once the check stopped.");
            var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancelled = false;
            window.CheckFormContentType = (_, _, token) =>
            {
                checking.TrySetResult();
                cancelled = token.WaitHandle.WaitOne(Patience);
                return true;
            };
            bool closed = false;
            try
            {
                await UseAFormAsync(window);
                string request = HeadersAndBody(window);
                Task act = ActionOf(window, action)();
                await checking.Task.WaitAsync(Patience);

                if (close)
                {
                    window.Close();
                    closed = true;
                }
                else
                {
                    window.CancelSend();
                }

                await act.WaitAsync(Patience);

                Assert.True(cancelled);
                Assert.Empty(questions);
                Assert.Empty(steps);
                Assert.Equal(request, HeadersAndBody(window));
                if (!close)
                {
                    Assert.Equal(
                        Localization.GetLocalizedString(action == "copy" ? "COMPOSER_COPY_CURL_CANCELLED" : "COMPOSER_SAVE_CANCELLED"),
                        Status(window));
                }
            }
            finally
            {
                if (!closed)
                    window.Close();
            }
        });

        // A form's Content-Type that the headers would drop refuses Save request as and both cURL exports before anything is
        // inspected or prepared for the request, and before any question: also for a URL that goes to another origin, which
        // the exports would ask about, and which the check's refusal never gets to.
        [Theory]
        [InlineData("copy")]
        [InlineData("script")]
        [InlineData("save")]
        public Task AFormsContentTypeRefusal_ComesBeforeAnyInspectionPreparationOrQuestion(string action) => _application.RunAsync(async () =>
        {
            var questions = new List<string>();
            var window = new RequestComposerWindow(
                Capture(9, "POST"),
                confirm: message =>
                {
                    questions.Add(message);
                    return true;
                });
            List<string> steps = RecordSteps(window);
            window.PrepareCurl = (_, _, _, _) => throw new Xunit.Sdk.XunitException("Nothing is prepared for a refused form.");
            window.InspectRequest = (_, _) => throw new Xunit.Sdk.XunitException("Nothing is inspected for a refused form.");
            window.CheckFormContentType = (form, _, _) => form;
            try
            {
                await UseAFormAsync(window);
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.2:9/upload";
                string request = HeadersAndBody(window);

                await ActionOf(window, action)();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_FORM_CONTENT_TYPE_DROPPED"), Status(window));
                Assert.Empty(questions);
                Assert.Empty(steps);
                Assert.Equal(request, HeadersAndBody(window));
            }
            finally
            {
                window.Close();
            }
        });

        // The check itself is pure, over a request and a form flag, without the window: only a form's request whose Connection
        // header nominates Content-Type, in any case, is refused; header text that is not valid is not, nor is a request
        // without a form. It changes nothing, and once cancelled it gives no result, with or without a form.
        [Fact]
        public void TheContentTypeCheck_IsPure_AndACancelledCheckGivesNoResult()
        {
            static RequestReplayDraft Draft(string headers) =>
                new() { Method = "POST", Url = "http://127.0.0.1:9/upload", HeadersText = headers };
            const string Nominating = "Content-Type: multipart/form-data; boundary=b\r\nConnection: keep-alive, content-TYPE";
            RequestReplayDraft nominated = Draft(Nominating);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.True(RequestComposerWindow.FormContentTypeDropped(true, nominated, CancellationToken.None));
            Assert.False(RequestComposerWindow.FormContentTypeDropped(false, nominated, CancellationToken.None));
            Assert.False(RequestComposerWindow.FormContentTypeDropped(true, Draft("Connection: keep-alive"), CancellationToken.None));
            Assert.False(RequestComposerWindow.FormContentTypeDropped(
                true, Draft("not a header line\r\nConnection: Content-Type"), CancellationToken.None));
            Assert.Equal(Nominating, nominated.HeadersText);
            Assert.Throws<OperationCanceledException>(
                () => RequestComposerWindow.FormContentTypeDropped(true, nominated, cancellation.Token));
            Assert.Throws<OperationCanceledException>(
                () => RequestComposerWindow.FormContentTypeDropped(false, nominated, cancellation.Token));
        }

        // While the body is a form, header text that is not valid is left to the action, which refuses it as it always does.
        [Fact]
        public Task WithAForm_HeaderTextThatIsNotValid_IsRefusedByTheActionAsAlways() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var window = new RequestComposerWindow(Capture(server.Port, "POST"), client, NoQuestion);
            try
            {
                MultipartFormWindow dialog = OpenForm(window);
                AddText(dialog, "a", "1");
                await dialog.UseFormAsync();
                Find<TextBox>(window, "HeadersBox").Text += "\r\nnot a header line";

                await window.SendAsync();

                Assert.StartsWith(Localization.GetLocalizedString("COMPOSER_ERROR", ""), Status(window));
                Assert.Empty(server.Requests);
            }
            finally
            {
                window.Close();
            }
        });

        // Build form is unavailable while the composer is busy, here reading a file; while its dialog is open, which keeps
        // the composer from starting anything else; and once the composer is closed.
        [Fact]
        public Task BuildForm_IsUnavailableWhileTheComposerIsBusy_WhileItsDialogIsOpen_AndOnceClosed() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var opened = new List<MultipartFormWindow>();
            window.ShowFormDialog = opened.Add;
            window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "slow.bin");
            window.ReadBodyFile = async (_, token) =>
            {
                await UntilCancelledAsync(token);
                return new ReplayBodyRead(new byte[] { 1 }, null);
            };
            bool closed = false;
            try
            {
                var button = Find<Button>(window, "BuildFormButton");
                Task upload = window.UploadBodyAsync();
                Assert.False(button.IsEnabled);
                window.BuildForm();
                Assert.Empty(opened);
                window.CancelSend();
                await upload.WaitAsync(Patience);

                Click(window, "BuildFormButton");
                MultipartFormWindow dialog = Assert.Single(opened);
                window.BuildForm();
                Assert.Single(opened);
                dialog.Close();
                window.BuildForm();
                Assert.Equal(2, opened.Count);
                opened[1].Close();

                window.Close();
                closed = true;
                window.BuildForm();

                Assert.Equal(2, opened.Count);
            }
            finally
            {
                if (!closed)
                    window.Close();
            }
        });

        // Build form has its own access key in the composer, beside Upload File in the Tab order. In the dialog, every
        // button and field has an access key of its own; the list and each field are named for automation as their labels
        // read, whether or not anything has made the labels' automation peers, and are labeled by them; the name field has
        // the focus first, and Esc closes the dialog. Tab follows the dialog's order: the parts, the new part, then the form.
        // The list of parts, like every WPF list, is no Tab stop itself: Tab enters it once, onto a part, and passes it while
        // it is empty, when its label's access key still reaches it.
        [Fact]
        public Task BuildForm_AndItsDialog_AreReachedByKeyboard_AndNamedForAutomation() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                var button = Find<Button>(window, "BuildFormButton");
                Assert.Equal("Buil_d form...", button.Content);
                Assert.Equal("Build form...", UIElementAutomationPeer.CreatePeerForElement(button).GetName());
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_BUILD_FORM_HINT"), button.ToolTip);
                Assert.Single(AccessKeys(window), key => key == 'D');
                string[] stops = TabStopNames(window);
                Assert.Equal(Array.IndexOf(stops, "UploadFileButton") + 1, Array.IndexOf(stops, "BuildFormButton"));

                MultipartFormWindow dialog = OpenForm(window);
                (string Label, string Field, string Name)[] fields =
                {
                    ("PartsLabel", "PartsList", "Parts"),
                    ("NameLabel", "NameBox", "Name"),
                    ("TypeLabel", "TypeBox", "Type (optional)"),
                    ("ValueLabel", "ValueBox", "Value of a text part"),
                };

                // Each field is named for automation before anything has made its label's automation peer, as a screen reader
                // that starts at the focused field finds it; all four are asked before any is checked.
                Assert.All(fields, field => Assert.Null(UIElementAutomationPeer.FromElement(Find<Label>(dialog, field.Label))));
                Assert.Equal(
                    fields.Select(field => field.Name).ToArray(),
                    fields.Select(field => UIElementAutomationPeer.CreatePeerForElement(
                        Assert.IsAssignableFrom<UIElement>(dialog.FindName(field.Field))).GetName()).ToArray());

                await ShowOffscreenAsync(dialog);
                List<char> keys = AccessKeys(dialog);
                Assert.Equal(12, keys.Count);
                Assert.Equal(keys.Count, keys.Distinct().Count());
                foreach (var field in fields)
                {
                    var target = Assert.IsAssignableFrom<UIElement>(dialog.FindName(field.Field));
                    Assert.Same(target, Find<Label>(dialog, field.Label).Target);
                    Assert.Same(dialog.FindName(field.Label), AutomationProperties.GetLabeledBy(target));
                }

                Assert.Same(dialog.FindName("NameBox"), FocusManager.GetFocusedElement(dialog));
                var list = Find<ListBox>(dialog, "PartsList");
                Assert.Equal(
                    (true, false, KeyboardNavigationMode.Once),
                    (list.Focusable, list.IsTabStop, KeyboardNavigation.GetTabNavigation(list)));
                Assert.Equal(
                    new[]
                    {
                        "RemovePartButton", "MoveUpButton", "MoveDownButton", "NameBox", "TypeBox", "ValueBox", "AddTextButton",
                        "AddFileButton", "UseFormButton", "CancelWorkButton", "CloseButton",
                    },
                    TabStopNames(dialog));

                AddText(dialog, "a", "1");

                var part = Assert.IsType<ListBoxItem>(Assert.Single(list.Items));
                Assert.Equal((true, true), (part.Focusable, part.IsTabStop));
                Assert.Equal(new Control[] { part, Find<Button>(dialog, "RemovePartButton") }, TabStops(dialog).Take(2));
                Assert.True(Find<Button>(dialog, "CloseButton").IsCancel);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Find<TextBlock>(dialog, "StatusText")));
            }
            finally
            {
                window.Close();
            }
        });

        // Typing measures nothing, and neither does adding a text: only a name and a type are checked, as they are typed, with
        // the reason under each one that is not valid. The list is the dialog's own: Remove and Move edit it, never the parts
        // that it started with. A form has at most 128 parts.
        [Fact]
        public Task TypingMeasuresNothing_NamesAndTypesAreCheckedAsTyped_AndTheListIsTheDialogsOwn() => _application.RunAsync(() =>
        {
            MultipartPart[] seed =
            {
                MultipartPart.Text("first", "1", null, CancellationToken.None),
                MultipartPart.File("second", "b.bin", new byte[] { 2 }, "image/png"),
            };
            MultipartPart[] started = seed.ToArray();
            MultipartFormWindow dialog = Dialog(seed);
            var units = new List<MultipartStage>();
            dialog.Progress = (stage, _, _) => units.Add(stage);
            try
            {
                var name = Find<TextBox>(dialog, "NameBox");
                var type = Find<TextBox>(dialog, "TypeBox");
                var nameReason = Find<TextBlock>(dialog, "NameReason");
                var typeReason = Find<TextBlock>(dialog, "TypeReason");
                var addText = Find<Button>(dialog, "AddTextButton");
                var addFile = Find<Button>(dialog, "AddFileButton");
                var list = Find<ListBox>(dialog, "PartsList");
                string first = Localization.GetLocalizedString("FORM_TEXT_PART", "first", 1);
                string second = Localization.GetLocalizedString(
                    "FORM_PART_TYPE", Localization.GetLocalizedString("FORM_FILE_PART", "second", "b.bin", 1), "image/png");
                Assert.Equal(new[] { first, second }, Listed(dialog));
                Assert.Equal((Visibility.Collapsed, false, false), (nameReason.Visibility, addText.IsEnabled, addFile.IsEnabled));

                name.Text = "caf\u00e9";
                Assert.Equal(
                    (Visibility.Visible, Localization.GetLocalizedString("FORM_NAME_INVALID"), false),
                    (nameReason.Visibility, nameReason.Text, addText.IsEnabled));
                dialog.AddTextPart();
                Assert.Equal(Localization.GetLocalizedString("FORM_NAME_INVALID"), Status(dialog));
                Assert.Equal(2, Listed(dialog).Length);

                name.Text = "third";
                type.Text = "text/plain; charset";
                Assert.Equal(
                    (Visibility.Collapsed, Visibility.Visible, false), (nameReason.Visibility, typeReason.Visibility, addText.IsEnabled));
                type.Text = "text/plain; charset=utf-8";
                Assert.Equal((Visibility.Collapsed, true, true), (typeReason.Visibility, addText.IsEnabled, addFile.IsEnabled));
                Find<TextBox>(dialog, "ValueBox").Text = LargeText;
                dialog.AddTextPart();

                Assert.Empty(units);
                Assert.Equal("", Find<TextBox>(dialog, "ValueBox").Text);
                string third = Localization.GetLocalizedString(
                    "FORM_PART_TYPE",
                    Localization.GetLocalizedString("FORM_TEXT_PART", "third", LargeText.Length),
                    "text/plain; charset=utf-8");
                Assert.Equal(new[] { first, second, third }, Listed(dialog));

                list.SelectedIndex = 2;
                dialog.MovePart(-1);
                Assert.Equal(1, list.SelectedIndex);
                list.SelectedIndex = 0;
                Assert.False(Find<Button>(dialog, "MoveUpButton").IsEnabled);
                dialog.RemovePart();
                Assert.Equal(new[] { third, second }, Listed(dialog));
                Assert.Equal(started, seed);

                type.Text = "";
                while (Listed(dialog).Length < MultipartForm.MaxParts)
                {
                    Find<TextBox>(dialog, "ValueBox").Text = "x";
                    dialog.AddTextPart();
                }

                Assert.Equal((false, false), (addText.IsEnabled, addFile.IsEnabled));
                dialog.AddTextPart();
                Assert.Equal(Localization.GetLocalizedString("FORM_TOO_MANY_PARTS"), Status(dialog));
                Assert.Equal(MultipartForm.MaxParts, Listed(dialog).Length);
            }
            finally
            {
                dialog.Close();
            }

            return Task.CompletedTask;
        });

        // Uses a form with one file part in the composer, and returns only a weak reference to that part's bytes, as read, so
        // that the caller keeps nothing of them.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<WeakReference> UseAFileFormAsync(RequestComposerWindow window)
        {
            MultipartFormWindow dialog = OpenForm(window);
            WeakReference snapshot = null;
            dialog.ReadFile = (_, _, _) =>
            {
                byte[] bytes = new byte[4096];
                snapshot = new WeakReference(bytes);
                return Task.FromResult(new ReplayBodyRead(bytes, null));
            };
            await AddFileAsync(dialog, "upload", Path.Join(AppContext.BaseDirectory, "data.bin"));
            await dialog.UseFormAsync();
            Assert.Single(Field<IReadOnlyList<MultipartPart>>(window, "_formSeed"));
            return snapshot;
        }

        // Build form, as the composer opens it, but not shown: the tests take the dialog's actions as the user would.
        private static MultipartFormWindow OpenForm(RequestComposerWindow window)
        {
            MultipartFormWindow opened = null;
            window.ShowFormDialog = dialog => opened = dialog;
            window.BuildForm();
            return Assert.IsType<MultipartFormWindow>(opened);
        }

        // Uses a form of one text part in the composer.
        private static async Task UseAFormAsync(RequestComposerWindow window)
        {
            MultipartFormWindow dialog = OpenForm(window);
            AddText(dialog, "a", "1");
            await dialog.UseFormAsync();
        }

        // The action as the composer runs it: Send, Copy as cURL, Save as cURL script or Save request as.
        private static Func<Task> ActionOf(RequestComposerWindow window, string action) => action switch
        {
            "send" => window.SendAsync,
            "copy" => window.CopyAsCurlAsync,
            "script" => window.SaveCurlScriptAsync,
            _ => window.SaveRequestAsync,
        };

        // Records, by its action's name, what each export does once it goes ahead: Copy as cURL copies, and Save as cURL
        // script and Save request as ask where to save, which nobody answers.
        private static List<string> RecordSteps(RequestComposerWindow window)
        {
            var steps = new List<string>();
            window.CopyText = _ => steps.Add("copy");
            window.ChooseCurlScriptPath = () =>
            {
                steps.Add("script");
                return null;
            };
            window.ChooseSavePath = () =>
            {
                steps.Add("save");
                return null;
            };
            return steps;
        }

        // A dialog of its own, for a request with this method, whose form nothing takes.
        private static MultipartFormWindow Dialog(IReadOnlyList<MultipartPart> seed = null, string method = "POST") =>
            new(method, seed, () => "", (_, _) => throw new Xunit.Sdk.XunitException("No form is used here."));

        // Adds a text part as the user does; returns its value as the dialog's editor held it.
        private static string AddText(MultipartFormWindow dialog, string name, string value, string type = "")
        {
            Find<TextBox>(dialog, "NameBox").Text = name;
            Find<TextBox>(dialog, "TypeBox").Text = type;
            var valueBox = Find<TextBox>(dialog, "ValueBox");
            valueBox.Text = value;
            string typed = valueBox.Text;
            dialog.AddTextPart();
            return typed;
        }

        // Adds a file part as the user does, picking the file at path.
        private static async Task AddFileAsync(MultipartFormWindow dialog, string name, string path, string type = "")
        {
            Find<TextBox>(dialog, "NameBox").Text = name;
            Find<TextBox>(dialog, "TypeBox").Text = type;
            dialog.ChooseFile = () => path;
            await dialog.AddFilePartAsync();
        }

        // The parts as the dialog lists them.
        private static string[] Listed(MultipartFormWindow dialog) =>
            Find<ListBox>(dialog, "PartsList").Items.Cast<ListBoxItem>().Select(item => (string)item.Content).ToArray();

        // What a form never changes in the composer: the method, the URL, the parts that a request file left out as shown,
        // the transport options and Inspect session.
        private static string Unformed(Window window) => string.Join(
            "|",
            Find<TextBox>(window, "MethodBox").Text,
            Find<TextBox>(window, "UrlBox").Text,
            Find<TextBlock>(window, "OmissionsText").Text,
            Find<ComboBox>(window, "HttpVersionBox").SelectedIndex,
            Find<CheckBox>(window, "ConnectToBox").IsChecked,
            Find<TextBox>(window, "ConnectToText").Text,
            Find<CheckBox>(window, "FollowRedirectsBox").IsChecked,
            Find<ComboBox>(window, "RedirectLimitBox").SelectedItem,
            Find<CheckBox>(window, "InspectSessionBox").IsChecked);

        // The request's header text and body, as the editors show them.
        private static string HeadersAndBody(Window window) => string.Join(
            "|",
            Find<TextBox>(window, "HeadersBox").Text,
            Find<TextBox>(window, "BodyBox").Text,
            Find<CheckBox>(window, "Base64Box").IsChecked,
            Find<TextBlock>(window, "BodyFileText").Text);

        // What the composer keeps behind its editors that a form never changes: the request as loaded, which tells whether it
        // was edited, where it comes from, what a request file left out, and the response.
        private static (object Loaded, object Provenance, object Omissions, object Response) KeptBy(RequestComposerWindow window) =>
            (Field<object>(window, "_loaded"), Field<object>(window, "_provenance"),
                Field<RequestReplayDraft>(window, "_draft").Omissions, window.LastResult);

        private static T Field<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);

        // The access keys of the controls under root, in their logical order: the character after '_' in each one's text.
        private static List<char> AccessKeys(DependencyObject root)
        {
            var keys = new List<char>();
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is ContentControl { Content: string text } && text.IndexOf('_') is int index and >= 0 &&
                    index + 1 < text.Length)
                {
                    keys.Add(char.ToUpperInvariant(text[index + 1]));
                }

                if (child is DependencyObject element)
                    keys.AddRange(AccessKeys(element));
            }

            return keys;
        }

        // The controls that Tab stops at under root, named or not, such as a list's parts, in their logical order, which is
        // the order that Tab follows, since none sets a TabIndex.
        private static Control[] TabStops(DependencyObject root)
        {
            var stops = new List<Control>();
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is Control { Focusable: true, IsTabStop: true } control)
                {
                    Assert.Equal(int.MaxValue, control.TabIndex);
                    stops.Add(control);
                }

                if (child is DependencyObject element)
                    stops.AddRange(TabStops(element));
            }

            return stops.ToArray();
        }

        // The names of the controls that Tab stops at under root, in that order; a control without a name has an empty one.
        private static string[] TabStopNames(DependencyObject root) => TabStops(root).Select(control => control.Name).ToArray();

        // A request captured for the loopback port, with a credential and a Content-Type of its own.
        private static ProxyConnectionModel Capture(int port, string method) =>
            new(7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{port}", method, "/upload",
                $"{method} /upload HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: text/plain\r\nX-Token: canary\r\n", [],
                "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");

        // Records sends in the model's Replays tab, as the composer does, which puts each in the history; their rows.
        private static ProxyConnectionModel[] Record(ReplayRecordingTarget target, params ReplayExchange[] exchanges)
        {
            using var cancellation = new CancellationTokenSource();
            using ReplayRecording recording = target.TryBegin(cancellation, _ => ReplayProvenance.Composed);
            Assert.NotNull(recording);
            var rows = new ProxyConnectionModel[exchanges.Length];
            for (int index = 0; index < exchanges.Length; index++)
            {
                recording.Recorder.Add(
                    new ReplayItemResult(1, null, null, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null),
                    exchanges[index]);
                rows[index] = recording.Recorder.LastRecorded;
            }

            return rows;
        }

        // A GET as the Replays tab records it, sent to the loopback port, with its response.
        private static ReplayExchange Exchange(int port, string target) => new()
        {
            Outcome = ReplayOutcome.Completed,
            Request = new ReplayMessage(
                $"GET {target} HTTP/1.1",
                new[] { new KeyValuePair<string, string>("Host", $"127.0.0.1:{port}") },
                null,
                null),
            Response = new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()),
            RequestUri = new Uri($"http://127.0.0.1:{port}{target}"),
            StatusCode = 200,
            Started = DateTime.UtcNow,
        };

        // Records into the model's Replays tab, on this test's UI thread.
        private static ReplayRecordingTarget Recording(CaptureAppModel model)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(model, action => dispatcher.Invoke(action));
        }

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (var context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }

        // Chooses an HTTP version as the user does.
        private static void ChooseVersion(Window window, ReplayHttpVersion version)
        {
            var box = Find<ComboBox>(window, "HttpVersionBox");
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(item => version.Equals(item.Tag));
        }

        // Selects a button as the user does: one that the window shows and enables.
        private static void Click(Window window, string name)
        {
            var button = Find<Button>(window, name);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        // Completes, without throwing, once the token is cancelled, which must happen within Patience: work that still
        // returns normally after Cancel.
        private static async Task UntilCancelledAsync(CancellationToken token)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult()))
            {
                await cancelled.Task.WaitAsync(Patience);
            }
        }

        // Collects until nothing references the targets any more, at most three times, letting finished work leave its
        // threads in between.
        private static async Task CollectAsync(params WeakReference[] references)
        {
            for (int attempt = 0; attempt < 3 && references.Any(reference => reference.IsAlive); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(50);
            }
        }

        // Shows the window off screen, as a dialog that the user reaches by keyboard, and lets it load.
        private static async Task ShowOffscreenAsync(Window window)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -12000;
            window.Top = -12000;
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        private static string FixtureDirectory()
        {
            string directory = Path.Join(AppContext.BaseDirectory, "MultipartFormFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;
    }
}
