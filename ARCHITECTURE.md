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

IQueryDatabase                   # Shared generic SQL capability on both database interfaces
  ├── GetQuerySchemaAsync()
  └── CreateQuerySessionAsync()

IQuerySession → IQueryResult      # Immutable snapshot/results, independent of capture records
  ├── ExecuteAsync()             # One read-only statement, deadline and store quota
  ├── ReadPageAsync() / ReadValueAsync()
  └── DisposeAsync()             # Drain readers and delete owned temporary storage
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
| `XR049Rule` | Xbox rich-presence validation |

`XR049Rule` checks JSON objects for a top-level `activity` property, skipping empty request bodies and non-object JSON values. Malformed JSON is recorded as a per-call warning in the exported rule results, and scanning continues with subsequent calls.

**Reports:** `PerEndpointReport`, `CallReport`, `StatsReport`, `CertWarningReport` — rendered as HTML via WebView2.

## Data Flow

### Capture → Storage → Analysis → Reporting

1. **Device setup** — User adds a device (Xbox Kit, PC, or generic) and selects a capture method (Web Proxy or Network Trace)
2. **Capture** — `IDeviceCaptureController` starts capturing; each captured item is stored as a row in the SQLite database via `IDataTable.AddRow()`; `RowAdded` events drive real-time UI updates
3. **Persistence** — Captures can be saved to file (`IDatabase.SaveToFile()`) and reloaded (`DataAbstractionLayer.LoadDatabaseFromFile()`); Fiddler SAZ import/export also supported, and network traces captured on another PC with `netsh trace start capture=yes` (.etl) can be imported
4. **Analysis** — `ICaptureAnalyzer.RunAsync()` reads the capture table, converts rows to `ServiceCallItem` objects, groups by endpoint, computes `ServiceCallStats`, then feeds data to the `RulesEngine`
5. **Rule execution** — Rules run in parallel; each produces a `RuleResult` containing violations (`RuleViolation`) categorized as Error, Warning, or Info
6. **Reporting** — Results are transformed into JSON reports, rendered in WebView2 using HTML/CSS/JS templates, and displayed in analysis tabs

## Agent access and application operations

The standalone `xmat.exe` host and the connected desktop MCP host are separate adapters, not separate analysis implementations. `XMAT.Cli` handles offline command-line input/output without creating a WPF application or reading desktop settings/logs. `AnalyzeCaptureOperation` under `XMAT/Operations/Analysis` owns conversion, body identities, grouping, API mapping, exact rule/work budgets and canonical report construction. WPF and `CaptureInvestigation` supply `AnalysisInput.FromRecords` from their existing DAL-owned data/snapshots; only the standalone file adapter uses `FromFile`. Canonical retention is explicit and internal: CLI JSON remains its sanitized, versioned summary, and retaining reports with `OperationContext.None` creates no files.

The local MCP surface is owned by `CaptureAppModel` and is opt-in through **Actions > Agent Access**. The official MCP SDK serves explicitly registered tools inside the desktop process; `XMAT.AgentBridge.exe` forwards standard MCP stdio to a local named pipe without loading WPF or the capture/DAL implementation. Both sides enforce current-user/elevation checks, and the companion pins the configured process identity and start time.

Desktop-only dispatch and dialog calls are centralized in the WPF `App` adapter (`App.Dispatching.cs`), not added to the UI-free operation or duplicated across application façades. The headless GUI-call and forbidden-member ratchets remain intact and shrink when old engine dialogs disappear. The capability registry describes standalone command-line coverage; connected MCP availability/grants remain separately discoverable.

`CaptureCatalog` assigns opaque, application-lifetime capture IDs and tracks preflight revisions separately from capture ownership epochs. `ICaptureOperations` remains the shared lifecycle boundary for WPF and agent actions. Transition receipts/failure metadata stamp the ownership epoch before releasing the operation lease, so a delayed caller cannot adopt a subsequent human restart. The MCP adapter resolves explicit IDs and rechecks revisions on the dispatcher before committing an action; it never changes the selected tab to execute work.

`AgentRuntime` owns connection-bound grants, one-shot desktop approvals, idempotent jobs, result authorization, and cleanup ownership. Public IDs/client names are not authority. Pending approval source text is kept out of agent status responses. Query error details are protected results requiring the original current source permissions; ordinary job/audit metadata never includes SQL or raw exception text. Failed jobs discard unpublished temporary results/artifacts rather than consuming cache capacity with unreachable IDs.

