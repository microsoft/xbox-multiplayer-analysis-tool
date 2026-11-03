// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT.Models
{
    internal partial class CaptureAppModel
    {
        private readonly object _administrationGate = new();
        private IHostPolicyOperations _hostPolicyOperations;
        private IApplicationPreferences _applicationPreferences;
        private IApplicationDiagnostics _applicationDiagnostics;
        private DesktopActions _desktopActions;

        internal IHostPolicyOperations HostPolicyOperations =>
            AdministrationService(ref _hostPolicyOperations, () => new HostPolicyOperations(HostListPolicy, _dispatcher));
        internal IApplicationPreferences ApplicationPreferences =>
            AdministrationService(ref _applicationPreferences, () => new ApplicationPreferences(this, _dispatcher));
        internal IApplicationDiagnostics ApplicationDiagnostics =>
            AdministrationService(ref _applicationDiagnostics, () => new ApplicationDiagnostics(CaptureFileExports));
        internal DesktopActions DesktopActions =>
            AdministrationService(ref _desktopActions, () => new DesktopActions(this, _dispatcher));

        private T AdministrationService<T>(ref T service, Func<T> create) where T : class
        {
            lock (_administrationGate)
            { return service ??= create(); }
        }
    }
}
