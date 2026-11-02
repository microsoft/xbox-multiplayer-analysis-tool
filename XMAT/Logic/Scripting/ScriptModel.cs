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
        private bool _isCompiling;
        private Diagnostic[] _compilationStatus;
        private string _compilationMessage;
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
                    _isCompiling = false;
                    _compilationStatus = Array.Empty<Diagnostic>();
                    _compilationMessage = null;
                    _host.ClearScript();
                }

                RaisePropertyChange();
                if (wasEnabled)
                    RaisePropertyChange(nameof(IsEnabled));
                RaisePropertyChange(nameof(IsCompiling));
                RaiseCompilationStatusChanged();
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
        public bool IsCompiling
        {
            get
            {
                lock (_syncRoot)
                {
                    return _isCompiling;
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
        public string CompilationMessage
        {
            get
            {
                lock (_syncRoot)
                {
                    return _compilationMessage;
                }
            }
        }
        public bool HasCompilationDiagnostics
        {
            get
            {
                lock (_syncRoot)
                {
                    return _compilationStatus.Length > 0;
                }
            }
        }
        public bool HasCompilationMessage
        {
            get
            {
                lock (_syncRoot)
                {
                    return !string.IsNullOrEmpty(_compilationMessage);
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
                _isCompiling = true;
                _compilationStatus = Array.Empty<Diagnostic>();
                _compilationMessage = null;
                _host.ClearScript();
            }
            RaisePropertyChange(nameof(IsEnabled));
            RaisePropertyChange(nameof(IsCompiling));
            RaiseCompilationStatusChanged();

            Diagnostic[] diagnostics;
            try
            {
                diagnostics = await _host.CompileScriptAsync<T>(script);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"Unexpected script compilation failure: {ex}");
                lock (_syncRoot)
                {
                    if (activationVersion != _activationVersion ||
                        !string.Equals(script, _script, StringComparison.Ordinal))
                    {
                        return ScriptActivationResult.Superseded;
                    }

                    _compiledScript = null;
                    _isEnabled = false;
                    _isCompiling = false;
                    _compilationStatus = Array.Empty<Diagnostic>();
                    _compilationMessage = Localization.GetLocalizedString(
                        "SCRIPT_COMPILE_UNEXPECTED_FAILURE");
                    _host.ClearScript();
                }

                RaisePropertyChange(nameof(IsEnabled));
                RaisePropertyChange(nameof(IsCompiling));
                RaiseCompilationStatusChanged();
                return ScriptActivationResult.Failed;
            }

            ScriptActivationResult result;
            lock (_syncRoot)
            {
                if (activationVersion != _activationVersion ||
                    !string.Equals(script, _script, StringComparison.Ordinal))
                {
                    return ScriptActivationResult.Superseded;
                }

                _compilationStatus = diagnostics;
                _isCompiling = false;
                bool hasErrors = diagnostics.Any(
                    diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                if (string.IsNullOrEmpty(script) || hasErrors)
                {
                    _compiledScript = null;
                    _isEnabled = false;
                    _compilationMessage = null;
                    result = ScriptActivationResult.Failed;
                }
                else
                {
                    _compiledScript = script;
                    _isEnabled = true;
                    _compilationMessage = diagnostics.Any(
                        diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
                        ? Localization.GetLocalizedString("SCRIPT_SUCCESS_WITH_WARNINGS")
                        : Localization.GetLocalizedString("SCRIPT_SUCCESS");
                    result = ScriptActivationResult.Enabled;
                }
            }

            RaisePropertyChange(nameof(IsCompiling));
            RaisePropertyChange(nameof(IsEnabled));
            RaiseCompilationStatusChanged();
            return result;
        }

        internal void DisableScript()
        {
            bool wasEnabled;
            bool wasCompiling;
            lock (_syncRoot)
            {
                ++_activationVersion;
                _compiledScript = null;
                wasEnabled = _isEnabled;
                wasCompiling = _isCompiling;
                _isEnabled = false;
                _isCompiling = false;
                _host.ClearScript();
            }

            if (wasEnabled)
                RaisePropertyChange(nameof(IsEnabled));
            if (wasCompiling)
                RaisePropertyChange(nameof(IsCompiling));
        }

        private void RaiseCompilationStatusChanged()
        {
            RaisePropertyChange(nameof(CompilationStatus));
            RaisePropertyChange(nameof(HasCompilationDiagnostics));
            RaisePropertyChange(nameof(CompilationMessage));
            RaisePropertyChange(nameof(HasCompilationMessage));
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