The non-modal `AgentAccessWindow` displays sessions/grants, source-specific raw-query/export permissions, query/export limits, operations/approvals, recovery items, and takeover controls. Main-window close suspends new application requests before existing close checks; a cancelled close resumes acceptance without dropping live grants. Once close is committed, the host stops accepting connections and drains sessions before the data layer shuts down.

Desktop grant edits atomically replace application-wide permissions and the explicitly selected capture/source/folder/input scopes, preserving all other targets. Unchecking a selected permission and applying it still performs normal revocation cleanup; adding a second target no longer revokes the first. A separate granted-target list derives its entries from actual permissions, not the current catalogs, so retained or replaced source scopes can be explicitly revoked without disconnecting or changing other targets. Full-session replacement remains available to trusted callers, and revoke/disconnect still removes every grant. These edits do not change the bounded, non-evicting idempotency ledger.

Final temporary cleanup attempts every retained entry even if another deletion fails. Failed result/snapshot disposal can be retried without reopening data readers, and retryable temporary recovery remains owned by the runtime. Persistent temporary-file locks are logged and surfaced as recovery, not cached as an unfixable host-stop error. Only temporary resource kinds are retried during final cleanup; capture and script recovery still respects explicit lifecycle/ownership policy.

### Query and export boundary

`ICaptureQueries` / `CaptureQueries` discovers the active and single currently loaded database. IDs follow database object identity rather than filenames or UI selection. Source resolution and acquisition of the existing stopped-capture data lease happen on the dispatcher; the lease is released after snapshot creation, before SQL execution. Replacing a source invalidates new requests for its old ID without changing already materialized results.

Only the DAL opens SQLite connections. `SqLtQuerySession` incrementally copies a snapshot, reopens it read-only without pooling, installs a connection-local native authorizer, validates the entire input's prepared-statement boundaries before stepping, and uses the SQLite progress callback for real CPU-bound cancellation/deadlines. `SqliteCommand.Cancel()` is not relied on. SQL cannot change application state, attach databases, control transactions/PRAGMAs, or load extensions.

`SqLtQueryResult` materializes the complete successful result into quota-limited disposable storage, preserving storage classes in affinity-free ordinal columns. Public labels and origin metadata remain separate, including duplicates. Bounded previews and exact UTF-8/BLOB byte chunks avoid loading large cells into managed memory. Failed, cancelled, or quota-limited runs are not published as partial successes.

Result paging batches its type/length/value projections within SQLite's native column limit and joins the immutable batches by row ordinal. Accepted wide queries therefore remain pageable and exportable without reducing the query's supported column count or rerunning the original statement.

`AgentQueryApplication` applies session/reference ownership, exact-source raw grants, bounded responses, HMAC continuation cursors, and retained-result admission limits. Every arbitrary SQL expression requires raw permission, regardless of its apparent aggregate shape. Complete results remain frozen across source changes; permission revocation destroys them. Current defaults are 100 rows/64 KiB per response, 60 seconds, 512 MiB per result store, and four retained/running results; snapshot/scratch disk usage is additional.

`QueryResultExporter` consumes only `IQueryResult`, streams CSV/JSON including full large values, and never opens a database or reruns SQL. Agent export additionally requires source-scoped `QueryResultExport` and exact-request sensitive-export approval. The adapter publishes a managed artifact ID only after successful file completion, with no arbitrary destination path. Downloads recheck raw/export permissions and connection identity, are base64 byte-budgeted, and can be explicitly released. Export pins the result across all page/chunk reads; result release waits rather than disposing it between reads. Artifact reads likewise drain before cleanup.

### Search and structured analysis

`ICaptureInvestigation` / `CaptureInvestigation` uses `ICaptureQueries` and fixed, field-constant-based DAL projections. HTTP search reads metadata only; packet statistics decode stored packets internally. Inputs specify the source, capture kind, and typed device/time/HTTP/packet filters, never a selected controller or arbitrary SQL fragment. `CaptureSnapshotReader` adapts immutable typed results to existing `IDataRecord` consumers without obtaining a provider connection or handle.

Analysis acquires a source-validated, selection-independent application analysis lease after snapshot acquisition. Acquisition/release and preference snapshots occur on the dispatcher; CPU work and immutable result construction run off it. The lease prevents concurrent use of shared rule-engine state and remains held while cancellation drains a non-interruptible rule.

