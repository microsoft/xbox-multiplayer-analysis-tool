# Telemetry schema and sample generation

This documents the **implemented schema, version 1**, not a proposed schema. Names and enum values are
case-sensitive. The source of truth is `XMAT\Logic\Telemetry\TelemetryEvents.cs`, with common metadata
in `TelemetryEnvironmentProvider.cs`.

Ordinary builds do not export telemetry or read telemetry registry metadata. Approved telemetry-enabled
releases collect automatically; there is no end-user collection preference. An explicitly requested
**local sample run** records the same application events to a local file without creating a collector
reporter or requiring a collector key.

## Event names and semantics

The namespace is `Microsoft.Gaming.XMAT`. For example, the launch event is
`Microsoft.Gaming.XMAT.AppLaunched`. The following table lists the suffixes:

| Event | When emitted | Meaningful fields |
|---|---|---|
| `AppLaunched` | Once after telemetry initializes for an eligible launch | Common metadata and `SessionId` |
| `AppReady` | Once when the main window loads | `DurationMs`: elapsed time since telemetry service creation, not process creation |
| `AppClosed` | Normal telemetry shutdown, after capture/analyzer cleanup | `DurationMs`: elapsed session time |
| `FeatureUsed` | First use of each feature category in a session | `Feature`; startup theme/language also count as configuration adoption |
| `OperationStarted` | At a real operation boundary | `Operation`, `OperationId`, `Feature`, optional context and `EntryPoint` |
| `OperationCompleted` | Operation finishes, returns an outcome, or fails | Matching `OperationId`, `Outcome`, `ErrorCategory`, `DurationMs`, relevant count/size buckets |
| `OperationStage` | A timed stage ends or the containing operation completes | Matching `OperationId`, `Stage`, `DurationMs` for that stage |
| `CaptureFirstData` | First item observed during a new capture operation | Matching `OperationId`, capture context, `DurationMs` from capture start |
| `Counter` | An interaction, a cancellation, or a shutdown aggregate | See counter interpretation below |
| `SessionSummary` | Normal telemetry shutdown | `DurationMs`; `Count` is peak concurrent capture operations |
| `UnhandledFailure` | An existing application exception handler observes a failure | Fixed `ErrorCategory`; the same exception object is reported only once per session |

Capture starts include setup/connection time. Peak capture operations include attempts that may fail;
they are not a count of simultaneously successful captures. A proxy listener starting does not prove
traffic arrived: use `CaptureFirstData` or a non-empty completed capture.

Durations measure elapsed wall-clock intervals using a monotonic timer; they are not active interaction
time. Abrupt termination, offline delivery, or queue overflow can leave unmatched starts or missing exit
events. Missing `AppClosed` does not prove a crash. `UnhandledFailure` is an observed failure, not a
guaranteed fatal crash.

### Counter interpretation

Do not indiscriminately add all `Counter.Count` values together:

| Counter pattern | Interpretation |
|---|---|
| `Interaction != None` | Immediate list/script configuration action or script revert; `Count = 1` |
| `Feature = CaptureFiles`, `Outcome = Cancelled` | File dialog cancellation; `Operation` identifies the requested action, `Count = 1` |
| `Feature = GdkInfo`, `Outcome = Failed`, `ErrorCategory = MissingGdk` | GDK-info action found no usable installation, `Count = 1` |
| `Outcome = Failed`, script-hook `Feature`, `Interaction = None` | Aggregated script runtime failures, emitted at shutdown; currently uses `ErrorCategory = Unknown` |
| `Outcome = None`, `Interaction = None`, `Operation = None` | Feature usage-call total for the session, emitted at shutdown; includes the first use counted by `FeatureUsed` |

Configuration edits can contribute to both an immediate action counter and a feature usage-call total.
Use `FeatureUsed` for distinct-session adoption and the appropriate counter pattern for action totals.

## Common metadata

Common fields are resource attributes in the application. OneCollector moves `ext.*` fields into nested
Common Schema extensions; other resource fields become `data` attributes. Local samples preserve
resource keys literally in their `resources` object.

