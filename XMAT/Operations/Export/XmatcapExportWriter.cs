// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using XMAT.DataAbstractionLayer;

namespace XMAT.Operations.Export
{
    internal sealed class XmatcapExportWriter : ICaptureExportWriter
    {
        internal XmatcapExportWriter(
            string path,
            IReadOnlyList<SqLtSchemaColumn> captureFields,
            IReadOnlyList<SqLtSchemaColumn> deviceFields,
            IReadOnlyDictionary<string, RawExportRow> deviceDescriptors)
        {
            _ = path;
            _ = captureFields;
            _ = deviceFields;
            _ = deviceDescriptors;
        }

        public void Write(RawExportRow row, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public ExportWriteSummary Complete(CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public void Dispose()
        {
        }
    }
}