`XboxLiveCaptureAnalyzer.AnalyzeRecords` is a compatibility adapter over `AnalyzeCaptureOperation`, not another conversion/rules loop. The shared operation retains canonical rules/documents for every console when requested, preserving the existing first-console file/report projection for desktop compatibility. Original capture row IDs are mapped through service-call breadcrumbs during the same conversion pass, including copied synthetic telemetry. `AnalysisResultsModel` retains canonical results separately from rendered report models.

`NetworkTraceStatistics` is a deterministic explicit-packet core shared by the desktop and agent service. The WPF adapter still copies its visible `FilteredItems`; agent inputs come from the requested source/filter with no rendered grid. Options are snapshotted, numeric totals use Int64, matching payloads compare bytes in timestamp order, second buckets include the full UTC date, and unsupported IP headers remain unknown rather than being classified as an IPv4 protocol. These directly related fixes preserve the intended metrics without claiming packet-loss or retransmission proof.

`AgentDataApplication` summary responses return only allowlisted record metadata, numeric metrics, rule identities/severity/counts, and separately paged evidence. No raw engine graph, request URI/query, headers/bodies, free-form violation text, or arbitrary `RuleResult.Results` dictionary is serialized into those summaries. Run-scoped opaque console/endpoint IDs preserve grouping when display metadata is unavailable. Raw report/detail access and WebSocket inspection use the separately authorized adapters below.

These tools require `SummaryRead` for the exact source; application metadata permission or a different source's grant is insufficient. Search/analysis caches are connection-owned `CaptureData` resources with bounded rows/bytes, source/result/section-bound HMAC cursors, idempotent jobs, explicit release, and revoke/disconnect cleanup. The input spool and serialized-output ceilings reuse desktop data limits; they are not a hard managed-heap cap. Agent runs do not write report files or add WPF tabs.

### Script operations and ownership

`ICaptureScripts` / `CaptureScripts` resolves explicit registered capture/script IDs through the shared catalog, rejects read-only/unsupported targets, and marshals bound model mutations to the dispatcher. It exposes typed source/state/diagnostic contracts, not `ScriptModel`, Roslyn objects, server paths, arbitrary reflection, or routed UI commands. The existing editor's load/revert/toggle paths share `ScriptOperations`; text binding still uses the model's source-invalidation invariant.

`ScriptModel` has a stable model ID, monotonic activation/source revision, atomic state snapshots, compare-and-set edits/disables, and committed activation receipts. Receipts capture the epoch/hash under the model lock before property-change callbacks, so delayed completion cannot adopt a subsequent human restart. Editing clears execution; validation never installs code. Source hash plus revision rejects edit-away-and-back races. Cancellation clears a still-current compiling state without affecting a newer human activation.

The Roslyn host retains the existing imports/references and trusted desktop activation settings. Compile-only validation and agent activation disable source/metadata resolvers for `#load`/`#r`; otherwise a source-read-only client could use compilation to read arbitrary files or activate unreviewed loaded source. This does not sandbox enabled C#: script-execution trust remains effectively full in-process code-execution trust, and callbacks cannot be forcibly interrupted.

`AgentScriptApplication` separates capture-scoped read/edit/execute grants, mandatory exact-diff source-edit approval, and exact-source activation approval. Source/symbol/diagnostic responses are byte/row bounded and session-authorized. Diagnostic messages are protected caches; failed compilation publishes a gated receipt and keeps those diagnostics readable, while failed jobs with unreachable receipts release them. Runtime/audit metadata never includes source, approval text, diagnostic messages, or filenames.

Only changed successful activations register `Script` ownership; already-enabled human code is not adopted. Cleanup uses the committed epoch through `DisableOwnedAsync`, disabling future execution only if it still matches. Human edits/re-enables and takeover survive disconnect. Drafts and user files are not reverted/deleted; in-flight code may still finish. Revocation during compilation cancels/drains before cleanup, preventing a cancelled candidate from becoming a later executable.

### Network Trace lifecycle and transport

Network Trace uses the same `ICaptureOperations` boundary as desktop and Web Proxy lifecycle calls. `INetworkTraceEngineFactory` supplies non-interactive prerequisites/connection/disconnection and test fakes. Local elevation is checked before reset/command effects; UAC/restart decisions remain in the WPF adapter, never an agent tool or engine dialog.

`NetworkTraceCaptureController` owns a phased transition/recovery state. Failed start outcomes distinguish definitive rejection from potentially committed effects; stop tracks unfinished Stop, Download, Publish, or Disconnect work. Retry resumes only that phase. The controller retains the session and truthful may-be-running state, rejects clearing/closing until resolved, and guards concurrent/repeated operations.

