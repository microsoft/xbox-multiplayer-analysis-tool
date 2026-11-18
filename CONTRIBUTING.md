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

# Headless Parity

Every capability that is not purely presentation must also work without the GUI, through the command-line tool
`xmat.exe` ([docs/cli.md](docs/cli.md)):

- Implement the capability once as a UI-free operation under `XMAT/Operations` (`IOperation<TRequest, TResult>`), and
  add its GUI adapter and its `xmat.exe` adapter in the same change. When that is not possible, the change needs a
  reviewed parity exception: classify the capability as `Planned` (with the slice and owner that will deliver it) and
  explain why in the pull request.
- Classify every new GUI entry point in `XMAT/Operations/Capabilities/CapabilityRegistry.cs`: routed commands, XAML
  `Command`, `Click` and `CommandBinding` entries, dialog windows, device and capture types, script events, analyzers
  and preferences. `GuiOnly` is only for presentation, such as layout, themes and navigation inside a view; the rare
  capability that stays in the GUI by decision is a `PolicyException` (see [Parity Exceptions](#parity-exceptions)).
- Operations never show dialogs or use `System.Windows.Application.Current`, a `Dispatcher`, `CommandManager` or
  `PublicUtilities.SafeInvoke`. They report through `OperationResult`, `OperationContext.Progress` and diagnostics,
  and the adapters decide how to present them. Code under `XMAT.Operations` and `XMAT.Cli` never declares WPF types
  (XAML code-behind or anything derived from `DispatcherObject`): the exemption that UI types get elsewhere does not
  apply there.
- Code under `XMAT.Operations` and `XMAT.Cli` also never reads the GUI's app model (`CaptureAppModel`) or saved
  settings (`CaptureAppSettings`, `AppPreferencesModel`), never uses the storage folders of `PublicUtilities` or its
  `AppLog`, and never looks up localized strings (`Localization`). It takes its options from its request, writes only
  under `OperationContext.WorkingDirectory` or paths that the request names, and reports stable codes with fixed English
  messages; the GUI adapters read the settings and localize what they show. The one reviewed exception is
  `xmat analyze` loading the English strings once, for the analysis rules that still look them up.
- The tests in `XMAT.Tests/HeadlessParity` enforce this. They fail when an entry point is not classified, when the
  registry lists one that no longer exists, when non-UI code adds calls to GUI-only APIs (the baseline in
  `ForbiddenCallBaseline.txt` may only shrink; remove its lines in the change that removes the calls), when headless
  code uses one of the members above (the failure names what to use instead), and when the capability table in
  `docs/cli.md` no longer matches the registry (the failure prints the expected table).
- The commands that `xmat.exe` accepts and its help come from the registry too: a `Supported` capability needs its
  command in `XMAT/Cli`, and a `Planned` capability has no command line until it ships. Commands, options and values
  that xmat does not implement, planned ones included, are usage errors (exit code 2). Messages repeat only names that
  xmat knows, never what the user typed. Cover command changes with the tests in `XMAT.Tests/Cli`, which run the built
  `xmat.exe` as a child process.

## Shared Operations and Additional Surfaces

Reusable application behavior lives under `XMAT/Operations/<Area>`. An operation implements
`IOperation<TRequest, TResult>` and exposes:

```csharp
Task<OperationResult<TResult>> ExecuteAsync(
    TRequest request,
    OperationContext context,
    CancellationToken cancellationToken);
```

`OperationResult<T>` carries an `OperationOutcome`, a value when successful, and `OperationDiagnostic` entries.
Diagnostic codes are stable, lower-case dotted names grouped by area, such as `analysis.input.unreadable`; add codes
instead of changing an existing code's meaning. `OperationContext.Progress` reports presentation-neutral progress,
and the cancellation token belongs to the caller.

The GUI, `xmat.exe`, and any other application surface are thin adapters over these operations. A surface may provide
an application-facing façade that selects and coordinates operations, but it does not copy their validation,
business rules or diagnostics into the surface.

Analysis operations accept files through `AnalysisInput.FromFile(...)` and in-app record sources through
`AnalysisInput.FromRecords(...)`. The GUI already uses the records form when it analyzes captures that it owns; a new
adapter should use the same input seam rather than adding another analysis path.

Add or update capabilities in `XMAT/Operations/Capabilities/CapabilityRegistry.cs`. The current statuses are
`Supported`, `Planned`, `GuiOnly` and `PolicyException`, and the tests in `XMAT.Tests/HeadlessParity` fail when GUI
commands or other entry points are unclassified. `Supported` currently describes command-line availability. A
different surface records its own availability instead of treating command-line support as that surface's contract.

For a companion console executable, follow the existing `xmat.exe` pattern: keep the host project small, reference the
shared XMAT assembly, add the project to the solution, copy its executable and runtime files beside the GUI during
packaging, and run a smoke command from the assembled artifact in `.github/workflows/dotnet-desktop.yml`.

## Compatibility of xmat.exe

- The JSON on standard output (`xmat-cli/1`) and `summary.json` (`xmat-analysis/1`) change only additively within
  their version: new fields, diagnostic codes, commands and capabilities. Removing or renaming a field, changing its
  type or meaning, or changing what a status value means is a breaking change. It ships as a new version (for example
  `xmat-cli/2`) with migration guidance in `docs/cli.md`, and the pull request says so.
- Exit codes are stable: add new codes, but never renumber or reuse one.

## Parity Exceptions

- The author of a change classifies the GUI entry points that it adds, and the reviewers check the classification. An
  external contributor's first pull request gets help with the classification from the maintainers instead of being
  blocked: say in the pull request where you are unsure.
- `GuiOnly` needs a presentation reason in the registry entry that says why there is nothing to run headless, such as
  arranging views or editing GUI-only preferences. An entry point that produces captures, analysis results or exported
  data, or changes devices or traffic, is not `GuiOnly`.
- `Planned` needs the slice and owner that will deliver the capability, and the pull request explains why it is not
  headless yet.
- `PolicyException` keeps a capability in the GUI by a reviewed decision, not for presentation: deleting sessions from
  a capture (`capture.delete-sessions`) stays a confirmed, irreversible GUI action, and headless runs export the
  sessions to keep instead. The entry needs its slice, an owner, a reason that starts with `Policy exception:` and its
  GUI entry points, and has no command line or operation. The tests accept only the capabilities on their reviewed
  list; adding one needs a reviewer's explicit approval and the reason in the pull request.
- `ForbiddenCallBaseline.txt` only shrinks. Adding a line to it, or a reviewed exception to the headless bans in
  `XMAT.Tests/HeadlessParity/HeadlessBans.cs`, needs a reviewer's explicit approval and the reason in the pull
  request.

# Coding Standards

This project utilized the standard [C# coding conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions).

# Reviewing Changes

Changes by external contributors require review by 2 Microsoft employees to be merged.

Changes by internal contributors require review by 1 Microsoft employee (excluding author) to be merged.