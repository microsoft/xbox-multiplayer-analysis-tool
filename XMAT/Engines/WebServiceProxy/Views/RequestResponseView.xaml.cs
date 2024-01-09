// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

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

        private bool _autoExpandJson = true;
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
    public partial class RequestResponseView : UserControl
    {
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

        public string BodyText
        {
            get => (string)GetValue(BodyTextProperty);
            set => SetValue(BodyTextProperty, value);
        }

        // Using a DependencyProperty as the backing store for BodyText.  This enables animation, styling, binding,
        // etc...
        public static readonly DependencyProperty BodyTextProperty =
            DependencyProperty.Register(
                nameof(BodyText),
                typeof(string),
                typeof(RequestResponseView));

        public Dictionary<string,string> HeaderDictionary
        {
            get { return (Dictionary<string,string>)GetValue(HeaderDictionaryProperty); }
            set { SetValue(HeaderDictionaryProperty, value); }
        }

        // Using a DependencyProperty as the backing store for HeaderDictionary.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty HeaderDictionaryProperty =
            DependencyProperty.Register("HeaderDictionary", typeof(Dictionary<string,string>), typeof(RequestResponseView));

        public byte[] BodyBytes
        {
            get { return (byte[])GetValue(BodyBytesProperty); }
            set { SetValue(BodyBytesProperty, value); }
        }

        // Using a DependencyProperty as the backing store for BodyBytes.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty BodyBytesProperty =
           DependencyProperty.Register("BodyBytes", typeof(byte[]), typeof(RequestResponseView));

        public RequestResponseView()
        {
            InitializeComponent();

            HeadersTab.Header = Localization.GetLocalizedString("WEBCAP_REQRES_TAB_HEADERS");
            BodyTab.Header = Localization.GetLocalizedString("WEBCAP_REQRES_TAB_BODY");
            JsonTab.Header = Localization.GetLocalizedString("WEBCAP_REQRES_TAB_JSON");
        }
    }
}