Complete event retrieval stages validated compact JSONL in an owned temporary directory, then streams rows into `IImportedCaptureTable.ReplaceImportedRows`. Only a complete successful download is published. UI projection is prepared off-thread and assigned on the dispatcher; a projection/disconnect failure retains committed rows and does not repeat stop/download/replace on retry. New packet timestamps preserve full UTC date/time. No controller/tool opens a SQLite connection.

Atomic publication can overlap other capture callbacks, so `SqLtConnectionGate` serializes connection-bound table writes, transaction publication, dataset command/reader access, schema/snapshot acquisition, and backups across tables. Existing dataset enumeration remains streamed rather than forcing another full capture copy. This prevents a different table's command from accidentally entering an active publication transaction.

Local trace commands execute system `netsh` without shell/UAC flags, use per-engine GUID session names, and query only that owned session when handling an already-stopped trace. Remote device-portal commands have bounded complete-message decoding/deadlines, definitive-versus-uncertain outcomes, known stopped trace IDs, and non-modal connection failures. The existing device-portal self-signed certificate behavior is preserved. Native ETL callback errors are retained and propagated after parsing rather than escaping across a reverse-P/Invoke boundary.

Agent Start discloses packet reset as a data-deletion approval; recovery Stop requires explicit human confirmation. Safe unchanged owned Local PC traces are stopped/retrieved during session cleanup; Xbox/uncertain traces retain human recovery resources. `ICaptureRecoveryOperations` separately acknowledges an operator's external/manual recovery decision. That action abandons the pending handle without asserting/stopping an external trace and preserves existing captured rows/files. Its explicit result reports `TraceStoppedByApp=false`.

### Device/context management

`ICaptureDevices` / `CaptureDevices` is the shared boundary for device creation, explicit-target safe removal, parameter/proxy-status inspection, and persisted pending-port drafts. `AddDeviceWindow` collects a typed choice; default-console/GDK resolution no longer executes inside its OK handler. The shared service binds/revalidates the resolved address, uses the model's existing identity rules/controller construction/DAL registration, and never starts capture or grants control. Main-window New and tab-close orchestration use this service; desktop-only close decisions remain callbacks supplied by WPF, not agent-supplied approval flags.

Agent removal only retires stopped writable contexts without unresolved recovery. Supported device configuration is freshly read before acquiring the global data lease, permitting unrelated capture stops during a slow console check; the target revision is rechecked afterward. An owned/unknown/unreachable proxy blocks removal. Existing Web Proxy close/drain clears its rows and releases its current/pending reservations; Network Trace close retains packet rows. Removal advances the capture epoch and reconciles ownership tombstones so delayed receipts cannot resurrect ownership of a removed context.

Pending-port queue/discard uses the controller's existing reservation/rollback behavior, extended to drain asynchronous settings saves under the transition/data guards. Settings serialization snapshots WPF-bound preferences/parameters on the dispatcher and performs atomic file replacement off-thread; ordered writes cannot overwrite a newer desktop snapshot with a delayed older write. Add/removal receipts distinguish a committed context change from failed persistence; errors/paths are excluded from ordinary agent metadata. Contexts and drafts remain after disconnect, unlike actively owned capture/script resources.

`AgentDeviceApplication` adds application-scoped `DeviceCreation` and separately scoped `DeviceConfiguration` permissions, mandatory exact-request approvals, expected revisions and idempotency. Five typed tools (`xmat_describe_devices`, `xmat_get_device`, `xmat_add_device`, `xmat_remove_device`, `xmat_set_pending_proxy_port`) extend the catalog to 36. Creation never implies capture/raw/script grants; parameter/status reads require application summary permission and exclude private configuration/error details. The desktop manager follows context collection changes and provides distinct creation/selected-context grants.

### Device proxy configuration

`ICaptureProxyOperations` / `CaptureProxyOperations` extracts the complete WPF enable/disable/port-application workflow. It uses existing controller initialization/start/drain/rollback, `IProxyDeviceConfiguration`, port reservations and `ProxyDeviceOperations`, rather than wrapping a bare console SDK call. The UI still owns source selection and reboot/manual-update dialogs; agent approvals are server-owned and cannot be supplied as booleans.

Preflight binds an explicit capture revision, assigned source address, resolved console identity, observed configuration/endpoint and exact port draft. Execution resolves/rechecks these before effects and acquires the same per-device target guard. Console SDK operations target the resolved address instead of resolving an approved hostname independently for each write. Enable rejects foreign endpoints; explicitly approved console Disable discloses unknown/foreign-setting effects. Captured history is preserved.

