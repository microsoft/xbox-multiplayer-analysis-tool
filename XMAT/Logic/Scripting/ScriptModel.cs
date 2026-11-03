// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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

        internal Guid Id { get; } = Guid.NewGuid();
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

                    wasEnabled = _isEnabled;
                    SetSourceCore(value);
                }

                NotifySourceChanged(wasEnabled);
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

        internal Task<ScriptActivationResult> EnableScriptAsync<T>() =>
            EnableScriptAsync<T>(null);

        internal async Task<ScriptActivationResult> EnableScriptAsync<T>(string expectedSourceHash) =>
            (await ActivateScriptAsync<T>(null, expectedSourceHash, false, CancellationToken.None)).Outcome;

        internal async Task<ScriptActivationReceipt> ActivateScriptAsync<T>(
            long? expectedRevision, string expectedSourceHash, bool exactSourceOnly, CancellationToken cancellationToken)
        {
            long activationVersion;
            string script;
            lock (_syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((expectedRevision.HasValue && expectedRevision.Value != _activationVersion) ||
                    (expectedSourceHash != null && !string.Equals(expectedSourceHash, GetSourceHash(_script), StringComparison.Ordinal)))
                {
                    return Receipt(ScriptActivationResult.Superseded, false);
                }

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
                diagnostics = await _host.CompileScriptAsync<T>(script, exactSourceOnly, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var current = false;
                lock (_syncRoot)
                {
                    if (activationVersion == _activationVersion)
                    {
                        _compiledScript = null;
                        _isEnabled = false;
                        _isCompiling = false;
                        _host.ClearScript();
                        current = true;
                    }
                }
                if (current)
                {
                    RaisePropertyChange(nameof(IsEnabled));
                    RaisePropertyChange(nameof(IsCompiling));
                }
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"Unexpected script compilation failure ({ex.GetType().Name}).");
                ScriptActivationReceipt failure;
                lock (_syncRoot)
                {
                    if (activationVersion != _activationVersion ||
                        !string.Equals(script, _script, StringComparison.Ordinal))
                    {
                        return Receipt(ScriptActivationResult.Superseded, false);
                    }

                    _compiledScript = null;
                    _isEnabled = false;
                    _isCompiling = false;
                    _compilationStatus = Array.Empty<Diagnostic>();
                    _compilationMessage = Localization.GetLocalizedString(
                        "SCRIPT_COMPILE_UNEXPECTED_FAILURE");
                    _host.ClearScript();
                    failure = Receipt(ScriptActivationResult.Failed, true);
                }

                RaisePropertyChange(nameof(IsEnabled));
                RaisePropertyChange(nameof(IsCompiling));
                RaiseCompilationStatusChanged();
                return failure;
            }

            ScriptActivationResult result;
            ScriptActivationReceipt receipt;
            var cancelledAtCommit = false;
            lock (_syncRoot)
            {
                if (activationVersion != _activationVersion ||
                    !string.Equals(script, _script, StringComparison.Ordinal))
                {
                    return Receipt(ScriptActivationResult.Superseded, false);
                }

                _compilationStatus = diagnostics;
                _isCompiling = false;
                bool hasErrors = diagnostics.Any(
                    diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                if (cancellationToken.IsCancellationRequested || string.IsNullOrEmpty(script) || hasErrors)
                {
                    _compiledScript = null;
                    _isEnabled = false;
                    _compilationMessage = null;
                    result = ScriptActivationResult.Failed;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _host.ClearScript();
                        cancelledAtCommit = true;
                    }
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
                receipt = Receipt(result, true);
            }

            RaisePropertyChange(nameof(IsCompiling));
            RaisePropertyChange(nameof(IsEnabled));
            RaiseCompilationStatusChanged();
            if (cancelledAtCommit)
                throw new OperationCanceledException(cancellationToken);
            return receipt;
        }

        public async Task<ScriptValidationResult> ValidateScriptAsync<T>(
            CancellationToken cancellationToken = default) =>
            await ValidateScriptAsync<T>(null, null, cancellationToken);

        internal async Task<ScriptValidationResult> ValidateScriptAsync<T>(
            long? expectedRevision, string expectedSourceHash, CancellationToken cancellationToken)
        {
            string script;
            long revision;
            lock (_syncRoot)
            {
                if ((expectedRevision.HasValue && _activationVersion != expectedRevision.Value) ||
                    (expectedSourceHash != null && expectedSourceHash != GetSourceHash(_script)))
                    throw new ScriptRevisionChangedException();
                script = _script;
                revision = _activationVersion;
            }

            var diagnostics = await _host.ValidateScriptAsync<T>(script, cancellationToken);
            lock (_syncRoot)
            {
                return new ScriptValidationResult(
                    GetSourceHash(script),
                    revision == _activationVersion && string.Equals(script, _script, StringComparison.Ordinal),
                    Array.AsReadOnly(diagnostics), revision);
            }
        }

        private static string GetSourceHash(string script) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script ?? string.Empty)));

        internal void DisableScript() => TryDisableScript(null, out _);

        internal bool TryDisableScript(long? expectedRevision, out ScriptStateSnapshot snapshot)
        {
            bool wasEnabled;
            bool wasCompiling;
            lock (_syncRoot)
            {
                if (expectedRevision.HasValue && expectedRevision.Value != _activationVersion)
                {
                    snapshot = State();
                    return false;
                }
                ++_activationVersion;
                _compiledScript = null;
                wasEnabled = _isEnabled;
                wasCompiling = _isCompiling;
                _isEnabled = false;
                _isCompiling = false;
                _host.ClearScript();
                snapshot = State();
            }

            if (wasEnabled)
                RaisePropertyChange(nameof(IsEnabled));
            if (wasCompiling)
                RaisePropertyChange(nameof(IsCompiling));
            return true;
        }

        internal ScriptStateSnapshot CaptureState()
        {
            lock (_syncRoot)
            { return State(); }
        }

        internal bool TryUpdateScript(string source, long expectedRevision, out ScriptStateSnapshot snapshot)
        {
            bool wasEnabled;
            bool changed;
            lock (_syncRoot)
            {
                if (_activationVersion != expectedRevision)
                {
                    snapshot = State();
                    return false;
                }
                wasEnabled = _isEnabled;
                changed = !string.Equals(_script, source, StringComparison.Ordinal);
                if (changed)
                    SetSourceCore(source);
                snapshot = State();
            }
            if (changed)
                NotifySourceChanged(wasEnabled);
            return true;
        }

        private ScriptStateSnapshot State() => new(Id, _activationVersion, GetSourceHash(_script),
            _script, _isEnabled, _isCompiling);

        private ScriptActivationReceipt Receipt(ScriptActivationResult outcome, bool changed) =>
            new(outcome, State(), changed, Array.AsReadOnly(_compilationStatus));

        private void SetSourceCore(string source)
        {
            _script = source;
            ++_activationVersion;
            _compiledScript = null;
            _isEnabled = false;
            _isCompiling = false;
            _compilationStatus = Array.Empty<Diagnostic>();
            _compilationMessage = null;
            _host.ClearScript();
        }

        private void NotifySourceChanged(bool wasEnabled)
        {
            RaisePropertyChange(nameof(Script));
            if (wasEnabled)
                RaisePropertyChange(nameof(IsEnabled));
            RaisePropertyChange(nameof(IsCompiling));
            RaiseCompilationStatusChanged();
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
