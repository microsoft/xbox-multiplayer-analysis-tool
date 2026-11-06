// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.Cli;
using XMAT.Operations.Capabilities;

namespace XMAT.Tests.HeadlessParity
{
    // Deleting captured sessions is the one reviewed exception to headless parity (P2-12): a confirmed, irreversible GUI
    // action with its slice, owner and reason, and no command line or operation. The rules accept no other policy
    // exception, and GuiOnly stays for presentation.
    public class DeleteCapabilityTests
    {
        private const string ProxyCommands = "XMAT.WebServiceCapture.WebServiceProxyCommands.";

        [Fact]
        public void DeleteSessions_IsTheOnlyPolicyException_AndHasNoCommandLine()
        {
            Capability delete = Assert.Single(
                CapabilityRegistry.All, capability => capability.Status == CapabilityStatus.PolicyException);

            Assert.Equal(("capture.delete-sessions", "S11", "XMAT"), (delete.Id, delete.Slice, delete.Owner));
            Assert.StartsWith("Policy exception:", delete.Reason, StringComparison.Ordinal);
            Assert.True(delete.CliRoute == null && delete.Operation == null);
            Assert.Equal(
                new[] { "DeleteSelectedSessions", "DeleteUnselectedSessions" }.Select(name => CapabilityKeys.Command(ProxyCommands + name)),
                delete.GuiEntryPoints);
            Assert.DoesNotContain(delete, CliCatalog.Commands().SelectMany(command => CliCatalog.CapabilitiesOf(command)));
            Assert.StartsWith("GUI only by policy", CapabilityTable.Status(delete), StringComparison.Ordinal);
            var doctor = new CliDoctorReport(CliCommands.Analyze) { Capabilities = new[] { delete } };
            using JsonDocument json = JsonDocument.Parse(CliJson.Serialize(new CliReport(CliCommands.Doctor, "run") { Doctor = doctor }));
            Assert.Equal("policyException", json.RootElement.GetProperty("capabilities")[0].GetProperty("status").GetString());
        }

        [Fact]
        public void PolicyExceptionRules_AcceptOnlyTheReviewedEntry_AndKeepGuiOnlyForPresentation()
        {
            Capability valid = Capability.PolicyException(
                "capture.delete-sessions", "Delete sessions", "S11", "XMAT", "Policy exception: reviewed.",
                new[] { CapabilityKeys.Command(ProxyCommands + "DeleteSelectedSessions") });

            Assert.Empty(Problems(valid));
            Assert.Contains("only capture.delete-sessions", Problems(valid with { Id = "capture.purge-sessions" }));
            Assert.Contains("needs an owner", Problems(valid with { Owner = null }));
            Assert.Contains("starts with 'Policy exception:'", Problems(valid with { Reason = "Presentation: hides rows." }));
            Assert.Contains("needs the delivery slice", Problems(valid with { Slice = null }));
            Assert.Contains("lists its GUI entry points", Problems(valid with { GuiEntryPoints = Array.Empty<string>() }));
            Assert.Contains("no command line or operation", Problems(valid with { CliRoute = "delete INPUT" }));
            Assert.Contains("no command line or operation", Problems(valid with { AdditionalCliRoutes = new[] { "delete INPUT" } }));
            Assert.Contains(
                "no command line or operation",
                Problems(valid with { Operation = "XMAT.Operations.Export.ExportCaptureSessionsOperation" }));
            Assert.Contains(
                "a GuiOnly reason starts with 'Presentation:'",
                Problems(Capability.GuiOnly("view.delete-sessions", "Delete sessions", "Policy exception: reviewed.")));
        }

        private static string Problems(Capability capability) =>
            string.Join(Environment.NewLine, CapabilityRegistryRules.Validate(new[] { capability }, typeof(PublicUtilities).Assembly));
    }
}