Global data leases are held only for listener mutation and saved commits, not while an SDK write or reboot/readback is pending. Controller transitions and resolved-target guards continue blocking same-device overlaps while other device stops remain available. In-flight SDK writes drain without promising forcible cancellation; readback waits are bounded and explicitly observe cancellation even when providers return already-completed tasks.

Port application shares the existing disable/readback, asynchronous saved commit, enable/readback sequence. Failed saves restore parameters/reservations and retire only an uncommitted replacement; committed/uncertain enables retain the new endpoint for recovery. Supported-device disable stops the listener only after confirming readback. Receipts are stamped under the controller transition and expose requested/confirmed configuration, committed port, persistence, unfinished phase and current listener state without private SDK text.

`AgentProxyApplication` requires exact-capture DeviceConfiguration plus CaptureControl and separate ScriptExecute/source approval when active scripts can run. Three typed tools bring the explicit catalog to 39: `xmat_get_proxy_options`, `xmat_set_device_proxy`, `xmat_apply_proxy_port`. Still-owned newly started/reassigned listeners use the existing capture resource cleanup policy; console/manual/uncertain state is retained for human recovery, not silently rebooted or overwritten on disconnect. Manual setup is expressly unverified and needs an operator decision before applying a port.

### Approved query file exports

`ICaptureFileExports` / `CaptureFileExports` owns opaque approved folder identities and new-file query serialization/publication. Both managed downloads and file destinations consume `IQueryResult` / `QueryResultExporter`; no SQL connection or reexecution is added. `CaptureAppModel` supplies protected loaded-source paths through dispatcher snapshots. The desktop manager registers folders through a picker and grants separate FileWrite authority for an exact ExportFolder scope; source raw/export permissions remain independently required.

`WindowsExportFolder` rejects relative/device/network/mapped roots, nested filenames, alternate streams, reserved device names and reparse paths. Only ordinary local NTFS/ReFS roots are accepted. It opens every directory ancestor without following reparse points, validates final canonical paths and volume/file identities, and pins directories without write/delete sharing during publication. Folder replacement invalidates the old identity. The owned output handle is renamed atomically with `FILE_RENAME_INFO.ReplaceIfExists=false`; no string-path temporary swap or existing destination overwrite is possible. Unpublished data is deleted through that handle, not a broad temporary-directory cleanup.

`xmat_list_export_folders` lists only currently granted folder descriptors with bounded continuation. `xmat_save_query_result` requires RawDataRead, QueryResultExport and exact-folder FileWrite, plus exact destination/sensitivity desktop approval. Query result lifetime pinning covers serialization and publication; cancellation, grants, quota/deadline and destination identity are checked again before commit. Published user files are not transient resources and survive disconnect; failure cannot be presented as a completed/truncated export.

Folder publication remains new-file/new-directory-only for typed outputs, without overwrite, arbitrary subdirectories, UNC/mapped roots or generic file access. This is the final fail-closed agent authorization policy, not an unrestricted filesystem promise. Script/native/SAZ/ETL inputs are separately identity-granted; human SQL file-picker exports retain their distinct overwrite/privacy consent. The native directory/handle policy is not a sandbox against other same-privilege code.

The integrated typed catalog covers device creation/removal/proxy inspection/configuration/port drafts, metadata/prerequisites, Web Proxy and Network Trace lifecycle/recovery/clear, read-only view closure, source/schema discovery, controlled search, structured/raw snapshot-bound investigation, WebSockets, completed report manifests/export/navigation, protected SQL, managed CSV/JSON downloads, approved typed file exports and native/SAZ/ETL inputs, scripts, host policy, preferences, diagnostics/public certificates, and explicit desktop/lifecycle requests. The human SQL workspace consumes the same query/result/export foundation.

### Active capture archive workflows

`ICaptureFiles` / `CaptureFiles` stages complete source-native captures and raw SAZ archives through the existing DAL `SaveToFile` and `FiddlerSazHandler` serializer. `CaptureQueries.ReadSourceAsync` resolves exact active/current-loaded source IDs under the stopped capture-data lease; neither MCP nor file publication opens a SQLite connection. Both database interfaces expose `ICaptureDatabaseBackup`; `SqLtDatabaseBackup` coordinates provider backup under the connection gate and rejects replacing its own source path. Loaded backup uses its existing read-only connection rather than copying the input filename or switching to the active database. Archive handles remain private, immutable and quota-checked; the source lease drains native backup before cancellation, then publication remains independently cancellable.

