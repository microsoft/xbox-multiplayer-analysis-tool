// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace XMAT.Scripting
{
    internal enum ScriptActivationResult
    {
        Superseded,
        Failed,
        Enabled
    }

    public class ScriptModel : INotifyPropertyChanged
    {
        private readonly ScriptHost _host;
        private readonly object _syncRoot = new();

        private string _filename;
        private string _script;
        private string _compiledScript;
        private bool _isEnabled;
        private Diagnostic[] _compilationStatus;
        private long _activationVersion;

        public string DisplayName { get; private set; }
        public string Description { get; private set; }
        public string Filename { get => _filename; set { _filename = value; RaisePropertyChange(); } }
        public string Script
        {
            get
            {
                lock (_syncRoot)
                {
                    return _script;
                }
            }
            set
            {
                bool wasEnabled;
                lock (_syncRoot)
                {
                    if (string.Equals(_script, value, StringComparison.Ordinal))
                        return;

                    // Source edits invalidate the executable immediately; enabling recompiles it.
                    _script = value;
                    ++_activationVersion;
                    _compiledScript = null;
                    wasEnabled = _isEnabled;
                    _isEnabled = false;
                    _host.ClearScript();
                }

                RaisePropertyChange();
                if (wasEnabled)
                    RaisePropertyChange(nameof(IsEnabled));
            }
        }
        public bool IsEnabled
        {
            get
            {
                lock (_syncRoot)
                {
                    return _isEnabled;
                }
            }
        }
        public Diagnostic[] CompilationStatus
        {
            get
            {
                lock (_syncRoot)
                {
                    return _compilationStatus;
                }
            }
        }

        public ScriptModel(Enum eventType) : this(eventType, new ScriptHost())
        {
        }

        internal ScriptModel(Enum eventType, ScriptHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            DisplayName = Localization.GetLocalizedString(GetEnumAttribute<DisplayAttribute>(eventType).Name);
            Description = Localization.GetLocalizedString(GetEnumAttribute<DescriptionAttribute>(eventType).Description);
            _script = Localization.GetLocalizedString(GetEnumAttribute<DefaultValueAttribute>(eventType).Value as string);
            _compilationStatus = Array.Empty<Diagnostic>();
        }

        internal async Task<ScriptActivationResult> EnableScriptAsync<T>()
        {
            long activationVersion;
            string script;
            lock (_syncRoot)
            {
                activationVersion = ++_activationVersion;
                script = _script;
                _compiledScript = null;
                _isEnabled = false;
                _host.ClearScript();
            }
            RaisePropertyChange(nameof(IsEnabled));

            Diagnostic[] diagnostics = await _host.CompileScriptAsync<T>(script);
            ScriptActivationResult result;
            lock (_syncRoot)
            {
                if (activationVersion != _activationVersion ||
                    !string.Equals(script, _script, StringComparison.Ordinal))
                {
                    return ScriptActivationResult.Superseded;
                }

                _compilationStatus = diagnostics;
                bool hasErrors = diagnostics.Any(
                    diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                if (string.IsNullOrEmpty(script) || hasErrors)
                {
                    _compiledScript = null;
                    _isEnabled = false;
                    result = ScriptActivationResult.Failed;
                }
                else
                {
                    _compiledScript = script;
                    _isEnabled = true;
                    result = ScriptActivationResult.Enabled;
                }
            }

            RaisePropertyChange(nameof(CompilationStatus));
            RaisePropertyChange(nameof(IsEnabled));
            return result;
        }

        internal void DisableScript()
        {
            bool wasEnabled;
            lock (_syncRoot)
            {
                ++_activationVersion;
                _compiledScript = null;
                wasEnabled = _isEnabled;
                _isEnabled = false;
                _host.ClearScript();
            }

            if (wasEnabled)
                RaisePropertyChange(nameof(IsEnabled));
        }

        private T GetEnumAttribute<T>(Enum enumType) where T : Attribute
        {
            return enumType.GetType().GetMember(enumType.ToString())
                           .First()
                           .GetCustomAttribute<T>();
        }

        public Task<T> RunScriptAsync<T>(
            T input,
            CancellationToken cancellationToken = default) where T : class
        {
            lock (_syncRoot)
            {
                if (!_isEnabled ||
                    !string.Equals(_compiledScript, _script, StringComparison.Ordinal))
                {
                    return Task.FromResult(input);
                }
            }

            return _host.RunScriptAsync(input, cancellationToken);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
