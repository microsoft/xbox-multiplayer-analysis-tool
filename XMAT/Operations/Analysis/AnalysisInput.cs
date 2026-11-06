// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using XMAT.SharedInterfaces;

namespace XMAT.Operations.Analysis
{
    internal enum AnalysisInputKind
    {
        Saz,
        XmatCapture,
        Records,
    }

    internal sealed class AnalysisInput
    {
        private AnalysisInput(
            string path,
            IEnumerable<IDataRecord> records,
            string deviceName)
        {
            Path = path;
            Records = records;
            DeviceName = deviceName;
        }

        internal static AnalysisInput FromFile(string path)
        {
            return new AnalysisInput(path, null, null);
        }

        internal static AnalysisInput FromRecords(
            IEnumerable<IDataRecord> records,
            string deviceName)
        {
            ArgumentNullException.ThrowIfNull(records);
            return new AnalysisInput(
                null,
                records,
                deviceName);
        }

        internal string Path { get; }
        internal bool IsRecords => Records != null;
        internal IEnumerable<IDataRecord> Records { get; }
        internal string DeviceName { get; }
    }
}
