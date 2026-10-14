# Architecture

This document describes the high-level design of the Xbox Multiplayer Analysis Tool (XMAT).

## Overview

XMAT is a Windows desktop application for capturing and analyzing network traffic from Xbox and PlayFab services. It acts as an HTTP/HTTPS proxy to intercept service calls, captures low-level network packets, and runs configurable rule-based analysis to detect performance issues such as excessive call frequency, polling patterns, and throttling violations.

```
┌─────────────────────────────────────────────────────────────┐
│                     WPF UI Layer                            │
│  MainWindow · PreferencesWindow · AddDeviceWindow           │
│  CaptureAnalyzerWindow · Device/Analysis Tab Controls       │
├─────────────┬──────────────────┬────────────────────────────┤
│  WebService │  NetworkTrace    │  CaptureAnalysis           │
│  Proxy      │  Engine          │  Engine                    │
│  Engine     │                  │                            │
│  ┌────────┐ │  ┌────────────┐  │  ┌──────────────────────┐  │
│  │ Proxy  │ │  │ ETW/Packet │  │  │ Rules Engine         │  │
│  │ Server │ │  │ Capture    │  │  │ ┌──────┐ ┌────────┐  │  │
│  │        │ │  │            │  │  │ │Rules │ │Reports │  │  │
│  └────────┘ │  └────────────┘  │  │ └──────┘ └────────┘  │  │
│             │                  │  └──────────────────────┘  │
├─────────────┴──────────────────┴────────────────────────────┤
│                     Core Logic Layer                        │
│  Interfaces · Data Abstraction Layer · Utilities            │
├─────────────────────────────────────────────────────────────┤
│                     Data Layer (SQLite)                     │
│  WebProxyConnections · NetworkTracePackets · CaptureDevices │
└─────────────────────────────────────────────────────────────┘
```

## Technology Stack

| Technology | Usage |
|---|---|
| .NET 10.0 / C# | Application framework and language |
| WPF (XAML) | Desktop UI framework |
| SQLite | Capture data storage via `Microsoft.Data.Sqlite` |
| WebView2 | HTML-based report rendering |
| Roslyn Scripting | Dynamic C# script compilation for traffic modification |
| System.Text.Json | JSON serialization for settings, rules, and reports |

**Platform:** Windows 10 (build 17763+) and Windows 11, x64 only.

## Project Structure

```
XMAT/
├── App.xaml.cs                  # Application entry point and lifecycle
├── Logic/
│   ├── Interfaces/              # Core abstractions (ICaptureMethod, IDatabase, etc.)
│   ├── DataAbstractionLayer/    # SQLite data access implementation
│   ├── Logger.cs                # Async file-based logging
│   ├── PublicUtilities.cs       # Shared utilities (compression, IP resolution)
│   ├── BinaryReadingExtensions.cs
│   ├── UriUtils.cs
│   ├── Localization.cs          # JSON-based i18n
│   ├── ThemeManager.cs          # Light/dark theme support
│   └── Scripting/               # C# script compilation and execution
├── Models/
│   ├── CaptureAppModel.cs       # Central application state (singleton)
│   ├── CaptureDeviceContextModel.cs
│   ├── AnalysisRunModel.cs
│   ├── CaptureAppSettings.cs    # JSON settings serialization
│   └── Preferences/
├── Engines/
│   ├── WebServiceProxy/         # HTTP/HTTPS proxy capture engine
│   ├── NetworkTrace/            # Low-level packet capture engine
│   └── CaptureAnalysis/         # Rules-based analysis engine
├── Windows/                     # WPF window dialogs
├── Controls/                    # Reusable WPF controls
├── Themes/                      # Dark.xaml, Light.xaml
├── Langs/                       # Localization files (en-us.json)
├── Data/                        # API map CSV and rules JSON
└── Resources/                   # HTML/CSS/JS for report rendering

XMAT.Tests/                      # xUnit test project
```

## Core Abstractions

The application is built around a set of interfaces that enable pluggable capture methods and analyzers.

### Capture Pipeline Interfaces

```
ICaptureMethod                   # Defines a capture technology (Web Proxy, Network Trace)
  ├── Initialize() / Shutdown()
  ├── PreferencesModel
  └── OwnsDataTable()

IDeviceCaptureController         # Controls capture on a specific device
  ├── IsRunning
  ├── CaptureMethod
  ├── Initialize() / Close()
  └── LoadCaptures() / ClearAllCaptures()

ICaptureAnalyzer                 # Analyzes captured data
  ├── SupportedCaptureMethod
  ├── Initialize() / Shutdown()
  └── RunAsync()
```

### Data Layer Interfaces

```
IDatabase                        # Database operations
  ├── Initialize() / Shutdown()
  ├── SaveToFile() / TableNames()
  └── CreateTable() / TableByName()

IDataTable                       # Table with event-driven row management
  ├── AddRow() / UpdateRow() / RemoveRowsWhere()
  ├── Subset()
  └── Events: RowAdded, RowUpdated, RowsRemoved

IDataset → IDataRecord → IFieldDefinition   # Query results
```

