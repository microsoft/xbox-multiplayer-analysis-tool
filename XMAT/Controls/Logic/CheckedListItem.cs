// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XMAT
{
    public class CheckedListItem : INotifyPropertyChanged
    {
        private string _text;
        private bool _isChecked;

        public string Text { get => _text; set { _text = value; RaisePropertyChange(); } }
        public bool IsChecked { get => _isChecked; set { _isChecked = value; RaisePropertyChange(); } }

        public CheckedListItem()
        {
            // constructor with no params required
        }

        public CheckedListItem(string text, bool isChecked = true)
        {
            Text = text;
            IsChecked = isChecked;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));  
        }

        public override string ToString()
        {
            return Text;
        }
    }
}