| Resource key | Type | Availability and meaning |
|---|---|---|
| `ext.app.id` | string | Fixed `XboxMultiplayerAnalysisTool` |
| `ext.app.ver` | string | Assembly informational version without `+` build metadata; `Unknown` if unavailable |
| `ext.os.name` | string | Fixed `Windows` |
| `ext.os.ver` | string | `Environment.OSVersion.Version` string |
| `BuildChannel` | string enum | `Local`, `Public`, `Internal`, `Test`; local samples always use `Local` |
| `ext.device.localId` | optional string | Host Windows `SQMClient\MachineId`, parsed as a GUID and formatted `s:<GUID>`; omitted when unavailable |
| `ext.xbl.sbx` | optional string | Host Windows XboxLive sandbox; `RETAIL` when absent/empty, `Unknown` for an invalid value; may be omitted after a registry-read failure |
| `MachineIdAvailable` | boolean | Whether `ext.device.localId` was included |

The machine ID is **persistent pseudonymous data**, not anonymous data and not a reliable user or
installation count. No fallback identifier is invented. The sandbox is the host Windows sandbox,
not necessarily the sandbox of a captured Xbox console. Sandbox values are limited to 128 characters
and cannot contain CR, LF, or NUL.

## Event attributes

All events share these fields. Unused enum fields use `None`, unused measurements use `0`, and
unavailable buckets use `Unknown`. Those defaults do **not** imply a successful outcome or a measured
zero for every field.

| Attribute | JSON / ingestion type | Presence | Meaning |
|---|---|---|---|
| `SchemaVersion` | integer / int | Always | Currently `1` |
| `SessionId` | string | Always | Fresh per-launch GUID, 32 hexadecimal characters without hyphens |
| `Feature` | string enum | Always | Fixed feature category |
| `Operation` | string enum | Always | Fixed operation category |
| `Outcome` | string enum | Always | `None`, `Succeeded`, `Failed`, `Cancelled`, `NoData`, `Unsupported` |
| `ErrorCategory` | string enum | Always | Fixed category, never the exception message/type/stack |
| `DurationMs` | integer / long | Always | Non-negative elapsed milliseconds; interpretation depends on event kind |
| `Count` | integer / long | Always | Non-negative count; meaningful for `Counter` and `SessionSummary` |
| `ItemCountBucket` | string enum | Always | Captured items, analyzer input count, or script compilation error count where applicable |
| `FileSizeBucket` | string enum | Always | Size bucket for a file operation where measurable |
| `Stage` | string enum | Always | Stage category for `OperationStage`, otherwise `None` |
| `EntryPoint` | string enum | Always | `None`, `Menu`, `DragDrop`; file actions distinguish menu from dropped files |
| `Interaction` | string enum | Always | `None`, `Added`, `Removed`, `Cleared`, `Enabled`, `Disabled`, `Reverted` |
| `OperationId` | optional string | Operation events | Fresh operation GUID in the same 32-character format; do not use captured request IDs |
| `CaptureMethod` | optional string enum | Context present | `WebProxy`, `NetworkTrace` |
| `DeviceType` | optional string enum | Context present | `LocalPC`, `XboxConsole`, `GenericProxyDevice`, `Unknown` |
| `CaptureOrigin` | optional string enum | Context present | `None`, `User`, `Restored`, `Loaded`, `Imported` |

An operation retains its own ID even when captures or analyses run concurrently. Pair by both
`SessionId` and `OperationId`; do not infer correlation from timestamp proximity.

`DeviceAdd` starts before the user has chosen a device, so its context can appear only on completion.
Restored/loaded/imported tabs do not count as new user device setup. Context is omitted for unrelated
operations. The collector does not receive console names, endpoint names, or device addresses.

Some completions carry `ItemCountBucket = Zero` as a default; only interpret that bucket for an
operation with a measured item count. `NoData` is meaningful for empty captures and unsuitable
analysis inputs. `StatisticsRender` measures WebView2 navigation, not proof every JavaScript graph
rendered successfully. Hidden Calls/CertWarnings reports are not counted as viewed.

### Operation values

