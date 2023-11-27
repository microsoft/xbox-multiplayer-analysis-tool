// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Drawing;

namespace XMAT.SharedInterfaces
{
    public enum DeviceType : int
    {
        LocalPC = 0,
        XboxConsole = 1,
        Unknown = 99
    }

    public enum CaptureType : int
    {
        WebProxy = 0,
        NetworkTrace = 1
    }

    public interface ICaptureDeviceContext
    {
        DeviceType DeviceType { get; }

        CaptureType CaptureType { get; }

        string DeviceName { get; }

        bool IsSelected { get; set; }

        bool IsReadOnly { get; set; }

        Color DeviceColor { get; set; }

        IDeviceCaptureController CaptureController { get; set; }
    }
}
