// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Operations;
using XMAT.Operations.Inspection;

namespace XMAT.WebServiceCapture
{
    public class JsonTreeViewItem : INotifyPropertyChanged
    {
        public JsonTreeViewItem()
        {
            JsonTreeViewItems = new ObservableCollection<JsonTreeViewItem>();
        }

        public string PropertyDescriptor { get; set; }

        public ObservableCollection<JsonTreeViewItem> JsonTreeViewItems { get; }

        // Collapsed unless the tree expands it (BodyPreviewDisplay.Tree), so a large document never expands every node.
        private bool _autoExpandJson;
        public bool AutoExpandJson
        {
            get { return _autoExpandJson; }
            set { _autoExpandJson = value; RaisePropertyChange(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// Interaction logic for RequestResponseView.xaml
    /// </summary>
    /// <remarks>
    /// The Body and JSON tabs show one bounded preview of the body (BodyPreviewOperation), made off the UI thread
    /// whenever the headers, the body or its framing change and only while the view is loaded (BodyPreviewPipeline).
    /// </remarks>
    public partial class RequestResponseView : UserControl, IBodyPreviewTarget
    {
        private readonly BodyPreviewPipeline _preview;

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        // Using a DependencyProperty as the backing store for Title.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(RequestResponseView));

        public string FirstLineAndHeaders
        {
            get { return (string)GetValue(FirstLineAndHeadersProperty); }
            set { SetValue(FirstLineAndHeadersProperty, value); }
        }

        // Using a DependencyProperty as the backing store for FirstLineAndHeaders.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty FirstLineAndHeadersProperty =
            DependencyProperty.Register("FirstLineAndHeaders", typeof(string), typeof(RequestResponseView));

        public Dictionary<string, string> HeaderDictionary
        {
            get { return (Dictionary<string, string>)GetValue(HeaderDictionaryProperty); }
            set { SetValue(HeaderDictionaryProperty, value); }
        }

        // Using a DependencyProperty as the backing store for HeaderDictionary.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty HeaderDictionaryProperty =
            DependencyProperty.Register(
                "HeaderDictionary",
                typeof(Dictionary<string, string>),
                typeof(RequestResponseView),
                new PropertyMetadata(null, OnPreviewInputChanged));

        public byte[] BodyBytes
        {
            get { return (byte[])GetValue(BodyBytesProperty); }
            set { SetValue(BodyBytesProperty, value); }
        }

        // Using a DependencyProperty as the backing store for BodyBytes.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty BodyBytesProperty =
           DependencyProperty.Register(
               "BodyBytes",
               typeof(byte[]),
               typeof(RequestResponseView),
               new PropertyMetadata(null, OnPreviewInputChanged));

        // Whether the body is as the HTTP stack delivered it, its transfer framing already removed: the rows of the
        // Replays tab and the composer's response. Its Transfer-Encoding is then never decoded again; a captured or
        // imported body's chunked framing is decoded only when it parses (BodyFraming).
        public bool BodyIsDecoded
        {
            get => (bool)GetValue(BodyIsDecodedProperty);
            set => SetValue(BodyIsDecodedProperty, value);
        }

        public static readonly DependencyProperty BodyIsDecodedProperty =
            DependencyProperty.Register(
                nameof(BodyIsDecoded),
                typeof(bool),
                typeof(RequestResponseView),
                new PropertyMetadata(false, OnPreviewInputChanged));

        public RequestResponseView()
        {
            _preview = new BodyPreviewPipeline(this);
            InitializeComponent();

            HeadersTab.Header = Localization.GetLocalizedString("WEBCAP_REQRES_TAB_HEADERS");
            BodyTab.Header = Localization.GetLocalizedString("WEBCAP_REQRES_TAB_BODY");
            JsonTab.Header = Localization.GetLocalizedString("WEBCAP_REQRES_TAB_JSON");

            Loaded += (_, _) => StartPreviews();
            Unloaded += (_, _) => StopPreview();
        }

        // Runs this view's previews (BodyPreviewPipeline.Runner); tests replace it to decide when previews complete.
        internal Func<BodyPreviewRequest, CancellationToken, Task<OperationResult<BodyPreview>>> PreviewRunner
        {
            get => _preview.Runner;
            set => _preview.Runner = value;
        }

        // Stops previewing: the preview in progress is cancelled and its result never shown. The view does so when it
        // is unloaded or the application exits, and a window that closes calls it at once.
        internal void StopPreview()
        {
            Dispatcher.ShutdownStarted -= Dispatcher_ShutdownStarted;
            _preview.Deactivate();
        }

        private void StartPreviews()
        {
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.ShutdownStarted -= Dispatcher_ShutdownStarted;
            Dispatcher.ShutdownStarted += Dispatcher_ShutdownStarted;
            _preview.Activate();
        }

        private void Dispatcher_ShutdownStarted(object sender, EventArgs e) => StopPreview();

        private static void OnPreviewInputChanged(DependencyObject view, DependencyPropertyChangedEventArgs e) =>
            ((RequestResponseView)view)._preview.InputChanged();

        // The same dictionary, body array and framing are the same input.
        object IBodyPreviewTarget.InputIdentity =>
            HeaderDictionary == null && BodyBytes == null ? null : (object)(HeaderDictionary, BodyBytes, BodyIsDecoded);

        BodyPreviewRequest IBodyPreviewTarget.CreateRequest() =>
            BodyPreviewRequest.FromHeaders(
                HeaderDictionary,
                BodyBytes ?? Array.Empty<byte>(),
                BodyIsDecoded ? BodyFraming.Decoded : BodyFraming.Advisory);

        void IBodyPreviewTarget.ShowNothing() => Show(null, null, null, null);

        void IBodyPreviewTarget.ShowPending() =>
            Show(BodyPreviewDisplay.Pending, BodyPreviewDisplay.Pending, null, null);

        void IBodyPreviewTarget.ShowFailure() => Show(BodyPreviewDisplay.Failed, BodyPreviewDisplay.Failed, null, null);

        void IBodyPreviewTarget.ShowPreview(BodyPreview preview)
        {
            string message = BodyPreviewDisplay.Message(preview, "BODY_PREVIEW_EMPTY");
            Show(message, BodyPreviewDisplay.JsonMessage(preview, message), preview.Text, preview.JsonDocuments);
        }

        void IBodyPreviewTarget.Post(Action action) =>
            _ = Dispatcher.InvokeAsync(action, DispatcherPriority.Background);

        private void Show(string message, string jsonMessage, string text, IReadOnlyList<BodyPreviewNode> documents)
        {
            ShowMessage(BodyMessage, message);
            ShowMessage(JsonMessage, jsonMessage);
            BodyTextBox.Text = text ?? string.Empty;
            JsonTree.ItemsSource = BodyPreviewDisplay.Tree(documents);
        }

        private static void ShowMessage(TextBlock bar, string message)
        {
            bar.Text = message ?? string.Empty;
            bar.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