`None`, `DeviceAdd`, `EnableProxy`, `DisableProxy`, `Capture`, `Analysis`, `LoadCaptures`,
`SaveCaptures`, `ImportSaz`, `ExportSaz`, `CompileScript`, `LoadScript`, `SaveScript`,
`CollectLogs`, `ExportRootCertificate`, `StatisticsRender`, `AnalyzerInitialize`.

`AnalyzerInitialize` also produces operation events during startup. Analysis kind is distinguished by
`Feature = XboxLiveAnalysis` or `NetworkTraceAnalysis`, not another arbitrary string field.

### Feature values

| Area | Values |
|---|---|
| Unspecified | `None` |
| Capture and analysis | `WebProxyCapture`, `NetworkTraceCapture`, `XboxLiveAnalysis`, `NetworkTraceAnalysis`, `CaptureFiles`, `ClearCaptures` |
| Request/packet inspection | `RequestHeaders`, `RequestBody`, `RequestJson`, `PacketData` |
| WebSockets | `WebSockets`, `WebSocketText`, `WebSocketJson`, `WebSocketHex` |
| Filters | `FilterStatus`, `FilterHost`, `FilterMethod`, `FilterProcess`, `FilterThread`, `FilterProtocol`, `FilterSourceAddress`, `FilterDestinationAddress`, `FilterWebSocketDirection`, `FilterWebSocketType` |
| Timeline | `TimelineWaterfall`, `TimelineByHost`, `TimelineSelection` |
| Reports | `PerEndpointReport`, `StatisticsReport`, `NetworkReport`, `ReportDrillDown`, `ReportRequestNavigation` |
| Scripting | `Scripting`, `SslScript`, `RequestScript`, `ResponseScript`, `ScriptLoad`, `ScriptSave`, `ScriptRevert` |
| Proxy configuration | `BlockList`, `BypassList` |
| Support/configuration | `DebugLogs`, `RootCertificate`, `GdkInfo`, `Preferences`, `LightTheme`, `DarkTheme`, `EnglishLanguage` |

Filter telemetry records only the category, never the filter value. Feature-use hooks exclude binding
initialization where appropriate; report/default selected-view inspection can be recorded when data is
actually displayed. The taxonomy is fixed; localization changes do not change event values.

### Stage values

`None`, `Connect`, `Start`, `Stop`, `Download`, `Disconnect`, `Convert`, `Validate`, `GenerateReport`.
Network acquisition uses the connection/start/stop/download/disconnect stages. Xbox Live analysis
uses conversion/validation/report generation; network analysis currently uses validation.

### Error values

| Value | Mapping or usage |
|---|---|
| `None` | No reported error category |
| `AccessDenied` | `UnauthorizedAccessException` |
| `Io` | Other `IOException` |
| `InvalidData` | JSON/format/invalid-data exceptions, or failed Statistics navigation |
| `InvalidOperation` | `InvalidOperationException` |
| `Cancelled` | `OperationCanceledException` |
| `Network` | Socket/HTTP request exceptions or failed proxy configuration |
| `MissingGdk` | Required GDK unavailable |
| `MissingDefaultConsole` | Device setup cannot find the default console |
| `AlreadyExists` | Duplicate device/proxy setup |
| `Configuration` | Reserved taxonomy value; no current producer |
| `Compilation` | Script compilation has errors |
| `Unknown` | Other or uncategorized failure |

Application errors keep their existing handling. Telemetry records only the category; it does not
serialize exception objects. Captured game/service HTTP failures are not automatically XMAT failures.

### Bucket boundaries

| `ItemCountBucket` | Count |
|---|---|
| `Unknown` | Unavailable; a negative input is used internally, never emitted as `Count` |
| `Zero` | 0 |
| `OneToTen` | 1-10 |
| `ElevenToHundred` | 11-100 |
| `HundredToThousand` | 101-1,000 |
| `ThousandToTenThousand` | 1,001-10,000 |
| `AboveTenThousand` | More than 10,000 |

| `FileSizeBucket` | Bytes |
|---|---|
| `Unknown` | Unavailable |
| `Empty` | 0 |
| `BelowOneMb` | 1-1,048,575 |
| `OneToTenMb` | 1,048,576-10,485,759 |
| `TenToHundredMb` | 10,485,760-104,857,599 |
| `HundredMbToGb` | 104,857,600-1,073,741,823 |
| `AboveOneGb` | 1,073,741,824 and above |

