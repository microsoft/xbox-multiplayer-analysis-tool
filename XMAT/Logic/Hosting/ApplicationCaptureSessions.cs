// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Hosting
{
    internal sealed class ApplicationCaptureSessions : IDisposable
    {
        private readonly CaptureAppModel _model;
        private readonly XboxProxySessions _sessions;
        private readonly HashSet<WebServiceDeviceCaptureController> _controllers = [];

        internal ApplicationCaptureSessions(CaptureAppModel model, XboxProxySessions sessions = null)
        {
            _model = model;
            _sessions = sessions ?? new(PcProxyRecovery.DefaultDirectory, ProcessIdentity.Current);
            model.CaptureDeviceContexts.CollectionChanged += ContextsChanged;
            Synchronize();
        }

        private void ContextsChanged(object sender, NotifyCollectionChangedEventArgs args) => Synchronize();

        private void Synchronize()
        {
            var current = _model.CaptureDeviceContexts.Where(context => !context.IsReadOnly &&
                context.DeviceType == DeviceType.XboxConsole).Select(context => context.CaptureController)
                .OfType<WebServiceDeviceCaptureController>().ToHashSet();
            foreach (var removed in _controllers.Except(current).ToArray())
            {
                removed.PropertyChanged -= ControllerChanged;
                _controllers.Remove(removed);
                _sessions.SetCapturing(removed.DeviceName, false);
            }
            foreach (var added in current.Except(_controllers).ToArray())
            {
                added.PropertyChanged += ControllerChanged;
                _controllers.Add(added);
                _sessions.SetCapturing(added.DeviceName, added.IsRunning);
            }
        }

        private void ControllerChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(WebServiceDeviceCaptureController.IsRunning))
            {
                var controller = (WebServiceDeviceCaptureController)sender;
                _sessions.SetCapturing(controller.DeviceName, controller.IsRunning);
            }
        }

        public void Dispose()
        {
            _model.CaptureDeviceContexts.CollectionChanged -= ContextsChanged;
            foreach (var controller in _controllers)
                controller.PropertyChanged -= ControllerChanged;
            _controllers.Clear();
            _sessions.Clear();
        }
    }
}