WPF Save/raw Export call the same staging service and keep dialogs/desktop overwrite behavior in their adapter. Native save includes all tables/device registrations; raw SAZ deliberately includes every active Web Proxy row, preserving the prior whole-method export semantics rather than silently using selection/grid state. Raw SAZ now disposes its archive on serializer failure and accepts cooperative cancellation through existing entry writers. Sanitized desktop export remains unchanged.

`CaptureFileExports` reuses its pinned-folder/exclusive temporary handle/atomic no-replace policy for matching `.xmatcap`/`.saz` destinations. `xmat_save_capture` requires exact-source RawDataRead plus separate CaptureExport and exact-folder FileWrite, with explicit privacy/scope approval. Published files are not transient resources; source/revocation/destination checks and admission/size/deadline limits govern the job, and stage cleanup removes only its owned workspace.

### Source/device-scoped sanitized SAZ

`CaptureFiles.CreateSazArchiveAsync` reads the explicit active/current-loaded source through the existing `CaptureQueries` stopped-source lease and DAL dataset interfaces. Optional exact `DeviceName` filtering uses the stored field contract; no controller/grid/selected-tab state is read. Native database backup includes the entire exact active/loaded database and rejects device filters.

Raw and sanitized SAZ share owned immutable staging/publication. `SanitizedFiddlerSaz` invokes the existing `FiddlerSazHandler.CreateSanitizedSazFile` policy, not a second redactor, and clones its numeric session/redaction/omission/work-budget summary into the archive receipt. `xmat_save_capture` exposes these typed formats and scope without adding an arbitrary query/filter path; current source grants and explicit privacy approval remain required even for sanitized output.

Loaded sources retain separate object-identity grants, so active permission cannot read an imported database's SAZ. Already staged archives remain frozen if later data changes. Sanitization is not anonymization: allowed content/host names/timing can still reveal identity. The desktop's selected-context sanitized export remains unchanged, while the agent explicitly chooses source and optional device.

Desktop native/raw archive commands require stopped captures consistently with the shared lease. Offline ETL and manifest report export use their shared workflows below. Temporary staging is additional disk use and not encrypted; native backup may need to finish before cancellation is observable.

### Exact script-source exports

`ScriptFiles` snapshots the script model and provides the shared UTF-8-without-BOM serializer used by desktop Save and approved script exports. Desktop Save retains its selected filename/overwrite behavior but now writes atomically from the captured source off-thread. The agent route reads the existing `ICaptureScripts` source snapshot and never edits/rebinds the model, resolves compiler directives, compiles or installs executable code.

`xmat_save_script` adds exact-revision/hash source export, bringing the typed catalog to 43 tools. `AgentScriptApplication` requires capture-scoped ScriptRead and separate ScriptExport plus exact-folder FileWrite and destination/privacy approval. It rechecks registered capture/script identity and revision/hash both after approval and immediately before publication; ABA edits and replaced script models cannot reuse approval. A source change or revoke during a paused export discards only its owned unpublished output and releases admission for retry.

The file publisher shares a private stream-to-owned-handle implementation across query, capture and script serializers while retaining typed public contracts and fixed extensions. Scripts are `.txt` only, with existing no-replace/non-reparse/root identity policy, output quota/deadline, and per-session running script-file admission. Receipts contain source identity/hash/revision and bytes/publication, not source text or remembered filenames in audit metadata. Published user files persist on disconnect and export does not adopt/disable an active script. Native capture loading is described below; multi-file report-manifest export remains separate work.

### Approved script inputs and source loading

`ICaptureFileInputs` / `CaptureFileInputs` registers trusted-desktop-selected `.txt`, `.xmatcap` and `.saz` inputs with opaque identities, typed kinds and original-byte hashes. The native directory policy also opens an input with `OPEN_REPARSE_POINT`, validates the final file path/type and volume/file identity, and excludes write/delete sharing while its bytes are hashed and decoded/copied. Parent identity, file identity, length and SHA-256 are checked again on use; no path or provider handle is accepted by an agent. Registration stores descriptors/private paths, not permanently open handles or implicit grants.

The desktop picker and exact-input `FileRead` scope are independent of capture ScriptRead/ScriptEdit and of output folders. `xmat_list_input_files` returns only granted file metadata; `xmat_load_script` requires all three permissions and exact input/diff approval. These two tools brought the explicit catalog to 45 before the native load tool below. There is no generic read-file, browse, registration or self-approval endpoint; each reader rejects inputs of the wrong kind.

