// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.NetworkTraceCaptureAnalysis.Models
{
    class NetworkTraceAnalysisOptionsModel : PreferenceObservable, ICaptureAnalyzerPreferences
    {
        private readonly int DefaultPacketsPerSecondThreshold = 20;
        private readonly int DefaultXboxMaximumMTU = 1384;
        private readonly int DefaultDuplicatePacketWindowMS = 1000;

        internal NetworkTraceAnalysisOptionsModel()
        {
            PacketsPerSecondThreshold = DefaultPacketsPerSecondThreshold;
            MaximumMTU = DefaultXboxMaximumMTU;
            DuplicatePacketWindow = DefaultDuplicatePacketWindowMS;
        }

        private int _packetsPerSecondThreshold;
        private int _maximumMtu;
        private int _duplicatePacketWindow;

        public int PacketsPerSecondThreshold
        {
            get => _packetsPerSecondThreshold;
            set => SetValue(ref _packetsPerSecondThreshold, value);
        }
        public int MaximumMTU { get => _maximumMtu; set => SetValue(ref _maximumMtu, value); }
        public int DuplicatePacketWindow
        {
            get => _duplicatePacketWindow;
            set => SetValue(ref _duplicatePacketWindow, value);
        }

        public void DeserializeFrom(JsonElement serializedObject)
        {
            JsonElement prop;

            if (serializedObject.TryGetProperty(nameof(PacketsPerSecondThreshold), out prop))
            {
                PacketsPerSecondThreshold = prop.GetInt32();
            }

            if (serializedObject.TryGetProperty(nameof(MaximumMTU), out prop))
            {
                MaximumMTU = prop.GetInt32();
            }

            if (serializedObject.TryGetProperty(nameof(DuplicatePacketWindow), out prop))
            {
                DuplicatePacketWindow = prop.GetInt32();
            }
        }

        public void Serialized()
        {
        }
    }
}