Names retain `Mb`/`Gb` for compatibility; boundaries use binary units (MiB/GiB).

## OneCollector wire format and registration

The pinned OneCollector exporter serializes Common Schema **`ver = "4.0"`** JSON records. That version is
the transport format, **not** XMAT's `data.SchemaVersion = 1`.

| Wire field | Type and meaning |
|---|---|
| `ver` | String `"4.0"` |
| `name` | Fully qualified event name |
| `time` | UTC event timestamp in ISO 8601 format |
| `iKey` | Collector tenant token derived by the exporter from the configured ingestion key; not an XMAT event attribute |
| `data` | XMAT event attributes and non-`ext.*` resource metadata |
| `data.severityText` | Exporter field, currently `"Information"` for XMAT events |
| `data.severityNumber` | Exporter field, currently `9` |
| `ext` | Nested common extensions such as `app`, `os`, `device`, and `xbl` |

For example, the literal resource key `ext.device.localId` becomes `ext.device.localId` through
nested JSON objects, and `BuildChannel`/`MachineIdAvailable` become `data` fields. Ambient activity,
baggage, scopes, exception content, and default environment/resource detectors are not forwarded.
XMAT uses explicit `data.SessionId`/`data.OperationId`, not `ext.dt.traceId`/`spanId`, for correlation.

This is an illustrative wire event with **synthetic identifiers**:

```json
{
  "ver": "4.0",
  "name": "Microsoft.Gaming.XMAT.AppLaunched",
  "time": "2026-01-01T12:00:00.0000000Z",
  "iKey": "o:00000000000000000000000000000000",
  "data": {
    "severityText": "Information",
    "severityNumber": 9,
    "SchemaVersion": 1,
    "SessionId": "22222222222222222222222222222222",
    "Feature": "None",
    "Operation": "None",
    "Outcome": "None",
    "ErrorCategory": "None",
    "DurationMs": 0,
    "Count": 0,
    "ItemCountBucket": "Unknown",
    "FileSizeBucket": "Unknown",
    "Stage": "None",
    "EntryPoint": "None",
    "Interaction": "None",
    "BuildChannel": "Test",
    "MachineIdAvailable": true
  },
  "ext": {
    "app": { "id": "XboxMultiplayerAnalysisTool", "ver": "0.3.0.0" },
    "os": { "name": "Windows", "ver": "10.0.26100.0" },
    "device": { "localId": "s:11111111-1111-1111-1111-111111111111" },
    "xbl": { "sbx": "SAMPLE_SANDBOX" }
  }
}
```

Register all 11 fully qualified event names with the shared attribute schema and optional fields above.
Collector table names may replace dots with underscores and change casing; confirm the actual ingestion
mapping with the data-source owner rather than assuming the original event name is the table name.
All telemetry-enabled builds still require a dedicated approved XMAT key, namespace, and privacy review.
Local samples do not satisfy those approval gates.

`XMAT\Data\TelemetryUsage.kql` expects an owner-defined normalized `XmatTelemetry` view:

| Normalized column | Collector source | Local-sample source |
|---|---|---|
| `Timestamp` | `time` | `time` |
| `EventName` | Suffix of `name` after `Microsoft.Gaming.XMAT.` | Same |
| `DeviceId` | `ext.device.localId` | `resources["ext.device.localId"]` |
| `AppVersion` | `ext.app.ver` | `resources["ext.app.ver"]` |
| `BuildChannel` | `data.BuildChannel` | `resources.BuildChannel` |
| `SessionId`, `Feature`, `Operation`, `Outcome`, `DurationMs`, `Count`, `Stage`, `CaptureOrigin` | Corresponding `data` field | Same |

The templates intentionally filter to `BuildChannel = Public`; local sample data will not appear in those
results unless you deliberately adapt a query for local testing. Never relabel local samples as production.

## Generate samples from the app without a collector

