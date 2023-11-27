// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XMAT.WebServiceCapture.Proxy
{
    public interface ICertificateManager
    {
        void ExportRootCertificate(string path);
        void RemoveRootCertificate();
        void RemoveHostCertificates();
    }
}
