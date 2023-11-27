// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Windows.Data;
using XMAT.NetworkTrace.Models;

namespace XMAT.NetworkTrace
{
    internal class DeviceStatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return string.Empty;
            }

            string converted = " ";

            switch((DeviceStatusEnum) value)
            {
                case DeviceStatusEnum.Connecting: converted += Localization.GetLocalizedString("NETCAP_STATE_CONNECTING"); break;
                case DeviceStatusEnum.Disconnecting: converted += Localization.GetLocalizedString("NETCAP_STATE_DISCONNECTING"); break;
                case DeviceStatusEnum.Downloading: converted += Localization.GetLocalizedString("NETCAP_STATE_DOWNLOADING"); break;
                case DeviceStatusEnum.Idle: converted += Localization.GetLocalizedString("NETCAP_STATE_IDLE"); break;
                case DeviceStatusEnum.Starting: converted += Localization.GetLocalizedString("NETCAP_STATE_STARTING"); break;
                case DeviceStatusEnum.Stopping: converted += Localization.GetLocalizedString("NETCAP_STATE_STOPPING"); break;
                case DeviceStatusEnum.Tracing: converted += Localization.GetLocalizedString("NETCAP_STATE_TRACING"); break;
            }

            return converted;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("PacketFlagsConverter.ConvertBack() not implemented");
        }
    }
}
