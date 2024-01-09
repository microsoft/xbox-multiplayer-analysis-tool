// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;

namespace XMAT.WebServiceCapture.Proxy
{
    internal class ClientState
    {
        public ClientState(int id, TcpClient client)
        {
            ID = id;
            TcpClient = client;
            RequestHistory = new List<ClientRequest>();
            ResponseHistory = new List<ServerResponse>();
            SslStream = null;
        }

        public int ID { get; set; }
        public TcpClient TcpClient { get; set; }
        public SslStream SslStream { get; set; }
        public List<ClientRequest> RequestHistory { get; set; }
        public List<ServerResponse> ResponseHistory { get; set; }

        public Stream GetStream()
        {
            if(SslStream != null)
                return SslStream;
            else if(TcpClient?.GetStream() != null)
                return TcpClient.GetStream();
            else
                return null;
        }
    }
}
