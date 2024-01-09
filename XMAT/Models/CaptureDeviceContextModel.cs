// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Drawing;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.NetworkTrace;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace XMAT.Models
{
    internal class CaptureDeviceContextModel : ICaptureDeviceContext, INotifyPropertyChanged
    {
        private DeviceType _deviceType;
        private CaptureType _captureType;
        private string _deviceName;
        private bool _isEnabled;
        private bool _isReadOnly;
        private Color _deviceColor;
        private IDeviceCaptureController _captureController;

        public DeviceType DeviceType { get => _deviceType; internal set { _deviceType = value; RaisePropertyChange(); } }

        public CaptureType CaptureType { get => _captureType; internal set { _captureType = value; RaisePropertyChange(); } }

        public string DeviceName { get => _deviceName; internal set { _deviceName = value; RaisePropertyChange(); } }

        public bool IsSelected { get => _isEnabled; set { _isEnabled = value; RaisePropertyChange(); } }

        public bool IsReadOnly { get => _isReadOnly; set { _isReadOnly = value; RaisePropertyChange(); } }

        public Color DeviceColor { get => _deviceColor; set { _deviceColor = value; RaisePropertyChange(); } }

        public IDeviceCaptureController CaptureController { get => _captureController; set { _captureController = value; RaisePropertyChange(); } }

        public string CaptureTypeString
        {
            get
            {
                switch(CaptureType)
                {
                    case CaptureType.WebProxy:
                        return Localization.GetLocalizedString("WEBCAP_CAPTURE_TYPE_PROXY");
                    case CaptureType.NetworkTrace:
                        return Localization.GetLocalizedString("WEBCAP_CAPTURE_TYPE_TRACE");
                    default:
                        return Localization.GetLocalizedString("WEBCAP_CAPTURE_TYPE_UNKNOWN");
                }
            }

        }
        internal CaptureDeviceContextModel(
            DeviceType deviceType,
            string deviceName,
            CaptureType captureType,
            bool readOnly,
            object serializedParameters)
        {
            DeviceType = deviceType;
            DeviceName = deviceName;
            CaptureType = captureType;
            IsReadOnly = readOnly;
            IsSelected = !readOnly;

            switch (captureType)
            {
                case CaptureType.WebProxy:
                    CaptureController = new WebServiceDeviceCaptureController(deviceName, deviceType, readOnly);
                    break;
                case CaptureType.NetworkTrace:
                    CaptureController = new NetworkTraceCaptureController(deviceName, deviceType);
                    break;
                default:
                    throw new NotImplementedException($"Unknown capture type: {captureType}");

            }

            if (serializedParameters != null)
            {
                (CaptureController.Parameters as ICaptureMethodParameters)
                    .DeserializeFrom((JsonElement)serializedParameters);
            }

            CaptureController.Initialize();
            AssignDeviceColor();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void AssignDeviceColor()
        {
            int nameHash = DeviceName.GetHashCode();
            int r = ((((nameHash) & 0xFF) + 0xFF) >> 1) & 0xFF;
            int g = ((((nameHash >> 8) & 0xFF) + 0xFF) >> 1) & 0xFF;
            int b = ((((nameHash >> 16) & 0xFF) + 0xFF) >> 1) & 0xFF;
            DeviceColor = Color.FromArgb(0xFF, r, g, b);
        }
    }
}
