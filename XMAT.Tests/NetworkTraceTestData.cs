// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.Text.Json;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.NTDE;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    internal sealed class FakeTraceEngine : INetworkTraceEngine
    {
        private EventHandler<string> _events;
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        internal int DownloadCount { get; private set; }
        internal int Subscribers { get; private set; }
        internal Func<Task> Start { get; set; } = () => Task.CompletedTask;
        internal Func<Task> Stop { get; set; } = () => Task.CompletedTask;
        internal Func<FakeTraceEngine, Task> Download { get; set; } = engine =>
        {
            engine.Emit(NetworkTraceTestData.Event(1));
            engine.Emit(NetworkTraceTestData.Event(2));
            return Task.CompletedTask;
        };
        public event EventHandler<string> EventRecordAvailable
        {
            add { _events += value; Subscribers++; }
            remove { _events -= value; Subscribers--; }
        }
        public Task StartPacketTraceAsync() { StartCount++; return Start(); }
        public Task StopPacketTraceAsync() { StopCount++; return Stop(); }
        public Task GetAllEventsAsync() { DownloadCount++; return Download(this); }
        internal void Emit(string record) => _events?.Invoke(this, record);
    }

    internal sealed class FakeTraceEngineFactory : INetworkTraceEngineFactory
    {
        internal CaptureOperationFailure Prerequisite { get; set; }
        internal int ConnectCount { get; private set; }
        internal int DisconnectCount { get; private set; }
        internal Queue<FakeTraceEngine> Engines { get; } = new();
        internal Func<Task> BeforeConnect { get; set; } = () => Task.CompletedTask;
        internal Func<Task> BeforeDisconnect { get; set; } = () => Task.CompletedTask;
        public CaptureOperationFailure GetPrerequisite(DeviceType deviceType) => Prerequisite;
        public async Task<INetworkTraceEngine> CreateAsync(string deviceName, DeviceType deviceType)
        {
            ConnectCount++;
            await BeforeConnect();
            return Engines.Dequeue();
        }
        public async Task DisconnectAsync(INetworkTraceEngine engine, DeviceType deviceType)
        {
            DisconnectCount++;
            await BeforeDisconnect();
        }
    }

    internal sealed class TraceCaptureContext : ICaptureDeviceContext
    {
        internal TraceCaptureContext(
            FakeTraceEngineFactory factory, string name = "test-device", DeviceType deviceType = DeviceType.LocalPC)
        {
            DeviceName = name;
            DeviceType = deviceType;
            Controller = new NetworkTraceCaptureController(name, deviceType, factory);
        }
        internal NetworkTraceCaptureController Controller { get; }
        public DeviceType DeviceType { get; }
        public CaptureType CaptureType => CaptureType.NetworkTrace;
        public string DeviceName { get; }
        public bool IsReadOnly { get; set; }
        public bool IsSelected { get; set; }
        public Color DeviceColor { get; set; }
        public IDeviceCaptureController CaptureController => Controller;
    }

    internal static class NetworkTraceTestData
    {
        internal static string Event(int marker)
        {
            var bytes = new byte[40];
            bytes[14] = 0x45;
            bytes[17] = 26;
            bytes[23] = 17;
            bytes[26] = bytes[30] = 10;
            bytes[29] = 1;
            bytes[33] = 2;
            bytes[34] = (byte)marker;
            return JsonSerializer.Serialize(new
            {
                @event = new { processId = 1, threadId = 2, timestamp = new DateTime(2026, 10, 30, 12, 0, marker, DateTimeKind.Utc) },
                packet = new { mediaType = "ethernet", flags = new[] { "start", "end", "send" }, data = Convert.ToBase64String(bytes) }
            });
        }
    }
}
