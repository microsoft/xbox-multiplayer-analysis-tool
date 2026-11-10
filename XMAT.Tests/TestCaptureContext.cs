// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    // A data-only web proxy capture tab for tests: the given controller, name, device type and initial read-only state.
    internal sealed class TestCaptureContext(
        IDeviceCaptureController controller,
        string name,
        bool readOnly,
        DeviceType deviceType) : ICaptureDeviceContext
    {
        public DeviceType DeviceType { get; } = deviceType;
        public CaptureType CaptureType => CaptureType.WebProxy;
        public string DeviceName { get; } = name;
        public bool IsSelected { get; set; }
        public bool IsReadOnly { get; set; } = readOnly;
        public System.Drawing.Color DeviceColor { get; set; }
        public IDeviceCaptureController CaptureController { get; } = controller;
    }
}
