// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using XMAT.SharedInterfaces;

namespace XMAT.NetworkTraceCaptureAnalysis.Models
{
    class NetworkTraceAnalysisOptionsModel : ICaptureAnalyzerPreferences
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

        public int PacketsPerSecondThreshold { get; set; }
        public int MaximumMTU { get; set; }
        public int DuplicatePacketWindow { get; set; }

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
