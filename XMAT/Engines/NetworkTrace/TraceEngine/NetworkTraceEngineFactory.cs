// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Principal;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.NetworkTrace.NTDE
{
    internal interface INetworkTraceEngineFactory
    {
        CaptureOperationFailure GetPrerequisite(DeviceType deviceType);
        Task<INetworkTraceEngine> CreateAsync(string deviceName, DeviceType deviceType);
        Task DisconnectAsync(INetworkTraceEngine engine, DeviceType deviceType);
    }

    internal sealed class NetworkTraceEngineFactory : INetworkTraceEngineFactory
    {
        public CaptureOperationFailure GetPrerequisite(DeviceType deviceType) => deviceType switch
        {
            DeviceType.LocalPC => IsElevated() ? CaptureOperationFailure.None : CaptureOperationFailure.ElevationRequired,
            DeviceType.XboxConsole => CaptureOperationFailure.None,
            _ => CaptureOperationFailure.UnsupportedCapture
        };

        public Task<INetworkTraceEngine> CreateAsync(string deviceName, DeviceType deviceType) => deviceType == DeviceType.LocalPC
            ? Task.FromResult(NetworkTraceEngine.CreateLocal())
            : NetworkTraceEngine.Connect(new NetworkTraceEngineOptions { HostName = deviceName });

        public Task DisconnectAsync(INetworkTraceEngine engine, DeviceType deviceType) => deviceType == DeviceType.XboxConsole
            ? NetworkTraceEngine.Disconnect(engine) : Task.CompletedTask;

        internal static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
