// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace XMAT.WebServiceCapture.Proxy
{
    public abstract class BaseRequestResponse
    {
        internal StringBuilder _firstLineAndHeaders = new();

        [Description("WEB_SVC_SCRIPT_PROP_DESC_REQNUM")]
        public int RequestNumber;
        [Description("WEB_SVC_SCRIPT_PROP_DESC_VERSION")]
        public string Version { get; set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_HEADERS")]
        public HeaderCollection Headers { get; internal set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_CONTENTHEADERS")]
        public HeaderCollection ContentHeaders { get; internal set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_FIRSTLINE")]
        public abstract string FirstLineAndHeaders { get; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_BODY")]
        public byte[] BodyBytes { get; set; }

        public BaseRequestResponse()
        {
            Headers = new HeaderCollection();
            ContentHeaders = new HeaderCollection();
            BodyBytes = Array.Empty<byte>();
        }

        internal string RefreshFirstLineAndHeaders(string firstLine)
        {
            _firstLineAndHeaders.Clear();
            _firstLineAndHeaders.Append($"{firstLine}\r\n");
            _firstLineAndHeaders.Append(Headers);
            _firstLineAndHeaders.Append(ContentHeaders);
            _firstLineAndHeaders.Append("\r\n");
            return _firstLineAndHeaders.ToString();
        }

        public override string ToString()
        {
            string final;

            final = FirstLineAndHeaders;
            if(BodyBytes != null && BodyBytes.Length > 0)
            {
                final += Encoding.ASCII.GetString(BodyBytes);
            }
            return final;
        }

        public byte[] ToByteArray()
        {
            byte[] headers = Encoding.ASCII.GetBytes(FirstLineAndHeaders);
            byte[] final = new byte[headers.Length + BodyBytes.Length];
            Array.Copy(headers, final, headers.Length);
            Array.Copy(BodyBytes, 0, final, headers.Length, BodyBytes.Length);
            return final;
        }
    }

    [Display(Name="Request")]
    public class ClientRequest : BaseRequestResponse
    {
        [Description("WEB_SVC_SCRIPT_PROP_DESC_SCHEME")]
        public string Scheme { get; set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_HOST")]
        public string Host { get; set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_PORT")]
        public int Port { get; set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_METHOD")]
        public string Method { get; set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_PATH")]
        public string Path { get; set; }
        public override string FirstLineAndHeaders { get => RefreshFirstLineAndHeaders($"{Method} {Path} {Version}"); }
    }

    [Display(Name="Response")]
    public class ServerResponse : BaseRequestResponse
    {
        [Description("WEB_SVC_SCRIPT_PROP_DESC_STATUS")]
        public string Status { get; set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_STATUSDESC")]
        public string StatusDescription { get; set; }
        public override string FirstLineAndHeaders { get => RefreshFirstLineAndHeaders($"{Version} {Status} {StatusDescription}"); }
    }
}
