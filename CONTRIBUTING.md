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

# Headless Parity

Every capability that is not purely presentation must also work without the GUI, through the command-line tool
`xmat.exe` ([docs/cli.md](docs/cli.md)):

- Implement the capability once as a UI-free operation under `XMAT/Operations` (`IOperation<TRequest, TResult>`), and
  add its GUI adapter and its `xmat.exe` adapter in the same change. When that is not possible, the change needs a
  reviewed parity exception: classify the capability as `Planned` (with the slice and owner that will deliver it) and
  explain why in the pull request.
- Classify every new GUI entry point in `XMAT/Operations/Capabilities/CapabilityRegistry.cs`: routed commands, XAML
  `Command`, `Click` and `CommandBinding` entries, dialog windows, device and capture types, script events, analyzers
  and preferences. `GuiOnly` is only for presentation, such as layout, themes and navigation inside a view.
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
`Supported`, `Planned` and `GuiOnly`, and the tests in `XMAT.Tests/HeadlessParity` fail when GUI commands or other entry
points are unclassified. `Supported` currently describes command-line availability. A different surface records its
own availability instead of treating command-line support as that surface's contract.

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
- `ForbiddenCallBaseline.txt` only shrinks. Adding a line to it, or a reviewed exception to the headless bans in
  `XMAT.Tests/HeadlessParity/HeadlessBans.cs`, needs a reviewer's explicit approval and the reason in the pull
  request.

# Coding Standards

This project utilized the standard [C# coding conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions).

# Reviewing Changes

Changes by external contributors require review by 2 Microsoft employees to be merged.

Changes by internal contributors require review by 1 Microsoft employee (excluding author) to be merged.