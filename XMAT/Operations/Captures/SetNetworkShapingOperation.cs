// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Operations.Captures
{
    internal sealed record SetNetworkShapingRequest(
        NetworkShapingController Controller,
        NetworkShapingProfile Profile,
        int BodyTimeoutSeconds = 120);

    internal sealed class SetNetworkShapingOperation :
        IOperation<SetNetworkShapingRequest, NetworkShapingProfile>
    {
        public Task<OperationResult<NetworkShapingProfile>> ExecuteAsync(
            SetNetworkShapingRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(request.Controller);
                new NetworkShapingPlan(1, request.Profile, BodyTimeoutSeconds: request.BodyTimeoutSeconds).Validate();
                bool notified = request.Controller.Apply(request.Profile);
                var diagnostics = new List<OperationDiagnostic>();
                if (!notified)
                {
                    diagnostics.Add(new OperationDiagnostic(
                        "capture.shaping.notification-failed",
                        OperationDiagnosticSeverity.Warning,
                        "The profile is active, but a connection abort or capture-history notification failed."));
                }
                if (request.Profile.Mode == NetworkShapingMode.Shape &&
                    request.Profile.UpKbps > 0 &&
                    ProxyRequestReadPolicy.Default.MaxBodyBytes * 8.0 / (request.Profile.UpKbps * 1000.0) >
                        request.BodyTimeoutSeconds)
                {
                    diagnostics.Add(new OperationDiagnostic(
                        "capture.shaping.body-deadline",
                        OperationDiagnosticSeverity.Warning,
                        "Large uploads at this rate can exceed the configured total-body deadline. Other connections share the bandwidth."));
                }
                return Task.FromResult(OperationResult<NetworkShapingProfile>.Success(request.Profile, diagnostics));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Task.FromResult(OperationResult<NetworkShapingProfile>.Failure(
                    OperationOutcome.InvalidInput,
                    new OperationDiagnostic(
                        "capture.shaping.invalid",
                        OperationDiagnosticSeverity.Error,
                        "The shaping settings are invalid or the capture's shaping history is full.")));
            }
        }
    }
}
