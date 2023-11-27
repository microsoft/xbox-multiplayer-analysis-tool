// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace XMAT.Scripting
{
    public class ScriptModel : INotifyPropertyChanged
    {
        private readonly ScriptHost _host = new();

        private string _filename;
        private string _script;
        private bool _isEnabled;
        private Diagnostic[] _compilationStatus;

        public string DisplayName { get; private set; }
        public string Description { get; private set; }
        public string Filename { get => _filename; set { _filename = value; RaisePropertyChange(); } }
        public string Script { get => _script; set { _script = value; RaisePropertyChange(); } }
        public bool IsEnabled { get => _isEnabled; set { _isEnabled = value; RaisePropertyChange(); } }
        public Diagnostic[] CompilationStatus { get => _compilationStatus; set { _compilationStatus = value; RaisePropertyChange(); } }

        public ScriptModel(Enum eventType)
        {
            DisplayName = Localization.GetLocalizedString(GetEnumAttribute<DisplayAttribute>(eventType).Name);
            Description = Localization.GetLocalizedString(GetEnumAttribute<DescriptionAttribute>(eventType).Description);
            Script      = Localization.GetLocalizedString(GetEnumAttribute<DefaultValueAttribute>(eventType).Value as string);
            CompilationStatus = Array.Empty<Diagnostic>();
        }

        public async Task CompileScriptAsync<T>()
        {
            CompilationStatus = await _host.CompileScriptAsync<T>(Script);
        }

        private T GetEnumAttribute<T>(Enum enumType) where T : Attribute
        {
            return enumType.GetType().GetMember(enumType.ToString())
                           .First()
                           .GetCustomAttribute<T>();
        }

        public Task<T> RunScriptAsync<T>(T input) where T : class
        {
            if (IsEnabled)
                return _host.RunScriptAsync(input);
            else
                return Task.FromResult(input);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
