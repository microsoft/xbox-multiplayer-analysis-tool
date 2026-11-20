# Network shaping: maintainer guide

This guide describes the implementation seams and invariants for future changes.
For behavior and user configuration, see [Web proxy: Network shaping](web-proxy.md#network-shaping)
and [Foreground proxy capture](cli.md#foreground-proxy-capture).

## Architecture

| Concern | Source of truth |
|---|---|
| Profile modes, presets, and numeric bounds | `XMAT/Engines/WebServiceProxy/Logic/Proxy/Shaping/NetworkShapingProfile.cs` |
| Current snapshot, active connections, shared buffer budget, and live history | `XMAT/Engines/WebServiceProxy/Logic/Proxy/Shaping/NetworkShapingController.cs` |
| Client-leg transport pacing, latency queue, and cancellation | `XMAT/Engines/WebServiceProxy/Logic/Proxy/Shaping/NetworkShapingMiddleware.cs` and `XMAT/Engines/WebServiceProxy/Logic/Proxy/Shaping/NetworkShapingPacer.cs` |
| Production listener registration | `XMAT/Engines/WebServiceProxy/Logic/Proxy/WebServiceProxy.cs` |
| Shared validation and profile changes | `XMAT/Operations/Captures/SetNetworkShapingOperation.cs` |
| GUI dialog and status | `XMAT/Engines/WebServiceProxy/Windows/NetworkShapingWindow.xaml`, `XMAT/Engines/WebServiceProxy/Windows/NetworkShapingWindow.xaml.cs`, `XMAT/Engines/WebServiceProxy/Views/CaptureConnectionView.xaml`, and `XMAT/Engines/WebServiceProxy/Views/CaptureConnectionView.xaml.cs` |
| CLI capture and timed changes | `XMAT/Operations/Captures/CaptureProxyOperation.cs`, `NetworkShapingPlan.cs`, `ProxyCaptureRecorder.cs`, and `XMAT/Cli/CliCaptureCommand.cs` |
| Anonymous history format and archive support | `XMAT/Operations/Captures/NetworkShapingMetadata.cs`, `XMAT/Operations/Export/CaptureExportSnapshot.cs`, `XMAT/Operations/Export/ExportCaptureSessionsOperation.cs`, `XMAT/Engines/WebServiceProxy/Logic/FiddlerSazHandler.cs`, and `XMAT/Engines/WebServiceProxy/Logic/BoundedSazReader.cs` |
| Analysis warning, report artifact, and HTML banner | `XMAT/Operations/Analysis/AnalyzeCaptureOperation.cs`, `XMAT/Operations/Analysis/NetworkShapingReport.cs`, and `XMAT/Engines/CaptureAnalysis/Logic/XboxLiveCaptureAnalyzer.cs` |
| Capability parity | `XMAT/Operations/Capabilities/CapabilityRegistry.cs` and the capability table in `docs/cli.md` |

The middleware is installed on the public proxy listener before
`ForwardProxyConnectionHandler`. It wraps the client transport: upload and
download budgets are shared per listener, independently by direction. The
origin-facing connection is not shaped. Keep pacing and latency in the shared
non-UI proxy core so the GUI and CLI use the same behavior.

## Behavioral and data invariants

- `Off`, `Shape`, `Broken`, and `Disconnect` have distinct semantics. `Broken`
  rejects new proxy connections but does not interrupt existing ones;
  `Disconnect` also aborts active proxy connections. Neither means the console
  link is down.
- A profile update applies to open connections. Update the controller snapshot
  atomically and wake pacing/delay waits; do not copy profile state into
  per-connection settings that become stale.
- Keep directional rate reservations shared across connections. Latency is
  pipelined, bounded, and defined as added round-trip time. Do not model packet
  loss, reordering, duplication, or corruption on a TCP stream.
- Queued chunks are pooled and bounded. Every cancellation, write failure, and
  connection shutdown path must return rented buffers and release budget slots.
  Each pump direction owns a small private reserve; only `Shape` mode borrows
  from the shared listener pool, and never waits on it, so a stalled peer or an
  unshaped connection cannot starve others.
- The live history cap must never prevent turning shaping off: at the cap,
  `Disconnect` and then `Off` remain accepted.
- The default request-body limits and inactivity timeout remain in force. Only
  the explicit, bounded total-body-deadline option changes the total upload
  deadline; do not silently relax protection when shaping is slow.
- Metadata records timestamped numeric conditions without device identifiers,
  hosts, paths, or profile-file locations. Validate size/count bounds on both
  read and write. Native, SAZ, sanitized SAZ, and analysis paths must preserve
  applicable history; imported history is display-only and must never mutate
  the live controller.
- The foreground CLI uses explicit client routing, a bounded capture lifetime,
  and private atomic output. It does not configure system/console proxies,
  firewall, certificate trust, or GUI preferences.
- Pin the foreground proxy's response-streaming defaults per instance, rather
  than inheriting GUI-global preferences. Keep its recorder alive through awaited
  proxy shutdown so terminal stream events are saved before publication. Record
  terminal responses once, not repeated progress prefixes; retain trailers,
  truncation markers and live-header provenance, and charge trailer UTF-8 bytes
  against the same recorder budgets as headers and bodies.
- New native captures store text through parameters and carry
  `CaptureTextFormat.LiteralUserVersion`, so replay and sanitized export can
  distinguish them from ambiguous legacy captures. Preserve both text provenance
  and shaping history when importing or exporting.
- Metadata parsing also runs during replay's pre-storage preparation. Use explicit
  JSON reading and known data constructors there, not reflective deserialization;
  keep the preparation-boundary tests intact.
- Pass the private staging database's extended native path to SQLite. Staging
  can cross Windows' classic path limit even when the requested output does
  not. Keep a child-process `xmat.exe` regression: the long-path-aware test host
  or `dotnet xmat.dll` can hide this failure.

## Extending the feature

1. Change profile validation in `NetworkShapingProfile`; keep the GUI and CLI
   on the same operation and validation path.
2. If transport behavior changes, add a focused failing proxy test first. Cover
   both directions and relevant routes (HTTP, intercepted TLS, opaque CONNECT,
   and WebSocket); use `IsolatedProxyTests.Name` for timing-sensitive tests.
3. If fields or lifecycle change, update the versioned shaping plan, native and
   SAZ metadata validation, sanitized export, import display-only behavior, and
   analysis/report output together. Do not activate settings from an archive.
4. For a GUI entry point, update the capability registry and the sorted
   `docs/cli.md` table in the same change. Keep stable CLI JSON additive.
5. Update [the user guide](web-proxy.md#network-shaping), this guide, and
   [CLI documentation](cli.md#foreground-proxy-capture) for user-visible
   behavior or option changes.

## Focused validation

Run from the repository root with the normal in-repository output path:

```powershell
dotnet build XMAT.Cli\XMAT.Cli.csproj -c Debug -p:Platform=x64 -p:TreatWarningsAsErrors=true
dotnet test XMAT.Tests\XMAT.Tests.csproj -c Debug -p:Platform=x64 -p:TreatWarningsAsErrors=true --filter "FullyQualifiedName~XMAT.Tests.Proxy|FullyQualifiedName~XMAT.Tests.Cli|FullyQualifiedName~HeadlessParity|FullyQualifiedName~NetworkShapingMetadataTests|FullyQualifiedName~NetworkShapingWindowTests|FullyQualifiedName~ProcessFilteredAnalyzerAdapterTests"
```

The capability and forbidden-call parity tests locate the repository from the
default test output path; a custom `BaseOutputPath` can make them fail to find
the solution rather than validate the change. Run the broader CI matrix when
shared lifecycle, export, or application-publication behavior changes.