Prerequisites: Windows, the project's .NET 10 SDK, and a source checkout. Run these commands from the
repository root in PowerShell. The sample flag is available in **non-exporting builds only**.

```powershell
dotnet build XMAT\XboxMultiplayerAnalysisTool.csproj -c Debug -p:Platform=x64 `
  -p:EnableTelemetry=false -p:TelemetryApproved=false
if ($LASTEXITCODE -ne 0) { throw "XMAT build failed." }

$sampleDirectory = Join-Path $env:TEMP "XMAT-Telemetry-Samples"
New-Item -ItemType Directory -Path $sampleDirectory -Force | Out-Null
$sampleFile = Join-Path $sampleDirectory ("xmat-" + [Guid]::NewGuid().ToString("N") + ".jsonl")
$app = (Resolve-Path "XMAT\bin\x64\Debug\net10.0-windows10.0.17763.0\win-x64\XboxMultiplayerAnalysisTool.exe").Path

$process = Start-Process -FilePath $app `
  -ArgumentList "--telemetry-sample `"$sampleFile`"" -PassThru
Write-Output "Samples: $sampleFile"
```

This records **real events from that app launch**, not a fabricated event for every taxonomy value.
The output directory must already exist and be writable; the full output filename must be new.
Existing files are never overwritten or appended. An invalid request displays a failure dialog and
does not fall back to collector export. A telemetry-enabled build cannot use this flag.
Normal first-run certificate/setup prompts still apply; review them as you would for an ordinary XMAT run.

The telemetry path creates no HTTP client/collector and needs no instrumentation key. Other XMAT
features can still use the network or change device/proxy state; the sample flag does not disable
their normal behavior. Metadata is read only because you explicitly requested the local run.

### Exercise useful event categories

| Action in XMAT | Expected samples / cautions |
|---|---|
| Launch and leave the main window open | `AppLaunched`, analyzer initialization operations, `AppReady`, theme/language `FeatureUsed` |
| Open Preferences or GDK Info; switch dark/light theme | Corresponding `FeatureUsed`; a missing GDK can also produce an error counter. These actions retain normal settings behavior. |
| Open a capture-file dialog and cancel | `Counter` with `Feature = CaptureFiles`, requested `Operation`, `Outcome = Cancelled` |
| Add a Generic Device Web Proxy capture | `DeviceAdd` with user context on completion; cancel the selector to exercise `Cancelled` without changing a device |
| Start then stop that proxy with no client traffic | Capture start/completion with `Outcome = NoData`; starting a generic proxy does not automatically change the Windows system proxy |
| Use a disposable/test client through the proxy | `CaptureFirstData`, inspection/filter/timeline categories and a non-empty capture completion. Do not use confidential traffic. |
| Load a non-sensitive `.xmatcap` or import a test `.saz`; save/export | File operation events and size buckets; analysis afterward carries loaded/imported context |
| Analyze eligible test data and inspect Per Endpoint / Statistics | `Analysis`, timed stages, report usage and Statistics navigation outcome; drill down or navigate to a request for those feature categories |
| Enable a harmless test script, or deliberately use invalid syntax | Hook enable/disable interactions, `CompileScript` result and error-count bucket; script contents/diagnostics are not in samples |
| Add/remove/clear disposable block/bypass entries | Interaction counters; actual patterns are omitted from samples but still affect proxy behavior |
| Capture from a test Xbox/PC using Network Trace | Capture/stage events and `PacketData`/network filter categories; GDK/elevation/device requirements still apply |
| Exit XMAT normally after stopping captures | Feature totals, script-failure aggregates if any, `SessionSummary`, `AppClosed` |

Do not induce a crash merely to produce `UnhandledFailure`. The tests exercise it safely using fake
exceptions. WebSocket/report events require relevant data and visible views; launching the app alone
does not generate every event or feature.

After exiting normally, inspect the output:

```powershell
$process.WaitForExit()
$events = @(Get-Content -LiteralPath $sampleFile | ForEach-Object { $_ | ConvertFrom-Json })
$events | Group-Object name | Select-Object Name, Count
$events | Where-Object name -eq "Microsoft.Gaming.XMAT.OperationCompleted" |
  ForEach-Object { $_.data } |
  Format-Table Operation, Outcome, ErrorCategory, DurationMs, ItemCountBucket, FileSizeBucket
