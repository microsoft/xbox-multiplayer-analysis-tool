// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XMAT.Scripting
{
    public class ScriptTypeInfo : INotifyPropertyChanged
    {
        private string name;
        private string type;
        private string description;
        private ScriptTypeCollection children;

        public string Name { get => name; set { name = value; RaisePropertyChange(); } }
        public string Type { get => type; set { type = value; RaisePropertyChange(); } }
        public string Description { get => description; set { description = value; RaisePropertyChange(); } }
        public ScriptTypeCollection Properties { get => children; set { children = value; RaisePropertyChange(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
