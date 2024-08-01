// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection.PortableExecutable;
using System.Text;

namespace XMAT.WebServiceCapture.Proxy
{
    public abstract class BaseWebSocketMessage
    {
        internal StringBuilder _message = new();

        [Description("WEB_SVC_SCRIPT_PROP_DESC_WS_REQNUM")]
        public int RequestNumber;
        [Description("WEB_SVC_SCRIPT_PROP_DESC_WS_BODY")]
        public byte[] BodyBytes { get; set; }

        public BaseWebSocketMessage()
        {
            BodyBytes = Array.Empty<byte>();
        }
        public override string ToString()
        {
            string final = "";

            if (BodyBytes != null && BodyBytes.Length > 0)
            {
                final = Encoding.ASCII.GetString(BodyBytes);
            }

            return final;
        }
        public byte[] ToByteArray()
        {
            byte[] final = new byte[BodyBytes.Length];
            Array.Copy(BodyBytes, 0, final, 0, BodyBytes.Length);
            return final;
        }
    }

    public class WebSocketMessage : BaseWebSocketMessage
    {
        [Description("WEB_SVC_SCRIPT_PROP_DESC_WS_FROM_HOST")]
        public bool FromHost { get; set; }
    }
}
