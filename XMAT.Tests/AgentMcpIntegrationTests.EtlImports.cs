// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void EtlImportUsesExactGrantsApprovalAndTheSharedCompleteOfflineWorkflow()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.etl");
                File.WriteAllText(path, "reviewed ETL");
                var reads = 0;
                var imports = new CaptureEtlImports(model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)model.CaptureQueries, model.CaptureFileInputs, (_, append, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        reads++;
                        append(NetworkTraceTestData.Event(1));
                        return Task.CompletedTask;
                    });
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher, etlImports: imports);
                McpClient client = null;
                try
                {
                    var input = Complete(host.RegisterInputFileAsync(path));
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var import = new AgentPermission(AgentCapability.CaptureImport, AgentScope.Source(source.Id));
                    var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                    var request = new Dictionary<string, object>
                    {
                        ["inputId"] = input.Id,
                        ["sourceId"] = source.Id,
                        ["idempotencyKey"] = "etl-once"
                    };
                    Finish(host.Runtime.SetPermissionsAsync(session, [file]));
                    Assert.Equal("PermissionDenied", Payload(Complete(client.CallToolAsync("xmat_import_etl", request).AsTask()))
                        .GetProperty("error").GetString());
                    Assert.Equal(0, reads);
                    Finish(host.Runtime.SetPermissionsAsync(session, [file, import]));
                    var operation = Payload(Complete(client.CallToolAsync("xmat_import_etl", request).AsTask()))
                        .GetProperty("id").GetGuid();
                    Assert.Contains(input.Sha256, host.Runtime.GetApprovalDetails(operation));
                    Assert.Equal(0, reads);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(session, operation).State);
                    Assert.Equal(1, host.Runtime.GetResult(session, operation).GetProperty("recordCount").GetInt32());
                    Assert.Null(model.SelectedDeviceContext);
                    Assert.False(model.CaptureDeviceContexts.Single().CaptureController.IsRunning);
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_import_etl", request).AsTask()))
                        .GetProperty("id").GetGuid());
                    Assert.Equal(1, reads);
                    Finish(host.RevokeAsync(session.Id));
                    Assert.Single(model.CaptureDeviceContexts);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                    Finish(imports.DisposeAsync().AsTask());
                }
            });
        }
    }
}