### Application Model Interfaces

```
ICaptureAppModel                 # Central app state
  ├── ActiveDatabase / LoadedDatabase
  ├── CaptureDeviceContexts
  └── AnalysisRuns

ICaptureDeviceContext             # Single device being captured
  ├── DeviceType / CaptureType / DeviceName
  └── CaptureController
```

## Engines

### WebServiceProxy Engine

Captures HTTP/HTTPS traffic by acting as a forward proxy. Supports Xbox development kits, PCs, and generic proxy-capable devices.

**Key components:**
- **WebServiceProxy** — Core proxy server that listens on allocated ports, intercepts requests, forwards to origin servers, and captures responses
- **WebServiceDeviceCaptureController** — Per-device proxy lifecycle management and port allocation via `ProxyPortPool`
- **InternetProxy** — Windows WinInet P/Invoke wrapper for system-level proxy configuration
- **CertificateManager** — SSL/TLS certificate handling for HTTPS decryption
- **FiddlerSazHandler** — Import/export support for Fiddler SAZ archive format
- **ScriptHost** — Roslyn-based C# script execution for on-the-fly traffic modification

**Data storage:** `WebProxyConnections` table with request/response metadata (timestamps, headers, bodies, status codes, client IP).

### NetworkTrace Engine

Captures low-level network packets from Windows and Xbox devices using ETW (Event Tracing for Windows).

**Key components:**
- **NetworkTraceEngine** — Factory for local and remote trace engine implementations
- **NetworkTraceCaptureController** — Device connection and packet capture lifecycle
- **TraceEventNativeMethods** — P/Invoke wrapper for native ETW tracing APIs
- **NetworkTracePacketsCollection** — Observable collection with filtering by PID, TID, protocol, and IP addresses

**Data storage:** `NetworkTracePackets` table with packet metadata (process/thread IDs, timestamps, flags, base64-encoded payload).

### CaptureAnalysis Engine

Analyzes captured traffic against configurable rules to detect performance issues and violations. This is the most complex engine.

**Architecture:**

```
ServiceCallItem[]                  # Raw captured calls
       │
       ▼
ServiceCallData                    # Calls grouped by console IP and endpoint
       │
       ▼
RulesEngine.RunRulesOnData()       # Parallel rule execution
       │
       ├── Rule.Run() ──► RuleResult (violations + data)
       ├── Rule.Run() ──► RuleResult
       └── ...
       │
       ▼
Report.RunReport()                 # Report generation
       │
       ▼
JSON ──► WebView2 HTML rendering   # Displayed in UI
```

**Rules engine:**
- `Rule` (abstract base) — Defines `Run()`, `DeserializeJson()`, `Clone()` methods
- `RulesEngine` — Manages rule definitions, expands wildcard endpoints (`*`) to match actual endpoints, executes all rules in parallel via `Parallel.ForEach`
- Rules are defined in `XboxLiveTraceAnalyzer.Rules.json` with per-endpoint configuration
- API endpoint-to-method mapping loaded from `XboxLiveTraceAnalyzer.APIMap.csv`

**Built-in rules:**

| Rule | Detects |
|---|---|
| `CallFrequencyRule` | Calls exceeding sustained/burst rate limits |
| `BurstDetectionRule` | Sudden traffic spikes |
| `PollingDetectionRule` | Repetitive polling patterns |
| `ThrottledCallsRule` | HTTP 429 throttling responses |
| `RepeatedCallsRule` | Duplicate calls to same endpoint |
| `BatchFrequencyRule` | Batch operations exceeding limits |
| `SmallBatchDetectionRule` | Inefficient small batches |
| `CallRecorderRule` | Records all calls for debugging |
| `StatsRecorderRule` | Collects aggregate statistics |
| `XR049Rule` | Xbox-specific validation |

**Reports:** `PerEndpointReport`, `CallReport`, `StatsReport`, `CertWarningReport` — rendered as HTML via WebView2.

## Data Flow

### Capture → Storage → Analysis → Reporting

1. **Device setup** — User adds a device (Xbox Kit, PC, or generic) and selects a capture method (Web Proxy or Network Trace)
2. **Capture** — `IDeviceCaptureController` starts capturing; each captured item is stored as a row in the SQLite database via `IDataTable.AddRow()`; `RowAdded` events drive real-time UI updates
3. **Persistence** — Captures can be saved to file (`IDatabase.SaveToFile()`) and reloaded (`DataAbstractionLayer.LoadDatabaseFromFile()`); Fiddler SAZ import/export also supported
4. **Analysis** — `ICaptureAnalyzer.RunAsync()` reads the capture table, converts rows to `ServiceCallItem` objects, groups by endpoint, computes `ServiceCallStats`, then feeds data to the `RulesEngine`
5. **Rule execution** — Rules run in parallel; each produces a `RuleResult` containing violations (`RuleViolation`) categorized as Error, Warning, or Info
6. **Reporting** — Results are transformed into JSON reports, rendered in WebView2 using HTML/CSS/JS templates, and displayed in analysis tabs

