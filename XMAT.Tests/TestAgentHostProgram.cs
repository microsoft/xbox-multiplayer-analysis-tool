// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Hosting;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    // Executed only from an owned test installation, never a production startup bypass.
    internal static class TestAgentHostProgram
    {
        internal const string DirectoryPrefix = "XMAT.AgentHost.Tests.";
        internal static string StateDirectory(string installation) => Path.Combine(installation, "HostState");

        [STAThread]
        private static int Main(string[] args)
        {
            var installation = AppContext.BaseDirectory;
            if (!new DirectoryInfo(installation).Name.StartsWith(DirectoryPrefix, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(installation, "test-owned.marker")))
                return 2;
            var state = StateDirectory(installation);
            if (args is ["--test-launch"])
                return LaunchAsync(installation, state).GetAwaiter().GetResult();
            if (!AgentHostArguments.TryParse(args, out var host))
                return 2;
            HeadlessStorage.Configure(Path.Combine(state, "Application"));
            var policy = new AgentAccessPolicyService(new(Path.Combine(state, "policy.json")));
            try
            {
                return StatefulAgentHost.Run(host, dispatcher => new(
                    dispatcher, host.Mode, new CaptureAppModel(dispatcher), policy,
                    initializeDatabase: InitializeDatabase,
                    initializeMethods: (_, _) => { },
                    loadPreferences: _ => { },
                    analyzers: Array.Empty<ICaptureAnalyzer>(), shutdownMethods: () => { },
                    instances: new(Environment.ProcessPath, Path.Combine(state, "instances")),
                    instanceId: host.InstanceId), async runtime =>
                    {
                        var dispatcherThread = Environment.CurrentManagedThreadId;
                        var callback = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await Task.Run(() => PublicUtilities.SafeInvoke(() => callback.SetResult(Environment.CurrentManagedThreadId)));
                        var publishedThread = await callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        File.WriteAllText(Path.Combine(state, "observed.json"), JsonSerializer.Serialize(new
                        {
                            hasApplication = Application.Current != null,
                            dispatcherThread,
                            publishedThread,
                            sta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
                            proxyInitialized = WebServiceProxy.IsInitialized,
                            devices = runtime.Model.CaptureDeviceContexts.Count,
                            windows = CountWindows(),
                            webViewLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                                assembly.GetName().Name.Contains("WebView", StringComparison.OrdinalIgnoreCase))
                        }));
                    });
            }
            finally { policy.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        private static int CountWindows()
        {
            var count = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var process);
                if (process == Environment.ProcessId)
                    count++;
                return true;
            }, IntPtr.Zero);
            return count;
        }

        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

        private static void InitializeDatabase(CaptureAppModel model)
        {
            var database = new SqLtDatabase();
            database.Initialize();
            model.ActiveDatabase = database;
            model.CaptureDevicesTable = CaptureDatabaseSchema.CreateDevices(database);
            WebServiceCaptureMethod.CreateDataTable(database);
            NetworkTraceCaptureMethod.CreateDataTable(database);
        }

        private static async Task<int> LaunchAsync(string installation, string state)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var report = await CliAgentCommand.RunAsync(new("start"), Guid.NewGuid().ToString("N"), timeout.Token,
                installation, new(Path.Combine(state, "policy.json")),
                new(Path.Combine(installation, "XboxMultiplayerAnalysisTool.exe"), Path.Combine(state, "instances")));
            CliApplication.Write(new(Console.OpenStandardOutput(), Console.Error), report);
            return report.ExitCode;
        }
    }
}
