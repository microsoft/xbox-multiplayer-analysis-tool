// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Models
{
    internal class PingableSourceAddressModel : INotifyPropertyChanged
    {
        public IPAddress SourceAddress { get; }
        public IPAddress DestinationAddress { get; }
        public string PingStatus
        {
            get => _pingStatus;
            set
            {
                _pingStatus = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PingStatus)));
            }
        }
        public bool CanPing
        {
            get => _canPing;
            set
            {
                _canPing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPing)));
            }
        }
        public bool IsSelected { get; set; }

        internal PingableSourceAddressModel(
            IPAddress sourceAddress,
            IPAddress destinationAddress,
            bool isSelected)
        {
            SourceAddress = sourceAddress;
            DestinationAddress = destinationAddress;
            _pingStatus = @"<unknown ping status>";
            _canPing = true;
            IsSelected = isSelected;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool _canPing;
        private string _pingStatus;
    }
}
