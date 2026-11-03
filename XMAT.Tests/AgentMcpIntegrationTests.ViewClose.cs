// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void CaptureClearRequiresExactControlAndApprovalAndPreservesOtherRows()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var target = setup.Created.Single();
                CaptureInvestigationTestData.AddWeb(setup.Database, 1, device: target.DeviceName);
                CaptureInvestigationTestData.AddWeb(setup.Database, 2, device: "other-context");
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    var arguments = new Dictionary<string, object>
                    {
                        ["captureId"] = capture.Id,
                        ["expectedRevision"] = capture.Revision,
                        ["idempotencyKey"] = "clear-once"
                    };
                    Assert.Equal("PermissionDenied", Payload(Complete(client.CallToolAsync("xmat_clear_capture", arguments).AsTask()))
                        .GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))]));
                    var operation = Payload(Complete(client.CallToolAsync("xmat_clear_capture", arguments).AsTask()))
                        .GetProperty("id").GetGuid();
                    Assert.Equal(2, setup.Database.TableByName("WebProxyConnections").Dataset.DataRecords.Count());
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.True(host.Runtime.GetResult(session, operation).GetProperty("cleared").GetBoolean());
                    Assert.Equal("other-context", Assert.Single(
                        setup.Database.TableByName("WebProxyConnections").Dataset.DataRecords).Str("DeviceName"));
                    Assert.False(target.CaptureController.IsRunning);
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_clear_capture", arguments).AsTask()))
                        .GetProperty("id").GetGuid());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void ReadOnlyImportViewsCloseOnlyWithNavigationAndExactDestructiveApproval()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                CaptureSazImportTests.WriteSaz(path);
                var imported = Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(path));
                var capture = Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single();
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var arguments = new Dictionary<string, object>
                    {
                        ["captureId"] = capture.Id,
                        ["expectedRevision"] = capture.Revision,
                        ["idempotencyKey"] = "close-view"
                    };
                    Assert.Equal("PermissionDenied", Payload(Complete(client.CallToolAsync("xmat_remove_device", arguments).AsTask()))
                        .GetProperty("error").GetString());
                    var navigation = new AgentPermission(AgentCapability.Navigation, AgentScope.Application);
                    Finish(host.Runtime.SetPermissionsAsync(session, [navigation]));
                    var operation = Payload(Complete(client.CallToolAsync("xmat_remove_device", arguments).AsTask()))
                        .GetProperty("id").GetGuid();
                    Assert.Single(setup.Model.CaptureDeviceContexts);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.True(host.Runtime.GetResult(session, operation).GetProperty("removed").GetBoolean());
                    Assert.Empty(setup.Model.CaptureDeviceContexts);
                    Assert.Empty(setup.Database.TableByName("WebProxyConnections").Dataset.DataRecords);
                    Assert.Equal(0, setup.SaveCount);
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_remove_device", arguments).AsTask()))
                        .GetProperty("id").GetGuid());
                    Assert.True(File.Exists(path));
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                    Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask());
                }
            });
        }
    }
}