## Data Layer

The data abstraction layer wraps SQLite via `Microsoft.Data.Sqlite`:

- **DataAbstractionLayer** — Static facade exposing `Database` (active) and `LoadDatabaseFromFile()` (read-only)
- **SqLtDatabase** — `IDatabase` implementation managing SQLite connections
- **SqLtDataTable** — `IDataTable` implementation with event-driven row lifecycle
- **SqLtDataset / SqLtDataRecord** — Lazy-loading query results with enumeration support

**Database tables:**

| Table | Created By | Fields |
|---|---|---|
| `CaptureDevices` | App startup | DeviceType, DeviceName, CaptureType |
| `WebProxyConnections` | WebServiceCaptureMethod | Request/response metadata, timestamps, headers, bodies |
| `NetworkTracePackets` | NetworkTraceCaptureMethod | PID, TID, timestamp, flags, payload |

## Design Patterns

### Product telemetry

`XMAT\Logic\Telemetry` is part of the application assembly. `XmatTelemetryService` owns the per-launch
session, fixed feature counters, explicit operation IDs, and build-derived export gate. Individual capture and
analysis operations retain their own timing/context, including simultaneous runs and restored/loaded/imported
origins. There is no user preference, settings store, installation ID, or offline event file.

`OneCollectorTelemetryReporter` owns an isolated logging pipeline with an empty resource builder populated
only by approved metadata. It suppresses ambient activity/baggage and scopes, rejects free-form exception
payloads, and batches at most 256 queued records with batches of 64. Shutdown waits at most two seconds;
unsent records remain memory-only and may be lost. SDK overflow/transmission/shutdown failures produce
sanitized local trace diagnostics, not uploaded application logs.

Metadata is read only for a validated enabled/approved build or an explicitly requested local sample run.
The host machine ID and sandbox are
explicit permitted identifiers; no fallback machine/installation ID is generated. Field names and event names
are case-sensitive. Public SDK packages are referenced directly; no DevTools implementation is vendored.

Lifecycle hooks reside in `App`/`MainWindow`; controller/result boundaries report real operations, and WPF
interaction hooks report fixed view/filter/report categories. Programmatic filter initialization is excluded.
Capture rows and script exceptions contribute counts only; their contents are never passed to the reporter.
Collector registration, retention/access policy, and backend enrichment review are external release gates.

An explicit `--telemetry-sample` launch in a non-exporting build instead selects
`LocalTelemetrySampleReporter`. It uses the same event attribute mapping, a bounded 256-record channel,
and a background UTF-8 JSON Lines writer, with no collector reporter/key or telemetry network export.
This opt-in development run reads the permitted metadata and creates only the requested new output file;
it creates no persisted telemetry preference or offline replay queue. Its normalized `name`/`time`/
`resources`/`data` envelope is intentionally distinct from the OneCollector wire format.
See [TELEMETRY.md](TELEMETRY.md) for exact fields, event semantics, and generation instructions.

| Pattern | Usage |
|---|---|
| **MVVM** | WPF data binding with `INotifyPropertyChanged` models |
| **Strategy** | Pluggable `ICaptureMethod` and `ICaptureAnalyzer` implementations |
| **Repository** | `IDatabase` / `IDataTable` abstraction over SQLite |
| **Observer** | Event-driven updates (`RowAdded`, `PropertyChanged`) |
| **Singleton** | `CaptureAppModel`, capture methods, analyzers |
| **Command** | WPF `ICommand` implementations for UI actions |
| **Factory** | `NetworkTraceEngine` for local/remote trace engines; `ReportViewModelFactory` for reports |

## Concurrency Model

- **UI thread** — All WPF operations marshalled via `PublicUtilities.SafeInvoke()` using `Dispatcher.BeginInvoke`
- **Analysis** — `ICaptureAnalyzer.RunAsync()` runs on background threads
- **Rule execution** — `Parallel.ForEach` for concurrent rule processing with `ConcurrentBag<RuleResult>` for thread-safe result collection
- **Logging** — `BlockingCollection<string>` with dedicated background consumer task
- **Port allocation** — `ProxyPortPool` uses `lock` for thread-safe port management

## Key Dependencies

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.Data.Sqlite` | 10.0.7 | SQLite database operations for capture storage |
| `Microsoft.Web.WebView2` | 1.0.3912.50 | Chromium-based web view for HTML report rendering |
| `Microsoft.CodeAnalysis.CSharp.Scripting` | 5.3.0 | Dynamic C# script compilation for traffic modification |
| `JsonDocumentPath` | 1.0.3 | JSON path queries for document navigation |
| `Microsoft.AspNetCore.App` | (framework) | Web server utilities |
| `OpenTelemetry` | 1.15.3 | Isolated structured telemetry logging and bounded batching |
| `OpenTelemetry.Exporter.OneCollector` | 1.15.1 | Approved Microsoft collector export |
