// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Drawing;
using System.Windows.Threading;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.NetworkTraceCaptureAnalysis.Models;
using XMAT.SharedInterfaces;
using ProxyPreferences = XMAT.WebServiceCapture.Models.PreferencesModel;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    internal sealed class AdministrationTestData : IDisposable
    {
        internal CaptureAppModel Model { get; } = new()
        {
            PreferencesModel = new AppPreferencesModel
            {
                WebProxyCapturePreferences = new ProxyPreferences(),
                NetworkAnalysisPreferences = new NetworkTraceAnalysisOptionsModel()
            }
        };
        internal AgentRuntime Runtime { get; } = new();
        internal AgentSession Session { get; }
        internal ApplicationPreferences Preferences { get; }
        internal ApplicationDiagnostics Diagnostics { get; }
        internal AgentAdministrationApplication Agent { get; }
        internal WebServiceCapture.ProxyPortPool Ports { get; } = new();
        internal int SaveCount { get; private set; }
        internal List<string> AppliedThemes { get; } = new();
        internal Func<Task> Save { get; set; } = () => Task.CompletedTask;

        internal AdministrationTestData()
        {
            Preferences = new(Model, Dispatcher.CurrentDispatcher, Ports,
                async () => { SaveCount++; await Save(); }, AppliedThemes.Add, () => ["en-us", "fr-fr"]);
            Diagnostics = new(Model.CaptureFileExports, () => new("1.0", "10.0", "10.0", false, false, null),
                () => null, () => null);
            Runtime.Enable();
            Session = Runtime.Connect("administration tests");
            Agent = new(Runtime, Model.HostPolicyOperations, Preferences, Diagnostics,
                Model.DesktopActions, () => Runtime.IsEnabled, Model.CaptureFileExports);
        }

        internal void Grant(params AgentCapability[] capabilities) =>
            Finish(Runtime.SetPermissionsAsync(Session, capabilities.Select(capability =>
                new AgentPermission(capability, AgentScope.Application))));

        internal void Approve(Guid operation)
        {
            Assert.True(Runtime.Approve(operation));
            Finish(Runtime.WaitForCompletionAsync(Session, operation));
        }

        public void Dispose()
        {
            Finish(Runtime.DisposeAsync().AsTask());
            Finish(Agent.DisposeAsync().AsTask());
            Finish(Model.AgentAccess.DisposeAsync().AsTask());
        }

        internal sealed class DirectoryFixture : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(
                Directory.GetCurrentDirectory(), $".xmat-administration-tests-{Guid.NewGuid():N}");
            internal DirectoryFixture() => Directory.CreateDirectory(Path);
            public void Dispose() => Directory.Delete(Path, recursive: true);
        }

        internal sealed class Context(string name) : ICaptureDeviceContext
        {
            internal Controller Controller { get; } = new();
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => name;
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => Controller;
        }

        internal sealed class Controller : IDeviceCaptureController
        {
            public bool IsRunning { get; private set; }
            public ICaptureMethod CaptureMethod => null;
            public object Parameters => null;
            public event PropertyChangedEventHandler PropertyChanged;
            internal void SetRunning(bool value)
            {
                IsRunning = value;
                PropertyChanged?.Invoke(this, new(nameof(IsRunning)));
            }
            public void Initialize() { }
            public void LoadCaptures(IEnumerable<IDataset> tables) { }
            public void ClearAllCaptures() { }
            public Task<bool> CanCloseAsync() => Task.FromResult(true);
            public Task CloseAsync() => Task.CompletedTask;
        }
    }
}