`ScriptFiles.ReadAsync` preserves desktop BOM-aware decoding. Desktop Load/Revert snapshots a revision, reads off-thread, then uses `ScriptOperations.Update` compare-and-set and changes the remembered filename only for a successful human Load. The agent uses `ICaptureScripts.UpdateAsync` with the exact original revision/hash, never filename rebinding, compilation or activation. Changed text invalidates old execution; identical no-ops preserve existing state.

Preflight/execution file reads are deadline/byte limited and share admission with script file exports. Approval binds the original file hash and decoded UTF-8 source hash; execution rereads through a pinned handle and compares that source before committing. Revoke/cancellation, changes during a read or stale script versions cannot overwrite newer human work, and admission is released for retry. Source/diff strings are memory-resident, so byte limits are not a hard heap cap. Loaded drafts persist after session end; file content is untrusted evidence, not execution/approval authority.

### Transactional native capture loading

`ICaptureFileLoads` / application-owned `CaptureFileLoads` stages the desktop/agent loading workflow, not a database-open wrapper or selected-context command. Verified `.xmatcap` bytes are copied from the exact pinned input into an owned private workspace; desktop paths likewise produce private frozen copies. Copying first passes through the same DAL immutable reader used by the headless operation: original sidecars, WAL headers, writer sharing, normalized paths and bounded structural/schema preflight are checked before copying, not bypassed by opening only a sidecar-free temporary file. The DAL verifies that its guarded file is the approved input identity and copies those exact original bytes. Only the DAL opens the read-only database, and failed constructors dispose their handles. Dataset identifiers are quoted and filter values bound. Writable datasets apply the same legacy text encoding as their writers/import transactions; native read-only datasets compare exact stored values without encoding them again.

The service validates the device schema/types/names, deduplicates the existing read-only key, resolves/constructs detached controllers on the dispatcher, and prepares every HTTP/packet projection off-thread through existing controller cores. No registered collection/source/view is changed on failure or cancellation. Read-only trace initialization does not subscribe to or clear the active packet table.

A model-owned preparation lease permits one load per application and prevents close while preparation drains; it does not block unrelated capture stops with a long-held global data lease. Commit briefly acquires the existing capture-data lease, rechecks the exact loaded-source revision/read-only-context fingerprint and live permissions/cancellation after admission callbacks, and assigns all prepared state/source/registrations without notifications. Only then are view/source/collection notifications sent. Read-only contexts are reused; packet collections are replaced rather than appended. Notification/release failures yield a published receipt with `ViewsNotified=false`, never an invalid-input error implying undo. WPF selection is preserved by the shared operation; the human File > Load adapter retains its explicit final tab selection.

`LoadedCaptureDatabaseRegistry` is the single database/controller owner and save-destination guard for both loading adapters. Managed workspaces retain an additional current-source lease, so closing the last view does not dispose the source still offered to SQL/MCP; unmatched older views retain their database independently. Frozen copies also register the original input path/identity, so application Save cannot overwrite it through its name or an alias. Final desktop publication uses the registry's synchronous destination lease only around its atomic move, never across an await. Superseded unreferenced workspaces drain their connections and are removed asynchronously; shutdown cancels/drains preparation and disposes all owned sources before the active DAL. Cleanup uses only owned workspace directories. Hard shutdown or cleanup failure can leave unencrypted data, and retained files/decoded models are additional disk/heap usage beyond the per-input byte ceiling.

`xmat_load_capture` requires application `CaptureLoad`, exact-input `FileRead`, expected current loaded-source ID (null only for none), idempotency, and hash/source-replacement approval. A server-owned preapproval state fingerprint also binds the read-only view registry. Revoke, stale source/registry, changed input, cancellation, deadline and failure release admission/unused storage. Loading never starts captures/scripts, edits active writable tables, selects agent targets, or implicitly grants access to the newly loaded source. Committed captures remain after disconnect. Multi-file reports retain their artifact-manifest/provenance boundary rather than a generic file-copy API.

### Shared transactional SAZ import

`ICaptureSazImports` / application-owned `CaptureSazImports` provides the full desktop/agent workflow. It binds the actual active DAL source and matching read-only context revisions, stages verified typed input, resolves/constructs a detached controller, and prepares models/filters through `WebServiceCaptureMethod`'s existing SAZ core off-thread. The model's file-preparation lease serializes native loads/imports and prevents close without holding the capture-data lease across decoding. All captures must be stopped; exact writable-name collisions are rejected.

