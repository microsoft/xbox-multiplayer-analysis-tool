// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using XMAT.Telemetry;

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
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    XmatTelemetryService.Current.Interact(Feature,
                        value ? TelemetryInteraction.Enabled : TelemetryInteraction.Disabled);
                }
                _isEnabled = value;
                RaisePropertyChange();
            }
        }
        internal TelemetryFeature Feature { get; }
        public Diagnostic[] CompilationStatus { get => _compilationStatus; set { _compilationStatus = value; RaisePropertyChange(); } }

        public ScriptModel(Enum eventType)
        {
            Feature = eventType switch
            {
                WebServiceCaptureScriptableEventType.SslConnectionRequest => TelemetryFeature.SslScript,
                WebServiceCaptureScriptableEventType.WebRequest => TelemetryFeature.RequestScript,
                WebServiceCaptureScriptableEventType.WebResponse => TelemetryFeature.ResponseScript,
                _ => TelemetryFeature.Scripting
            };
            DisplayName = Localization.GetLocalizedString(GetEnumAttribute<DisplayAttribute>(eventType).Name);
            Description = Localization.GetLocalizedString(GetEnumAttribute<DescriptionAttribute>(eventType).Description);
            Script = Localization.GetLocalizedString(GetEnumAttribute<DefaultValueAttribute>(eventType).Value as string);
            CompilationStatus = Array.Empty<Diagnostic>();
        }

        public async Task CompileScriptAsync<T>()
        {
            using var operation = XmatTelemetryService.Current.Begin(TelemetryOperationKind.CompileScript, Feature);
            try
            {
                CompilationStatus = await _host.CompileScriptAsync<T>(Script);
                var errors = CompilationStatus.Count(x => x.Severity == DiagnosticSeverity.Error);
                operation.Complete(errors > 0 ? TelemetryOutcome.Failed : TelemetryOutcome.Succeeded,
                    errors > 0 ? TelemetryError.Compilation : TelemetryError.None, items: errors);
            }
            catch (Exception ex)
            {
                operation.Fail(ex);
                throw;
            }
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
