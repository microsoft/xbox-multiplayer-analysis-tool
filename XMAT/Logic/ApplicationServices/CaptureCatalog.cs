// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.Scripting;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureCatalog(CaptureAppModel model, Dispatcher dispatcher) : ICaptureCatalog
    {
        private readonly ConditionalWeakTable<ICaptureDeviceContext, Identity> _identities = new();

        public Task<IReadOnlyList<CaptureDescriptor>> GetCapturesAsync(CancellationToken cancellationToken = default) =>
            InvokeAsync<IReadOnlyList<CaptureDescriptor>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<CaptureDescriptor>>(
                    model.CaptureDeviceContexts.Select(DescribeTarget).ToArray());
            });

        internal Task<T> WithTargetAsync<T>(
            Guid id,
            long? expectedRevision,
            Func<ICaptureDeviceContext, CaptureDescriptor, Task<T>> action)
        {
            return InvokeAsync(() =>
            {
                var target = model.CaptureDeviceContexts.FirstOrDefault(context => GetIdentity(context).Id == id)
                    ?? throw new AgentAccessException(AgentAccessError.OperationNotFound);
                var descriptor = DescribeTarget(target);
                if (expectedRevision.HasValue && descriptor.Revision != expectedRevision.Value)
                {
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                }
                return action(target, descriptor);
            });
        }

        internal CaptureDescriptor DescribeTarget(ICaptureDeviceContext target)
        {
            var identity = GetIdentity(target);
            var proxy = target.CaptureController as WebServiceDeviceCaptureController;
            var trace = target.CaptureController as NetworkTraceCaptureController;
            return new(identity.Id, target.DeviceName, target.DeviceType, target.CaptureType,
                target.IsReadOnly, target.CaptureController.IsRunning, identity.Revision,
                identity.CaptureEpoch, proxy == null ? null : proxy.Port, proxy?.Scripts.ActiveScripts.Count ?? 0,
                RecoveryRequired: identity.RecoveryRequired || trace?.RecoveryRequired == true,
                TraceRecoveryPhase: trace?.RecoveryPhase ?? CaptureTraceRecoveryPhase.None,
                TraceMayBeRunning: trace?.MayBeTracing == true,
                PacketCount: trace?.NetworkTracePackets?.Count,
                LocalTraceSession: trace?.LocalTraceSession);
        }

        internal void SetRecoveryRequired(ICaptureDeviceContext target, bool value) =>
            GetIdentity(target).SetRecoveryRequired(value);

        internal CaptureDescriptor RecordRemoval(ICaptureDeviceContext target)
        {
            GetIdentity(target).RecordRemoval();
            return DescribeTarget(target);
        }

        internal void RecordDataReplacement(ICaptureDeviceContext target) => GetIdentity(target).RecordRemoval();

        private Identity GetIdentity(ICaptureDeviceContext target) =>
            _identities.GetValue(target, context => new Identity(context));

        private Task<T> InvokeAsync<T>(Func<Task<T>> action) => App.RunOnUiAsync(dispatcher, action);

        private sealed class Identity
        {
            private readonly object _gate = new();
            private readonly ICaptureDeviceContext _target;
            private readonly HashSet<ScriptModel> _scripts = new();
            private bool _running;
            private int _port;
            private long _revision = 1;
            private long _captureEpoch = 1;
            private bool _recoveryRequired;

            internal Guid Id { get; } = Guid.NewGuid();
            internal long Revision { get { lock (_gate) { return _revision; } } }
            internal long CaptureEpoch { get { lock (_gate) { return _captureEpoch; } } }
            internal bool RecoveryRequired { get { lock (_gate) { return _recoveryRequired; } } }

            internal void SetRecoveryRequired(bool value)
            {
                lock (_gate)
                {
                    if (_recoveryRequired != value)
                    {
                        _recoveryRequired = value;
                        _revision++;
                    }
                }
            }

            internal void RecordRemoval()
            {
                lock (_gate)
                {
                    _revision++;
                    _captureEpoch++;
                }
            }

            internal Identity(ICaptureDeviceContext target)
            {
                _target = target;
                _running = target.CaptureController.IsRunning;
                _port = (target.CaptureController as WebServiceDeviceCaptureController)?.Port ?? 0;
                PropertyChangedEventManager.AddHandler(target.CaptureController, Changed, string.Empty);
                if (target is INotifyPropertyChanged context)
                {
                    PropertyChangedEventManager.AddHandler(context, Changed, string.Empty);
                }
                if (target.CaptureController is WebServiceDeviceCaptureController proxy)
                {
                    CollectionChangedEventManager.AddHandler(proxy.Scripts, ScriptsChanged);
                    ObserveScripts(proxy.Scripts);
                }
            }

            private void ScriptsChanged(object sender, NotifyCollectionChangedEventArgs args)
            {
                ObserveScripts((ScriptCollection)sender);
                lock (_gate)
                { _revision++; }
            }

            private void ObserveScripts(ScriptCollection scripts)
            {
                foreach (var old in _scripts)
                {
                    PropertyChangedEventManager.RemoveHandler(old, ScriptChanged, string.Empty);
                }
                _scripts.Clear();
                foreach (var script in scripts)
                {
                    _scripts.Add(script);
                    PropertyChangedEventManager.AddHandler(script, ScriptChanged, string.Empty);
                }
            }

            private void ScriptChanged(object sender, PropertyChangedEventArgs args)
            {
                if (string.IsNullOrEmpty(args.PropertyName) ||
                    args.PropertyName is nameof(ScriptModel.Script) or nameof(ScriptModel.IsEnabled))
                {
                    lock (_gate)
                    { _revision++; }
                }
            }

            private void Changed(object sender, PropertyChangedEventArgs args)
            {
                if (!string.IsNullOrEmpty(args.PropertyName) &&
                    args.PropertyName is not ("IsRunning" or "IsTransitioning" or "Port" or
                        "PendingPort" or "DeviceStatus" or "IsReadOnly" or "DeviceName"))
                {
                    return;
                }
                lock (_gate)
                {
                    _revision++;
                    var running = _target.CaptureController.IsRunning;
                    var port = (_target.CaptureController as WebServiceDeviceCaptureController)?.Port ?? 0;
                    if (running != _running || port != _port)
                    {
                        _running = running;
                        _port = port;
                        _captureEpoch++;
                    }
                }
            }
        }
    }
}
