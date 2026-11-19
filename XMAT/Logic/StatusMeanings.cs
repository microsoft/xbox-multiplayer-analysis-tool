// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using XMAT.NetworkTrace;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT
{
    // What a state means to the person looking at it. The meaning, not the state, sets how XMAT shows it, the same way
    // in every view: a problem in red with a cross, something that needs attention in amber with a warning sign, a
    // running capture with the recording dot, work under way in words without color, and the routine without color
    // (docs/design-system.md).
    public enum StatusMeaning
    {
        Routine,
        Working,
        Live,
        Attention,
        Problem,
    }

    // Where a captured response is: still arriving, as a streamed response is until it ends, or complete.
    public enum ResponseProgress
    {
        Complete,
        Streaming,
    }

    // The meaning of every state XMAT shows. A view takes a state's meaning from here rather than choosing a color, and
    // a state added without a meaning throws, so the design system tests catch it (STATUS-1).
    internal static class StatusMeanings
    {
        // An HTTP response's status class, as a table shows it. A request without a status is routine: until the proxy
        // records the requests that fail, it can't be told from one that is still waiting.
        internal static StatusMeaning Of(TrafficStatusClass status) => status switch
        {
            TrafficStatusClass.Success or TrafficStatusClass.Redirect or TrafficStatusClass.None => StatusMeaning.Routine,
            TrafficStatusClass.ClientError => StatusMeaning.Attention,
            TrafficStatusClass.ServerError => StatusMeaning.Problem,
            _ => throw Unmapped(status),
        };

        // An HTTP status as the capture records it, such as "429".
        internal static StatusMeaning OfHttpStatus(string status) => Of(TrafficSummary.ClassOf(status));

        // A captured response's progress. A response still streaming is work under way, whatever its status: its status
        // cell says Streaming until it ends, and then shows its code.
        internal static StatusMeaning Of(ResponseProgress progress) => progress switch
        {
            ResponseProgress.Complete => StatusMeaning.Routine,
            ResponseProgress.Streaming => StatusMeaning.Working,
            _ => throw Unmapped(progress),
        };

        // A network trace's state.
        internal static StatusMeaning Of(DeviceStatusEnum state) => state switch
        {
            DeviceStatusEnum.Idle => StatusMeaning.Routine,
            DeviceStatusEnum.Tracing => StatusMeaning.Live,
            DeviceStatusEnum.Connecting or DeviceStatusEnum.Starting or DeviceStatusEnum.Stopping or
                DeviceStatusEnum.Downloading or DeviceStatusEnum.Disconnecting => StatusMeaning.Working,
            _ => throw Unmapped(state),
        };

        // A device's proxy setting, as XMAT reads it.
        internal static StatusMeaning Of(ProxyConfigurationState state) => state switch
        {
            ProxyConfigurationState.Enabled or ProxyConfigurationState.Disabled => StatusMeaning.Routine,
            ProxyConfigurationState.Unknown => StatusMeaning.Attention,
            ProxyConfigurationState.Unreachable => StatusMeaning.Problem,
            _ => throw Unmapped(state),
        };

        // XMAT's web proxy, which runs while a web proxy capture does.
        internal static StatusMeaning Of(ProxyState state) => state switch
        {
            ProxyState.Stopped => StatusMeaning.Routine,
            ProxyState.Starting or ProxyState.Stopping => StatusMeaning.Working,
            ProxyState.Running => StatusMeaning.Live,
            ProxyState.Faulted => StatusMeaning.Problem,
            _ => throw Unmapped(state),
        };

        // The fill a status class takes in charts. There, color is the data, so every class has its own (STATUS-3).
        internal static string ChartFill(TrafficStatusClass status) => status switch
        {
            TrafficStatusClass.Success => "SuccessFillBrush",
            TrafficStatusClass.Redirect => "InfoFillBrush",
            TrafficStatusClass.ClientError => "WarningFillBrush",
            TrafficStatusClass.ServerError => "ErrorFillBrush",
            TrafficStatusClass.None => "NeutralFillBrush",
            _ => throw Unmapped(status),
        };

        // The color of a label on a status class's fill in a chart.
        internal static string ChartLabel(TrafficStatusClass status) =>
            ChartFill(status).Replace("FillBrush", "OnFillBrush", StringComparison.Ordinal);

        private static ArgumentOutOfRangeException Unmapped(Enum state) =>
            new(nameof(state), state,
                $"STATUS-1: {state.GetType().Name}.{state} has no meaning. Give it one in StatusMeanings.");
    }
}
