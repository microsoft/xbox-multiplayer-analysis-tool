// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.WebServiceCapture.Proxy
{
    public interface ICertificateManager
    {
        void ExportRootCertificate(string path);
        void RemoveRootCertificate();
        void RemoveHostCertificates();
    }
}
