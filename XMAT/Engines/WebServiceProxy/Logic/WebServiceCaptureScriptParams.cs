// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT
{
    public enum WebServiceCaptureScriptableEventType
    {
        [Display(Name="WEB_SVC_EVENT_TYPE_SSLCONN")]
        [Description("WEB_SVC_EVENT_TYPE_SSLCONN_DESC")]
        [DefaultValue("WEB_SVC_EVENT_TYPE_SSLCONN_SCRIPT")]
        SslConnectionRequest,

        [Display(Name="WEB_SVC_EVENT_TYPE_WEBREQ")]
        [Description("WEB_SVC_EVENT_TYPE_WEBREQ_DESC")]
        [DefaultValue("WEB_SVC_EVENT_TYPE_WEBREQ_SCRIPT")]
        WebRequest,

        [Display(Name="WEB_SVC_EVENT_TYPE_WEBRESP")]
        [Description("WEB_SVC_EVENT_TYPE_WEBRESP_DESC")]
        [DefaultValue("WEB_SVC_EVENT_TYPE_WEBRESP_SCRIPT")]
        WebResponse,
    }

    [Display(Name="Params")]
    [Description("WEB_SVC_SCRIPT_PROP_DESC_PARAMS")]
    public class WebServiceCaptureScriptParams
    {
        public WebServiceCaptureScriptParams()
        {
            Continue = true;
        }

        [Description("WEB_SVC_SCRIPT_PROP_DESC_REQUEST")]
        public ClientRequest Request  { get; set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_RESPONSE")]
        public ServerResponse Response { get; set; }
        [Description("WEB_SVC_SCRIPT_PROP_DESC_CONTINUE")]
        public bool Continue { get; set; }
    }
}
