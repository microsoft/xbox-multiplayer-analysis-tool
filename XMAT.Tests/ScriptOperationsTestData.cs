// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    internal sealed class ScriptCaptureContext : ICaptureDeviceContext
    {
        internal ScriptCaptureContext(string name = "script capture", bool readOnly = false)
        {
            DeviceName = name;
            IsReadOnly = readOnly;
            Controller = new WebServiceDeviceCaptureController(name, DeviceType.GenericProxyDevice, readOnly, new ProxyPortPool());
        }

        internal WebServiceDeviceCaptureController Controller { get; }
        public DeviceType DeviceType => DeviceType.GenericProxyDevice;
        public CaptureType CaptureType => CaptureType.WebProxy;
        public string DeviceName { get; }
        public bool IsReadOnly { get; set; }
        public bool IsSelected { get; set; }
        public Color DeviceColor { get; set; }
        public IDeviceCaptureController CaptureController => Controller;
    }
}