Finite imports reuse the headless `BoundedSazReader` and archive-directory preflight rather than a second decompression/limit implementation. Both adapters share `FiddlerSazHandler`'s streamed HTTP/header parser, malformed/incomplete-frame diagnostics, fixed header limits and DTD-prohibited XML parsing. Expanded entry bytes and entry envelopes are bounded before publication, and cancellation is cooperative. Prepared row serialization applies a separate byte ceiling, including base64/text storage and envelopes; no broad SQL or filesystem endpoint is introduced. These are work/data limits, not a strict managed-heap cap.

Commit acquires the stopped capture-data lease, revalidates the input-bound source/context target and live permissions/cancellation after admission callbacks, and invokes the existing atomic `IImportedCaptureTable.ReplaceImportedRows`. That DAL transaction checks cancellation before work, between rows and immediately before commit, rolling back exact old rows/IDs on a failed iterator/limit/write/cancel. Once it commits, the service deliberately finishes controller/filter/registration publication even if cancellation arrives before the provider returns. No cancellation check may pretend that committed writes were undone.

All view state is assigned before notifications; fresh tabs are registered only after successful persistence, and reimport preserves the context identity instead of appending. The shared service never selects a tab or saves writable-device preferences; WPF retains its explicit selection adapter. Notification/lease-release failures produce a published warning. `CaptureDataRebound` informs loaded-source lifetime owners when a view moves to active imported rows, releasing only obsolete non-current datasets while preserving other views/current loaded sources. Staging cleanup is owned-path-only, logged/retried if it fails, and shutdown drains both import/load services before the active DAL.

`xmat_import_saz` requires exact-active-source `CaptureImport`, exact-input `FileRead`, stable idempotency and separate hash/source/replacement approval. The approved scope is ALL active `WebProxyConnections` rows whose `DeviceName` equals `Fiddler - <input filename>`; an empty archive deletes that scope, including orphan rows. Other rows and loaded native sources are not changed. No raw/summary/export grant, capture/script execution or remembered writable device is implied, but existing active-source grants already cover newly imported data. Committed imports survive disconnect. Input/capability/source/context races, quota/deadline/cancel and post-commit draining use the common runtime.

### Completed parity adapters

`CaptureFileImports` is the focused shared SAZ/ETL preparation, target, transaction and publication workflow. Format adapters retain existing SAZ core behavior and use the offline ETL event reader without starting tracing or requesting elevation. ETL native callbacks validate fragment bounds, retain delegates for the complete native read, use system-converted UTC timestamps, and stop cooperatively on cancellation/failure. Complete bounded event staging and packet-model preparation precede atomic DAL replacement; original IDs/views survive failed preparation/transactions. Committed work drains publication. Clear uses `ICaptureDataOperations`, explicit stopped writable targets, atomic row deletion and complete HTTP/WebSocket/packet publication; read-only view close uses Navigation authority with desktop-equivalent retention/deletion semantics.

Investigation result ownership now retains original snapshots/private raw detail state across source replacement. Exact-source raw permission, connection/result ownership, record/finding identity, bounded previews/chunks, reader/export pinning and revoke invalidation apply to every detail read. WebSockets are frozen controller/session data with explicit source/capture epochs and honest saved-source unavailability. Completed desktop/agent reports retain all-console canonical provenance and artifact manifests; export uses the existing report serializer/manifest validation with pinned atomic new-directory publication, not a rerun or current-selection copy.

`DesktopActions` stores server-owned provenance-bound navigation targets and explicit lifecycle plans. Trusted MainWindow callbacks reveal only validated exact live records or frozen report windows; normal close preserves capture recovery and unsaved-query checks. Elevated restart requests deliver operator instructions without automatic UAC or claiming a restart occurred. Host policy uses shared normalization/revisions and intentionally persists application-session edits across disconnect; preferences use typed validation, guards and atomic settings persistence, including truthful committed-but-unsaved outcomes. Diagnostics expose bounded state and approved typed logs/public DER/PEM exports, never private keys or implicit trust creation.

`CaptureAppModel.Sql.cs` owns a lazy SQL workspace with per-document generation/run/export/drain lifetime. AvalonEdit 6.3.1.120 (MIT notice distributed) adds local schema/keyword/function assistance and selection execution. Result grids page the complete ordinal store in 1,000-row pages; full/selected CSV/JSON export uses immutable `IQueryResult`, not another SQL run. SQL history is session-only. Dirty-close preparation does not dispose results if another close check aborts. Confirmed application shutdown drains SQL/investigation windows and owned stores before closing the DAL; ordinary agent disable preserves desktop report provenance.

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
