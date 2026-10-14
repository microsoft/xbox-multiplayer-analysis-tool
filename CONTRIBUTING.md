# Contributing

This project welcomes contributions and suggestions. Most contributions require you to
agree to a Contributor License Agreement (CLA) declaring that you have the right to,
and actually do, grant us the rights to use your contribution. For details, visit
https://cla.microsoft.com.

When you submit a pull request, a CLA-bot will automatically determine whether you need
to provide a CLA and decorate the PR appropriately (e.g., label, comment). Simply follow the
instructions provided by the bot. You will only need to do this once across all repositories using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/)
or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

# Continuous Integration & Testing

All contributions to this project must successfully pass all current build pipelines and tests.

Any new functionality added, must include appropriate testing alongside the new functionality.

## Telemetry development and release policy

Telemetry is XMAT-owned code in `XMAT\Logic\Telemetry`, informed by the DevTools telemetry design but with
no private repository, project, or package dependency. It uses public OpenTelemetry/OneCollector SDK APIs,
an explicit field contract, and an isolated logger rather than forwarding application logs.

Local, PR, and CI builds default to `EnableTelemetry=false`; a Release configuration alone does not enable
export. Focused tests use fake reporters and a loopback collector, never the production endpoint:

```powershell
dotnet test XMAT.Tests\XMAT.Tests.csproj -p:Platform=x64 --filter FullyQualifiedName~Telemetry
```

Before publishing an enabled build, the release owner must obtain a dedicated XMAT instrumentation key and
register the case-sensitive `Microsoft.Gaming.XMAT` namespace/event schemas. The owner must approve automatic
collection, the machine-ID/sandbox fields, retention/access rules, and any backend IP/identity enrichment.
This registration/approval is a separate owner task; checking a build flag is not a substitute for it.
Do not reuse the DevTools ingestion key, publish test traffic into production dashboards, or commit keys or
service credentials into source. Client ingestion keys are not authorization credentials.

For an approved **test data source only**, supply its key through your build environment:

```powershell
dotnet build XMAT\XboxMultiplayerAnalysisTool.csproj -c Release -p:Platform=x64 `
  -p:EnableTelemetry=true -p:TelemetryApproved=true -p:TelemetryBuildChannel=Test `
  "-p:TelemetryInstrumentationKey=$env:XMAT_TELEMETRY_KEY"
```

Approved publication uses channel `Public` or `Internal`. Enabled builds fail if approval, key, or channel
configuration is absent. Non-exporting artifacts do not contain the supplied ingestion key and do not read
telemetry registry metadata unless explicitly launched in local sample mode. There is no telemetry
preference UI or settings file.

The full schema and app sample-generation commands are in [TELEMETRY.md](TELEMETRY.md). Use
`--telemetry-sample` with a new absolute output filename in a non-exporting build to inspect real
application events without any collector key or network export. Local samples contain the permitted
machine ID and sandbox; sanitize them before sharing.

Every new event must use fixed enums/explicit attribute mappings. Never add arbitrary strings, command options,
exception objects, URLs, paths, traffic contents, or environment resource detectors. Preserve capture and
analysis behavior and verify both the typed contract and actual serialized collector payload.
`XMAT\Data\TelemetryUsage.kql` contains aggregate query templates against an owner-provided normalized
`XmatTelemetry` view; its column mapping and deployment require the destination owner's approval.

# Coding Standards

This project utilized the standard [C# coding conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions).

# Reviewing Changes

Changes by external contributors require review by 2 Microsoft employees to be merged.

Changes by internal contributors require review by 1 Microsoft employee (excluding author) to be merged.