$events | Where-Object name -eq "Microsoft.Gaming.XMAT.SessionSummary" |
  ForEach-Object { $_.data }
```

The file is UTF-8 without a BOM, **one compact JSON object per line**, not a JSON array. It uses this
local envelope:

```json
{
  "name": "Microsoft.Gaming.XMAT.OperationCompleted",
  "time": "2026-01-01T12:00:01.0000000+00:00",
  "resources": {
    "ext.app.id": "XboxMultiplayerAnalysisTool",
    "ext.app.ver": "0.3.0.0",
    "ext.os.name": "Windows",
    "ext.os.ver": "10.0.26100.0",
    "BuildChannel": "Local",
    "ext.device.localId": "s:11111111-1111-1111-1111-111111111111",
    "ext.xbl.sbx": "SAMPLE_SANDBOX",
    "MachineIdAvailable": true
  },
  "data": {
    "SchemaVersion": 1,
    "SessionId": "22222222222222222222222222222222",
    "Feature": "WebProxyCapture",
    "Operation": "Capture",
    "Outcome": "NoData",
    "ErrorCategory": "None",
    "DurationMs": 1000,
    "Count": 0,
    "ItemCountBucket": "Zero",
    "FileSizeBucket": "Unknown",
    "Stage": "None",
    "EntryPoint": "None",
    "Interaction": "None",
    "OperationId": "33333333333333333333333333333333",
    "CaptureMethod": "WebProxy",
    "DeviceType": "GenericProxyDevice",
    "CaptureOrigin": "User"
  }
}
```

This readable envelope is **not a verbatim OneCollector HTTP payload**: common resources are flat,
there is no `ver`/`iKey` or exporter severity, and the timestamp is taken at local submission.
The event attributes are produced by the exact same `TelemetryEvent.Attributes` mapping.
For a real encoded collector envelope with synthetic metadata, run the loopback-only test:

```powershell
dotnet test XMAT.Tests\XMAT.Tests.csproj -c Debug -p:Platform=x64 -p:EnableTelemetry=false `
  --filter FullyQualifiedName~EncodedCollectorPayloadContainsOnlyExplicitFieldsAndNoAmbientIdentity `
  --logger "console;verbosity=detailed"
```

The test prints the decoded actual wire event to its test output and never contacts the production
collector. It validates that injected ambient user/host/activity values are absent.

### Sample safety, delivery, and troubleshooting

- Local samples include your real permitted machine ID and host sandbox when available. Before sharing
  the file, replace or remove those values if the recipient does not need them. The examples above use
  synthetic identifiers; do not commit real sample files.
- Captured traffic, URLs, addresses, account information, scripts, paths, exception messages, and stack
  traces are excluded from telemetry. Captures and debug/support logs are separate and may contain them.
- Sampling uses a bounded 256-event memory queue and a background file writer. Lines are flushed as
  written; normal shutdown waits up to two seconds. Forced termination or overflow can lose events.
- Initialization errors display a failure dialog. Runtime write/drop/shutdown failures produce sanitized
  `XMAT telemetry:` trace messages (`LocalSampleWriteFailed`, `LocalSampleDropped`,
  `LocalSampleShutdownTimedOut`); inspect debugger trace output if an expected line is missing.
- Running without `--telemetry-sample` restores ordinary non-exporting behavior automatically; nothing
  is saved in user preferences. Stop samples by closing that app launch. Delete only the specific sample
  file you generated when no longer needed.

## Optional approved test-collector workflow

If you need real ingestion instead of a local file, follow the approval/key/build instructions in
[CONTRIBUTING.md](CONTRIBUTING.md#telemetry-development-and-release-policy), use an approved **test**
data source with `TelemetryBuildChannel = Test`, and launch without the sample flag. Repeat the same
app actions, then inspect the destination owner's actual tables/mappings.

The test channel is a label, not an automatic switch to a different endpoint or safe data source.
The configured key determines routing. Collector registration, retention/access policy, and
backend enrichment verification remain outstanding release-owner tasks.
