// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed partial class AgentDataApplication
    {
        internal async Task<JsonElement> PrepareNavigationAsync(
            AgentSession session, Guid resultId, CaptureDetailKind kind, long itemId, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var navigation = new AgentPermission(AgentCapability.Navigation, AgentScope.Application);
            Runtime.Demand(session, navigation);
            var entry = GetEntry(session, resultId);
            using var lease = Use(entry, false);
            if (NavigationModel == null || kind is not (CaptureDetailKind.Record or CaptureDetailKind.Report))
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            if (kind == CaptureDetailKind.Report && itemId != 0)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            await entry.NavigationGate.WaitAsync(cancellationToken);
            try
            {
                var key = new CaptureDetailTarget(kind, kind == CaptureDetailKind.Report ? 0 : itemId);
                DesktopNavigationTarget target;
                lock (_gate)
                {
                    if (entry.NavigationTargets.TryGetValue(key, out target))
                        return AgentJson.Encode(target);
                    if (entry.NavigationTargets.Count >= Runtime.GetSession(session).Limits.ResponseRows)
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                }
                bool IsOwned()
                {
                    lock (_gate)
                    {
                        if (!_results.TryGetValue(resultId, out var found) || !ReferenceEquals(entry, found) || entry.Releasing)
                            return false;
                    }
                    try
                    { Runtime.Demand(session, Summary(entry.SourceId), navigation); return true; }
                    catch (AgentAccessException) { return false; }
                }
                try
                {
                    target = await NavigationModel.RegisterInvestigationNavigationAsync(entry.Result, kind, itemId, IsOwned, session.Id, cancellationToken);
                }
                catch (CaptureDataException exception) { throw DetailFailure(exception); }
                catch (AdministrationException) { throw new AgentAccessException(AgentAccessError.OperationUnavailable); }
                lock (_gate)
                {
                    entry.NavigationTargets.Add(key, target);
                }
                Runtime.Demand(session, Summary(entry.SourceId), navigation);
                DemandAccepting();
                return AgentJson.Encode(target);
            }
            finally { entry.NavigationGate.Release(); }
        }
    }
}
