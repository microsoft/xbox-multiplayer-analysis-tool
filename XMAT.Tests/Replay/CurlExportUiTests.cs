// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using XMAT.Operations.Replay;
using XMAT.Operations.Storage;
using XMAT.Tests.HeadlessParity;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The composer's cURL export (Slice I2): Copy as cURL and Save as cURL script. When they are available and why not, the
    // warning shown before either is used, what they ask, the work that they run off the UI thread and how it stops, and what
    // becomes of a saved script. Nothing is sent: commands go to a list in place of the clipboard, and scripts to folders
    // under the test output.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CurlExportUiTests
    {
        private const string Url = "http://127.0.0.1:9/items/7?page=2";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        public CurlExportUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static CurlExportUiTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // The warning beside both buttons says, before either is used, that they export the full request, credentials and
        // body included, and each button is read with it. Its English wording is pinned here.
        [Fact]
        public Task TheWarning_IsShownBesideBothButtons_BeforeAnythingIsExported() => _application.RunAsync(() =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                var warning = Find<TextBlock>(window, "CurlWarningText");
                var copy = Find<Button>(window, "CopyCurlButton");
                var script = Find<Button>(window, "SaveCurlButton");

                Assert.Equal(Visibility.Visible, warning.Visibility);
                Assert.Equal(
                    "Copy as cURL copies, and Save as cURL script saves, the full request, credentials and body included: " +
                        "anyone who gets the command or the script can use them.",
                    warning.Text);
                Assert.Same(warning.Parent, copy.Parent);
                Assert.Same(warning.Parent, script.Parent);
                Assert.Equal(warning.Text, AutomationProperties.GetHelpText(copy));
                Assert.Equal(warning.Text, AutomationProperties.GetHelpText(script));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVE_CURL"), script.Content);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

        // Both exports are available for one request as it is, with Automatic or HTTP/1.1 only, and each button says why
        // not: for a URL with '#', Follow same-origin redirects, HTTP/2 only, Connect to another endpoint, Upstream proxy,
        // Client certificate and Automatically authenticate with Windows. Copy as cURL also needs a body of at most 64 KiB,
        // which a body held as bytes shows by its length; Save as cURL script takes a larger one. Availability prepares and
        // decodes nothing: a body that is not Base64 leaves it as it was.
        [Fact]
        public Task Availability_SaysWhy_FromWhatCostsNothing() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            window.PrepareCurl = (_, _, _, _) => throw new Xunit.Sdk.XunitException("Availability prepares nothing.");
            try
            {
                var copy = Find<Button>(window, "CopyCurlButton");
                var script = Find<Button>(window, "SaveCurlButton");
                var url = Find<TextBox>(window, "UrlBox");
                var follow = Find<CheckBox>(window, "FollowRedirectsBox");
                var connectTo = Find<CheckBox>(window, "ConnectToBox");
                var proxy = Find<CheckBox>(window, "ProxyBox");
                (bool, object, bool, object) Buttons() => (copy.IsEnabled, copy.ToolTip, script.IsEnabled, script.ToolTip);
                (bool, object, bool, object) Neither(string key) =>
                    (false, Localization.GetLocalizedString(key), false, Localization.GetLocalizedString(key));
                (bool, object, bool, object) both = (true, null, true, null);
                (bool, object, bool, object) savedOnly =
                    (false, Localization.GetLocalizedString("COMPOSER_COPY_CURL_LARGE_BODY"), true, null);
                Assert.True(ToolTipService.GetShowOnDisabled(script));

                url.Text = Url;
                Find<CheckBox>(window, "Base64Box").IsChecked = true;
                Find<TextBox>(window, "BodyBox").Text = "%%% is not Base64";
                Assert.Equal(both, Buttons());

                ChooseVersion(window, ReplayHttpVersion.Http11);
                Assert.Equal(both, Buttons());
                ChooseVersion(window, ReplayHttpVersion.Http2);
                Assert.Equal(Neither("COMPOSER_CURL_HTTP2"), Buttons());
                ChooseVersion(window, ReplayHttpVersion.Auto);

                url.Text = "http://127.0.0.1:9/items/#";
                Assert.Equal(Neither("COMPOSER_CURL_NUMBERED_URL"), Buttons());
                url.Text = Url;

                follow.IsChecked = true;
                Assert.Equal(Neither("COMPOSER_CURL_REDIRECTS"), Buttons());
                follow.IsChecked = false;

                connectTo.IsChecked = true;
                Assert.Equal(Neither("COMPOSER_CURL_CONNECT_TO"), Buttons());
                connectTo.IsChecked = false;
                Assert.Equal(both, Buttons());

                // Whatever its text, which availability does not read.
                Find<TextBox>(window, "ProxyText").Text = "not a proxy";
                proxy.IsChecked = true;
                Assert.Equal(Neither("COMPOSER_CURL_PROXY"), Buttons());
                proxy.IsChecked = false;
                Assert.Equal(both, Buttons());

                // Whether a certificate is chosen or not, which availability does not read.
                var certificate = Find<CheckBox>(window, "ClientCertificateBox");
                certificate.IsChecked = true;
                Assert.Equal(Neither("COMPOSER_CURL_CLIENT_CERT"), Buttons());
                certificate.IsChecked = false;
                Assert.Equal(both, Buttons());

                var windowsAuth = Find<CheckBox>(window, "WindowsAuthBox");
                windowsAuth.IsChecked = true;
                Assert.Equal(Neither("COMPOSER_CURL_WINDOWS_AUTH"), Buttons());
                windowsAuth.IsChecked = false;
                Assert.Equal(both, Buttons());

                byte[] held = new byte[RequestCurlExporter.MaxPasteableBodyBytes + 1];
                window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "held.bin");
                window.ReadBodyFile = (_, _) => Task.FromResult(new ReplayBodyRead(held, null));
                await window.UploadBodyAsync();
                Assert.Equal(savedOnly, Buttons());

                held = new byte[RequestCurlExporter.MaxPasteableBodyBytes];
                await window.UploadBodyAsync();
                Assert.Equal(both, Buttons());
            }
            finally
            {
                window.Close();
            }
        });

        // Neither export is available in Raw mode, whose prepared bytes curl would not send as they are: each button says
        // why, and neither export prepares, copies, asks or saves anything. Both come back with Parsed mode.
        [Fact]
        public Task NeitherExport_IsAvailableInRawMode_AndBothComeBackWithParsedMode() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var copied = new List<string>();
            window.CopyText = copied.Add;
            window.PrepareCurl = (_, _, _, _) => throw new Xunit.Sdk.XunitException("Raw mode prepares no export.");
            window.ChooseCurlScriptPath = () => throw new Xunit.Sdk.XunitException("Raw mode saves no script.");
            try
            {
                var copy = Find<Button>(window, "CopyCurlButton");
                var script = Find<Button>(window, "SaveCurlButton");
                string raw = Localization.GetLocalizedString("COMPOSER_CURL_RAW");
                (bool, object, bool, object) Buttons() => (copy.IsEnabled, copy.ToolTip, script.IsEnabled, script.ToolTip);
                (bool, object, bool, object) neither = (false, raw, false, raw);
                (bool, object, bool, object) both = (true, null, true, null);
                ChooseVersion(window, ReplayHttpVersion.Http11);
                Find<TextBox>(window, "UrlBox").Text = Url;
                await window.SwitchModeAsync();

                Assert.Equal(neither, Buttons());
                await window.CopyAsCurlAsync();
                Assert.Equal(raw, Status(window));
                Find<TextBlock>(window, "StatusText").Text = "-";
                await window.SaveCurlScriptAsync();
                Assert.Equal(raw, Status(window));
                Assert.Empty(copied);

                await window.SwitchModeAsync();

                Assert.Equal(both, Buttons());
            }
            finally
            {
                window.Close();
            }
        });

        // Copy as cURL fixes the request and its options when it is selected, then prepares and renders the command off the
        // UI thread, with the window busy, and copies it; nothing is asked for the request's own origin, whatever its
        // method. Edits made meanwhile change nothing in the command: HTTP/1.1 only reaches it.
        [Fact]
        public Task CopyAsCurl_CopiesTheRequestAsItWasWhenSelected_PreparedAndRenderedOffTheUiThread() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var copied = new List<string>();
            var work = new List<string>();
            var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var proceed = new ManualResetEventSlim();
            window.CopyText = copied.Add;
            window.PrepareCurl = (draft, transport, redirects, token) =>
            {
                work.Add("prepare on the UI thread: " + window.Dispatcher.CheckAccess());
                preparing.TrySetResult();
                Assert.True(proceed.Wait(Patience));
                return RequestCurlExporter.Prepare(draft, transport, redirects, token);
            };
            window.WriteCurlScript = (request, form, sidecar, stream, token) =>
            {
                work.Add($"write {form}, {sidecar ?? "inline"}, on the UI thread: " + window.Dispatcher.CheckAccess());
                RequestCurlExporter.Write(request, form, sidecar, stream, token);
            };
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "PUT";
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<TextBox>(window, "HeadersBox").Text = "X-Test: copied";
                Find<TextBox>(window, "BodyBox").Text = "copied body";
                ChooseVersion(window, ReplayHttpVersion.Http11);

                Task copying = window.CopyAsCurlAsync();

                await preparing.Task.WaitAsync(Patience);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_CURL_PREPARING"), Status(window));
                Assert.False(Find<Button>(window, "CopyCurlButton").IsEnabled);
                Assert.False(Find<Button>(window, "SaveCurlButton").IsEnabled);
                Assert.True(Find<Button>(window, "CancelSendButton").IsEnabled);
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/changed";
                ChooseVersion(window, ReplayHttpVersion.Auto);
                proceed.Set();
                await copying.WaitAsync(Patience);

                Assert.Equal(
                    new[] { "prepare on the UI thread: False", "write Pasteable, inline, on the UI thread: False" }, work);
                string command = Assert.Single(copied);
                Assert.Contains(Url, command);
                Assert.Contains("--http1.1", command);
                Assert.Contains("X-Test: copied", command);
                Assert.DoesNotContain("/changed", command);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_COPIED"), Status(window));
            }
            finally
            {
                proceed.Set();
                window.Close();
            }
        });

        // A body in the editor is measured when it is prepared, off the UI thread, never by the button: Copy as cURL copies
        // one of 64 KiB, and for a larger one copies nothing, leaving the clipboard as it was, and points to Save as cURL
        // script.
        [Theory]
        [InlineData(64 * 1024)]
        [InlineData(64 * 1024 + 1)]
        public Task CopyAsCurl_HoldsABodyInTheEditorTo64KiB_WhenItIsPrepared(int length) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var copied = new List<string>();
            var prepared = new List<bool>();
            window.CopyText = copied.Add;
            window.PrepareCurl = (draft, transport, redirects, token) =>
            {
                prepared.Add(window.Dispatcher.CheckAccess());
                return RequestCurlExporter.Prepare(draft, transport, redirects, token);
            };
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<TextBox>(window, "BodyBox").Text = new string('a', length);
                Assert.True(Find<Button>(window, "CopyCurlButton").IsEnabled);

                await window.CopyAsCurlAsync();

                Assert.Equal(new[] { false }, prepared);
                if (length <= RequestCurlExporter.MaxPasteableBodyBytes)
                {
                    Assert.Single(copied);
                    Assert.Equal(Localization.GetLocalizedString("COMPOSER_COPIED"), Status(window));
                }
                else
                {
                    Assert.Empty(copied);
                    Assert.Equal(Localization.GetLocalizedString("COMPOSER_COPY_CURL_LARGE_BODY"), Status(window));
                }
            }
            finally
            {
                window.Close();
            }
        });

        // A clipboard that another program holds keeps what it had, and the status says so.
        [Fact]
        public Task CopyAsCurl_SaysSo_WhenAnotherProgramHoldsTheClipboard() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            window.CopyText = _ => throw new ExternalException("The clipboard is held by another program.");
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;

                await window.CopyAsCurlAsync();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_COPY_CURL_FAILED"), Status(window));
                Assert.True(Find<Button>(window, "CopyCurlButton").IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        // A URL that goes to another origin than the request was captured for is exported only once the user allows that
        // origin, which the question names. It is asked for every export, before anything else about it: declining leaves
        // the clipboard as it was, asks nothing more and publishes nothing. Allowing it is no permission to send, which a
        // send still asks for.
        [Theory]
        [InlineData("copy")]
        [InlineData("save")]
        public Task AnotherOrigin_IsExportedOnlyOnceAllowed_AndDecliningExportsNothing(string action) => _application.RunAsync(async () =>
        {
            const string Moved = "http://127.0.0.2:9/data";
            var steps = new List<string>();
            var answers = new Queue<bool>(action == "copy"
                ? new[] { false, true, false }
                : new[] { false, true, false, true, true, false });
            var window = new RequestComposerWindow(Capture(), confirm: message =>
            {
                steps.Add(message);
                return answers.Dequeue();
            });
            var copied = new List<string>();
            var published = new List<string>();
            window.CopyText = copied.Add;
            window.ChooseCurlScriptPath = () =>
            {
                steps.Add("where");
                return Path.Join(AppContext.BaseDirectory, "moved.ps1");
            };
            window.PublishCurlScript = (path, write, _) =>
            {
                write(Stream.Null);
                published.Add(Path.GetFileName(path));
                return PrivateFilePublication.Published;
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Moved;
                string origin = Localization.GetLocalizedString("COMPOSER_CURL_ORIGIN", "http://127.0.0.2:9");
                string consent = Localization.GetLocalizedString("COMPOSER_SAVE_CURL_CONSENT", 0);
                Func<Task> export = action == "copy" ? window.CopyAsCurlAsync : window.SaveCurlScriptAsync;

                await export();

                Assert.Equal(action == "copy" ? new[] { origin } : new[] { "where", origin }, steps);
                Assert.Equal(
                    Localization.GetLocalizedString(
                        action == "copy" ? "COMPOSER_COPY_CURL_ORIGIN_REFUSED" : "COMPOSER_SAVE_CURL_ORIGIN_REFUSED",
                        "http://127.0.0.2:9"),
                    Status(window));
                Assert.Empty(copied);
                Assert.Empty(published);

                if (action == "save")
                {
                    await export();

                    Assert.Equal(new[] { "where", origin, "where", origin, consent }, steps);
                    Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED"), Status(window));
                    Assert.Empty(published);
                }

                steps.Clear();
                await export();

                Assert.Equal(action == "copy" ? new[] { origin } : new[] { "where", origin, consent }, steps);
                Assert.Equal(
                    action == "copy"
                        ? Localization.GetLocalizedString("COMPOSER_COPIED")
                        : Localization.GetLocalizedString("COMPOSER_SAVED_CURL", "moved.ps1"),
                    Status(window));
                Assert.Equal(action == "copy" ? 1 : 0, copied.Count);
                Assert.Equal(action == "copy" ? 0 : 1, published.Count);

                steps.Clear();
                await window.SendAsync();

                Assert.Equal(new[] { Localization.GetLocalizedString("COMPOSER_CONFIRM_MESSAGE", "GET", Moved) }, steps);
                Assert.Empty(answers);
            }
            finally
            {
                window.Close();
            }
        });

        // Save as cURL script asks where, then whether to save the full request, naming the body's size, for every script:
        // No, the default, saves nothing. The request's own origin needs no other question.
        [Fact]
        public Task SaveAsCurlScript_AsksWhereThenForTheContentEveryTime_AndNoSavesNothing() => _application.RunAsync(async () =>
        {
            var steps = new List<string>();
            var answers = new Queue<bool>(new[] { false, true });
            var window = RequestComposerWindow.ForNewRequest(confirm: message =>
            {
                steps.Add(message);
                return answers.Dequeue();
            });
            var published = new List<string>();
            window.ChooseCurlScriptPath = () =>
            {
                steps.Add("where");
                return Path.Join(AppContext.BaseDirectory, "request.ps1");
            };
            window.PublishCurlScript = (path, write, _) =>
            {
                write(Stream.Null);
                published.Add(Path.GetFileName(path));
                return PrivateFilePublication.Published;
            };
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<TextBox>(window, "BodyBox").Text = "canary-body";
                string consent = Localization.GetLocalizedString("COMPOSER_SAVE_CURL_CONSENT", 11);

                await window.SaveCurlScriptAsync();

                Assert.Equal(new[] { "where", consent }, steps);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED"), Status(window));
                Assert.Empty(published);

                await window.SaveCurlScriptAsync();

                Assert.Equal(new[] { "where", consent, "where", consent }, steps);
                Assert.Equal(new[] { "request.ps1" }, published);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVED_CURL", "request.ps1"), Status(window));
            }
            finally
            {
                window.Close();
            }
        });

        // Preparing and rendering run off the UI thread while the window is busy, and Cancel request or closing the window
        // stops them. A request prepared, or a command rendered, by then arrives late and is dropped: nothing is asked,
        // copied or saved.
        [Theory]
        [InlineData("copy", "cancel")]
        [InlineData("copy", "close")]
        [InlineData("copy", "late")]
        [InlineData("save", "cancel")]
        [InlineData("save", "close")]
        [InlineData("save", "late")]
        public Task Exporting_StopsWhenCancelledOrClosed_AndDropsALateResult(string action, string stop) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var copied = new List<string>();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool closed = false;
            window.CopyText = copied.Add;
            window.ChooseCurlScriptPath = () => throw new Xunit.Sdk.XunitException("Nothing is asked after Cancel request.");
            window.PublishCurlScript = (_, _, _) => throw new Xunit.Sdk.XunitException("Nothing is saved after Cancel request.");
            window.PrepareCurl = (draft, transport, redirects, token) =>
            {
                started.TrySetResult(window.Dispatcher.CheckAccess());
                bool cancelled = token.WaitHandle.WaitOne(Patience);
                if (stop != "late")
                    token.ThrowIfCancellationRequested();
                Assert.True(cancelled);
                // Returns normally once cancelled, as work does that last checked its token before Cancel request.
                return RequestCurlExporter.Prepare(draft, transport, redirects, CancellationToken.None);
            };
            window.WriteCurlScript = (_, _, _, stream, _) => stream.Write("& { }"u8);
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;

                Task working = action == "copy" ? window.CopyAsCurlAsync() : window.SaveCurlScriptAsync();

                Assert.False(await started.Task.WaitAsync(Patience));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_CURL_PREPARING"), Status(window));
                Assert.False(Find<Grid>(window, "RequestEditor").IsEnabled);
                Assert.False(Find<Button>(window, "CopyCurlButton").IsEnabled);
                Assert.False(Find<Button>(window, "SaveCurlButton").IsEnabled);
                Assert.True(Find<Button>(window, "CancelSendButton").IsEnabled);
                if (stop == "close")
                {
                    window.Close();
                    closed = true;
                }
                else
                {
                    window.CancelSend();
                }

                await working.WaitAsync(Patience);

                Assert.Empty(copied);
                if (!closed)
                {
                    Assert.Equal(
                        Localization.GetLocalizedString(
                            action == "copy" ? "COMPOSER_COPY_CURL_CANCELLED" : "COMPOSER_SAVE_CANCELLED"),
                        Status(window));
                    Assert.True(Find<Button>(window, "CopyCurlButton").IsEnabled);
                    Assert.True(Find<Button>(window, "SaveCurlButton").IsEnabled);
                }
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }
            }
        });

        // What became of a script is what the status says, by its name only, also after a late Cancel request: a script
        // published by then stays published. The script is published off the UI thread.
        [Theory]
        [InlineData("Published", "COMPOSER_SAVED_CURL")]
        [InlineData("Exists", "COMPOSER_SAVE_EXISTS")]
        [InlineData("Unsupported", "COMPOSER_SAVE_UNSUPPORTED")]
        [InlineData("Cancelled", "COMPOSER_SAVE_CANCELLED")]
        [InlineData("Failed", "COMPOSER_SAVE_FAILED")]
        [InlineData("CleanupFailed", "COMPOSER_SAVE_CLEANUP_FAILED")]
        public Task EveryPublicationOutcome_IsWorded_AlsoAfterALateCancel(string outcome, string key) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: _ => true);
            var publishing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ChooseCurlScriptPath = () => Path.Join(AppContext.BaseDirectory, "script.ps1");
            window.PublishCurlScript = (_, _, token) =>
            {
                publishing.TrySetResult(window.Dispatcher.CheckAccess());
                Assert.True(token.WaitHandle.WaitOne(Patience));
                return Enum.Parse<PrivateFilePublication>(outcome);
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;

                Task saving = window.SaveCurlScriptAsync();

                Assert.False(await publishing.Task.WaitAsync(Patience));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVING", "script.ps1"), Status(window));
                window.CancelSend();
                await saving.WaitAsync(Patience);

                Assert.Equal(Localization.GetLocalizedString(key, "script.ps1"), Status(window));
            }
            finally
            {
                window.Close();
            }
        });

        // With the real publisher, a script never replaces a file, and says so; a temporary file that XMAT could not remove
        // takes precedence over a failure before the publication. Closing the window once the script is published deletes
        // nothing and relabels nothing: the script stays, a PowerShell 7.2 script in UTF-8 with a byte order mark.
        [Fact]
        public Task SavingAScript_NeverReplacesAFile_AndAPublishedScriptOutlivesClosing() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            var window = RequestComposerWindow.ForNewRequest(confirm: _ => true);
            bool closed = false;
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                string taken = Path.Join(directory, "taken.ps1");
                File.WriteAllText(taken, "keep me");
                window.ChooseCurlScriptPath = () => taken;

                await window.SaveCurlScriptAsync();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVE_EXISTS", "taken.ps1"), Status(window));
                Assert.Equal("keep me", File.ReadAllText(taken));
                Assert.Equal(new[] { "taken.ps1" }, Directory.GetFiles(directory).Select(Path.GetFileName));

                window.ChooseCurlScriptPath = () => Path.Join(directory, "failed.ps1");
                window.WriteCurlScript = (_, _, _, _, _) => throw new IOException("The disk is full.");
                window.PublishCurlScript = (path, write, token) => PrivateFilePublisher.Publish(
                    path, write, new PrivateFilePublisherTestHooks(RemoveStaging: _ => false), token);

                await window.SaveCurlScriptAsync();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVE_CLEANUP_FAILED"), Status(window));
                Assert.DoesNotContain("failed.ps1", Directory.GetFiles(directory).Select(Path.GetFileName));

                string kept = Path.Join(directory, "kept.ps1");
                window.ChooseCurlScriptPath = () => kept;
                window.WriteCurlScript = null;
                window.PublishCurlScript = (path, write, token) => PrivateFilePublisher.Publish(
                    path,
                    write,
                    new PrivateFilePublisherTestHooks(Checkpoint: checkpoint =>
                    {
                        if (checkpoint == PrivateFilePublisherCheckpoint.AfterCommit)
                        {
                            window.Dispatcher.Invoke(() =>
                            {
                                window.Close();
                                closed = true;
                            });
                        }
                    }),
                    token);

                await window.SaveCurlScriptAsync();

                Assert.True(closed);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVING", "kept.ps1"), Status(window));
                byte[] script = File.ReadAllBytes(kept);
                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, script.Take(3));
                Assert.Contains("#Requires -Version 7.2", Encoding.UTF8.GetString(script));
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }

                Directory.Delete(directory, recursive: true);
            }
        });

        // Save as cURL script saves a body larger than Copy as cURL copies, held as bytes from an uploaded file: the script
        // is rendered off the UI thread straight into the file, its body inline, after asking for the content with its size.
        [Fact]
        public Task SaveAsCurlScript_SavesABodyLargerThan64KiB_RenderedOffTheUiThreadIntoTheFile() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            var steps = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(confirm: message =>
            {
                steps.Add(message);
                return true;
            });
            var written = new List<string>();
            byte[] body = Enumerable.Range(0, RequestCurlExporter.MaxPasteableBodyBytes + 1).Select(index => (byte)index).ToArray();
            window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "large.bin");
            window.ReadBodyFile = (_, _) => Task.FromResult(new ReplayBodyRead(body, null));
            window.ChooseCurlScriptPath = () => Path.Join(directory, "large.ps1");
            window.WriteCurlScript = (request, form, sidecar, stream, token) =>
            {
                written.Add($"{form}, {sidecar ?? "inline"}, {request.Body.Length}, on the UI thread: {window.Dispatcher.CheckAccess()}");
                RequestCurlExporter.Write(request, form, sidecar, stream, token);
            };
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = Url;
                await window.UploadBodyAsync();
                Assert.False(Find<Button>(window, "CopyCurlButton").IsEnabled);
                Assert.True(Find<Button>(window, "SaveCurlButton").IsEnabled);

                await window.SaveCurlScriptAsync();

                Assert.Equal(new[] { Localization.GetLocalizedString("COMPOSER_SAVE_CURL_CONSENT", body.Length) }, steps);
                Assert.Equal(new[] { $"File, inline, {body.Length}, on the UI thread: False" }, written);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVED_CURL", "large.ps1"), Status(window));
                Assert.True(new FileInfo(Path.Join(directory, "large.ps1")).Length > body.Length);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, recursive: true);
            }
        });

        // What Prepare refuses is said in the words that the buttons use when they are unavailable for the same reason, and
        // nothing is asked, copied or saved. The window's own checks come first, so an option that it makes unavailable
        // reaches Prepare here only through the test seam.
        [Theory]
        [InlineData("numbered", "COMPOSER_CURL_NUMBERED_URL")]
        [InlineData("redirects", "COMPOSER_CURL_REDIRECTS")]
        [InlineData("http2", "COMPOSER_CURL_HTTP2")]
        [InlineData("connect-to", "COMPOSER_CURL_CONNECT_TO")]
        [InlineData("proxy", "COMPOSER_CURL_PROXY")]
        [InlineData("client-certificate", "COMPOSER_CURL_CLIENT_CERT")]
        [InlineData("windows-auth", "COMPOSER_CURL_WINDOWS_AUTH")]
        [InlineData("head-body", "COMPOSER_CURL_HEAD_BODY")]
        [InlineData("control-header", "COMPOSER_CURL_CONTROL_HEADER")]
        [InlineData("too-long", "COMPOSER_CURL_TOO_LONG")]
        [InlineData("not-base64", "COMPOSER_CURL_INVALID")]
        public Task WhatPrepareRefuses_IsSaidAsTheButtonsSayIt_AndNothingIsExported(
            string refusal, string key) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var copied = new List<string>();
            Assert.True(ReplayConnectTo.TryParse("127.0.0.2:9443", out ReplayConnectTo endpoint));
            Assert.True(ReplayProxy.TryParse("http://192.0.2.20:3128", out ReplayProxy upstream));
            Assert.True(ReplayClientCertificate.TryParse(new string('A', 40), out ReplayClientCertificate certificate));
            window.CopyText = copied.Add;
            window.ChooseCurlScriptPath = () => throw new Xunit.Sdk.XunitException("Nothing is asked about a refused request.");
            window.PrepareCurl = (draft, transport, redirects, token) => refusal switch
            {
                "numbered" => RequestCurlExporter.Prepare(draft.WithUrl(draft.Url + "#"), transport, redirects, token),
                "redirects" => RequestCurlExporter.Prepare(draft, transport, 3, token),
                "http2" => RequestCurlExporter.Prepare(
                    draft, new ReplayTransportOptions(ReplayHttpVersion.Http2, null), redirects, token),
                "connect-to" => RequestCurlExporter.Prepare(
                    draft, new ReplayTransportOptions(ReplayHttpVersion.Auto, endpoint), redirects, token),
                "proxy" => RequestCurlExporter.Prepare(
                    draft, new ReplayTransportOptions(ReplayHttpVersion.Auto, null, upstream), redirects, token),
                "client-certificate" => RequestCurlExporter.Prepare(
                    draft, new ReplayTransportOptions(ReplayHttpVersion.Auto, null, null, certificate), redirects, token),
                "windows-auth" => RequestCurlExporter.Prepare(
                    draft, new ReplayTransportOptions(ReplayHttpVersion.Auto, null, null, null, WindowsAuth: true), redirects, token),
                _ => RequestCurlExporter.Prepare(draft, transport, redirects, token),
            };
            try
            {
                Find<TextBox>(window, "MethodBox").Text = refusal == "head-body" ? "HEAD" : "POST";
                Find<TextBox>(window, "UrlBox").Text = "https://127.0.0.1:9/items/";
                Find<TextBox>(window, "HeadersBox").Text = refusal switch
                {
                    "control-header" => "Fiddler-Host: 127.0.0.2",
                    "too-long" => "X-Long: " + new string('a', 20_000),
                    _ => "X-Test: refused",
                };
                Find<CheckBox>(window, "Base64Box").IsChecked = refusal == "not-base64";
                Find<TextBox>(window, "BodyBox").Text = refusal == "not-base64" ? "%%%" : "body";

                await window.CopyAsCurlAsync();
                string notCopied = Status(window);
                await window.SaveCurlScriptAsync();

                Assert.Equal(notCopied, Status(window));
                if (refusal == "not-base64")
                {
                    Assert.StartsWith(Localization.GetLocalizedString(key, ""), notCopied);
                }
                else
                {
                    Assert.Equal(Localization.GetLocalizedString(key), notCopied);
                }

                Assert.Empty(copied);
                switch (refusal)
                {
                    case "numbered":
                        Find<TextBox>(window, "UrlBox").Text += "#";
                        break;
                    case "redirects":
                        Find<CheckBox>(window, "FollowRedirectsBox").IsChecked = true;
                        break;
                    case "http2":
                        ChooseVersion(window, ReplayHttpVersion.Http2);
                        break;
                    case "connect-to":
                        Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                        break;
                    case "proxy":
                        Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                        break;
                    case "client-certificate":
                        Find<CheckBox>(window, "ClientCertificateBox").IsChecked = true;
                        break;
                    case "windows-auth":
                        Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;
                        break;
                    default:
                        return;
                }

                Assert.Equal(notCopied, Find<Button>(window, "CopyCurlButton").ToolTip);
                Assert.Equal(notCopied, Find<Button>(window, "SaveCurlButton").ToolTip);
            }
            finally
            {
                window.Close();
            }
        });

        // Without a confirm, the window asks in its own message box, with Yes and No, as a warning, and with No as the answer
        // that Enter gives: here, whether to export for another origin and whether to save the content. Declining leaves
        // the clipboard as it was and publishes nothing.
        [Fact]
        public Task TheWindowsQuestions_AreYesOrNo_WithNoAsTheirDefault() => _application.RunAsync(async () =>
        {
            var window = new RequestComposerWindow(Capture());
            var shown = new List<(string, MessageBoxButton, MessageBoxImage, MessageBoxResult)>();
            var answers = new Queue<MessageBoxResult>(new[] { MessageBoxResult.No, MessageBoxResult.Yes, MessageBoxResult.No });
            string clipboard = "sentinel";
            var published = new List<string>();
            window.ShowQuestion = (message, buttons, image, defaultResult) =>
            {
                shown.Add((message, buttons, image, defaultResult));
                return answers.Dequeue();
            };
            window.CopyText = text => clipboard = text;
            window.ChooseCurlScriptPath = () => Path.Join(AppContext.BaseDirectory, "moved.ps1");
            window.PublishCurlScript = (path, _, _) =>
            {
                published.Add(path);
                return PrivateFilePublication.Published;
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.2:9/data";

                await window.CopyAsCurlAsync();
                await window.SaveCurlScriptAsync();

                string origin = Localization.GetLocalizedString("COMPOSER_CURL_ORIGIN", "http://127.0.0.2:9");
                string consent = Localization.GetLocalizedString("COMPOSER_SAVE_CURL_CONSENT", 0);
                Assert.Equal(
                    new[]
                    {
                        (origin, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No),
                        (origin, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No),
                        (consent, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No),
                    },
                    shown);
                Assert.Equal("sentinel", clipboard);
                Assert.Empty(published);
                Assert.Empty(answers);
            }
            finally
            {
                window.Close();
            }
        });

        // A send that the user confirmed, to another origin than the request was captured for, is no permission to export
        // it: Copy as cURL and Save as cURL script each ask about that origin again, and Save as cURL script about the
        // content too. Declining leaves the clipboard as it was and publishes nothing.
        [Fact]
        public Task AConfirmedSend_IsNoPermissionToExport_EachExportAsksAgain() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var steps = new List<string>();
            var answers = new Queue<bool>(new[] { true, false, false, true, false });
            var window = new RequestComposerWindow(Capture(), client, message =>
            {
                steps.Add(message);
                return answers.Dequeue();
            });
            string clipboard = "sentinel";
            var published = new List<string>();
            window.CopyText = text => clipboard = text;
            window.ChooseCurlScriptPath = () =>
            {
                steps.Add("where");
                return Path.Join(AppContext.BaseDirectory, "moved.ps1");
            };
            window.PublishCurlScript = (path, _, _) =>
            {
                published.Add(path);
                return PrivateFilePublication.Published;
            };
            try
            {
                string moved = server.Origin + "/moved";
                Find<TextBox>(window, "UrlBox").Text = moved;

                await window.SendAsync();

                Assert.Single(server.Requests);

                await window.CopyAsCurlAsync();
                await window.SaveCurlScriptAsync();
                await window.SaveCurlScriptAsync();

                string origin = Localization.GetLocalizedString("COMPOSER_CURL_ORIGIN", server.Origin);
                Assert.Equal(
                    new[]
                    {
                        Localization.GetLocalizedString("COMPOSER_CONFIRM_MESSAGE", "GET", moved),
                        origin,
                        "where",
                        origin,
                        "where",
                        origin,
                        Localization.GetLocalizedString("COMPOSER_SAVE_CURL_CONSENT", 0),
                    },
                    steps);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED"), Status(window));
                Assert.Equal("sentinel", clipboard);
                Assert.Empty(published);
                Assert.Single(server.Requests);
                Assert.Empty(answers);
            }
            finally
            {
                window.Close();
            }
        });

        // Nothing in the window clears the clipboard: copying only replaces it, and a command that is not copied leaves it as
        // it was. The scan covers the window's own code, its lambdas and state machines included, which do set the clipboard.
        [Fact]
        public void TheWindow_NeverClearsTheClipboard()
        {
            IReadOnlyList<string> used = ForbiddenCallScanner.MembersUsedBy(
                typeof(RequestComposerWindow).Assembly.Location, new[] { typeof(RequestComposerWindow).FullName });

            Assert.Contains("System.Windows.Clipboard::SetText", used);
            Assert.DoesNotContain(used, member => member.EndsWith("Clipboard::Clear", StringComparison.Ordinal));
        }

        // A request captured for http://127.0.0.1:9, with a credential.
        private static ProxyConnectionModel Capture() =>
            new(7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "GET", "/data",
                "GET /data HTTP/1.1\r\nHost: 127.0.0.1:9\r\nAuthorization: Bearer canary-credential\r\n", [],
                "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");

        // Chooses an HTTP version as the user does.
        private static void ChooseVersion(Window window, ReplayHttpVersion version)
        {
            var box = Find<ComboBox>(window, "HttpVersionBox");
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(item => version.Equals(item.Tag));
        }

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        private static string FixtureDirectory()
        {
            string directory = Path.Join(AppContext.BaseDirectory, "CurlExportFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
