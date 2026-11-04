# Local Agent Access

[Back to README](../README.md)

For offline analysis of capture files without a desktop instance, use [the standalone command line](cli.md). For connected agent control of a running desktop, use the MCP interface below. Both analysis routes share the UI-free capture analysis operation; MCP additionally enforces its connection-bound grants, approvals and result ownership.

The agent-access interface exposes device creation/removal and configuration inspection, capture/source metadata, bounded search, built-in analysis, read-only SQL, CSV/JSON result downloads, approved native capture loading and SAZ import, script editing/validation/approved activation, explicit Web Proxy/Network Trace start-stop, recovery, and operation status through the Model Context Protocol (MCP). It uses the official MCP SDK inside the running desktop app and a packaged `AgentBridge\XMAT.AgentBridge.exe` stdio companion. The companion forwards messages over a local, current-user named pipe; it does not open capture databases or automate the UI.

1. Open **Actions > Agent Access** and enable local access. It is off by default. XMAT and the agent/companion must run as the same Windows user at the same elevation level; XMAT does not silently elevate the companion.
2. Copy the generated configuration into your MCP client's configuration. The copied JSON uses the Copilot CLI-compatible `mcpServers` shape; adapt it for clients with a different configuration format. It targets this specific running XMAT instance and must be regenerated after XMAT restarts. Do not commit machine-specific configuration.
3. Connect the client and call `xmat_get_session`. In XMAT, select the session and the relevant capture/source, choose the permissions to grant, then select **Apply selected grants**. Edits affect application-wide permissions and the selected capture, source, folder and input only; grants on other targets are preserved. Repeat for additional targets to authorize multi-device capture or active/loaded comparisons. To revoke individual permissions, uncheck them and apply. **Granted targets** also lists retained sources no longer in the source catalog; select one and choose **Revoke selected target** to remove all of that target's grants without disconnecting. Revocation can stop its agent-owned work or discard its results. **Revoke and disconnect** removes all grants. A declared client name is not a verified identity. Agents cannot grant permissions or approve their own requests.
4. Use `xmat_list_captures` for IDs and current revisions. `xmat_set_capture_running` requires an explicit capture ID, expected revision, desired running state, and an idempotency key. Retrying the same request/key returns the original operation rather than starting it twice. Sensitive effects wait for an exact-request desktop approval; poll `xmat_get_operation` and then `xmat_get_result`. Acceptance is not completion.

Responses are bounded to 100 rows / 64 KiB by default, with opaque, session-bound continuation cursors where applicable. Metadata access does not permit arbitrary SQL or raw payloads. Metadata can still identify devices and services, and an external agent may send returned information to its model provider.

### Adding and managing devices

Use `xmat_describe_devices` with application summary permission for supported device choices. Grant **adding devices** separately, then submit `xmat_add_device`, for example:

```json
{
  "kind": "XboxConsole",
  "captureType": "WebProxy",
  "address": "console.example",
  "idempotencyKey": "add-console-1"
}
```

Other choices are `LocalPC`, `DefaultXboxConsole`, `GenericProxyDevice`, and `WindowsHandheldOrRemoteDevice`; only the explicit `XboxConsole` choice accepts an address. Manual devices support Web Proxy only. Xbox creation retains the desktop's GDK prerequisite. A default-console request is approved for its exact resolved address and fails if that selection changes before execution.

Every add request needs desktop approval. Existing device identity/deduplication rules, controller initialization, port reservations, DAL registration and settings serialization are shared with the desktop. Adding **does not start capture, configure/reboot a device, enable scripts, or grant access to the new capture**. Get the `captureId` and revision from the completed operation result; grant capture/script/raw permissions separately. Created contexts remain after disconnect.

`xmat_get_device` returns parameters, current and pending port reservations, and operation availability. `inspectProxy: true` reads supported device proxy status with a bounded wait, without writes. Unknown/unreachable state is not treated as disabled; private configuration/error text is excluded. Status is a point-in-time observation, not proof that an external device cannot change later.

Grant the selected capture's separate **device proxy configuration and removal** permission for `xmat_set_pending_proxy_port` or `xmat_remove_device`. Both require its exact revision, a stable idempotency key, and desktop approval. Port drafts reserve the new port while retaining the current reservation; omitted/null `port` or the current port discards the draft. Save failure rolls back the draft/reservations. Drafts remain on disconnect and **never apply the port, start capture, or configure a device**; application is a separate desktop or approved agent action.

Agent removal requires a stopped writable context without unresolved recovery or transitions. An owned, unknown or unreachable proxy endpoint blocks removal; resolve it separately. Removal shares the existing close/drain and registration workflow. Web Proxy close clears that context's HTTP/WebSocket data; Network Trace packet rows and loaded files are retained. Manual-device configuration cannot be verified and remains the operator's responsibility. Add/removal results distinguish the committed change from a successful settings save: `PersistenceFailed` includes a protected receipt with `changed`/`removed` and `persisted: false`, rather than pretending that nothing happened.

### Configuring device proxying and applying ports

`xmat_get_proxy_options` lists the capture's assigned local source addresses and revision. It requires application summary permission. `addressOffset` continues a bounded address list; check its fingerprint if the machine's addresses change. This discovery does not grant configuration consent.

`xmat_set_device_proxy` takes `captureId`, `expectedRevision`, explicit `enabled`, and an `idempotencyKey`. Enabling selects a `sourceAddress` from the options (Local PC defaults to `127.0.0.1`); disabling omits it. Both require **DeviceConfiguration and CaptureControl** for that exact capture, plus desktop approval. Already-enabled C# requires separate ScriptExecute permission and exact source approval.

Enabling starts/reuses the existing listener and configures supported PC/Xbox devices. Xbox configuration uses the existing certificate/address copy/removal and restart workflow; the approval names the resolved console, observed endpoint, desired address/ports and reboot effects. Foreign endpoints are not silently overwritten by enabling. An explicitly approved Xbox Disable can remove an unknown/foreign current setting; it is not limited to a setting created by this tab.

Use `xmat_apply_proxy_port` with the capture/revision, an assigned `sourceAddress`, and an idempotency key to apply an existing stopped capture's reserved draft. It shares the desktop bind, disable/readback, asynchronous saved commit, enable/readback, and rollback workflow without clearing history or requeueing another port. Current/pending reservations are retained on a failed save; after a committed port change, an uncertain enable retains the new listener/reservation for recovery.

Poll the operation/result rather than treating acceptance or an SDK return as completion. Results report `configurationWriteRequested`, `configurationConfirmed`, `portCommitted`, `persisted`, the unfinished phase, and actual listener/port state. Supported-device disable retires the listener only after readback confirms it no longer uses the endpoint. Readback waits are bounded; SDK writes drain cooperatively and cannot be assumed undone by cancellation. Uncertain/rebooting console state remains RecoveryRequired instead of triggering an automatic extra write or silent stop on disconnect.

Console resolution, selected assigned source address, capture revision and observed configuration are rechecked after approval. These are point-in-time checks, not atomic control over external actors changing a device. Manual devices are not programmatically configured or verified: the operator must confirm external endpoint updates before approving port application, and results explicitly report manual setup with `configurationConfirmed: false`. No UAC or arbitrary filesystem/shell access is added.

### Searching and running built-in analysis

Grant application summary permission for source/analyzer discovery (`xmat_list_sources`, `xmat_list_analyzers`), then separately grant **search and analysis summaries** for the selected source. This source grant does not permit raw SQL, export, headers/bodies, URI paths/query strings, or free-form rule messages.

Call `xmat_search_captures`, for example:

```json
{
  "sourceId": "<source ID from xmat_list_sources>",
  "kind": "WebProxy",
  "idempotencyKey": "throttled-requests-1",
  "filter": {
    "statusCode": 429,
    "minimumDurationMilliseconds": 100
  }
}
```

Filters can select an exact device, inclusive `afterUtc` / exclusive `beforeUtc`, HTTP host/method/status/minimum duration, or packet protocol/source/destination. `kind` is `WebProxy` or `NetworkTrace`; incompatible filter combinations are rejected. Stop captures before creating the snapshot. The selected desktop tab and hidden grid filters do not redirect agent operations.

Poll `xmat_get_operation`, then use `xmat_get_result` for the frozen `resultId`. `xmat_get_data_page` pages the `Records`, `Metrics`, or `Findings` section, each with its own continuation cursor. Search returns controlled record metadata and statistics, including nearest-rank HTTP p50/p95 durations. Missing or malformed values are counted or left unknown, not invented.

Use the same arguments with `xmat_run_analysis` to run the existing Xbox Live rules or packet-statistics analyzer. Findings include stable rule IDs, severity/counts, and run-scoped console/endpoint IDs; Xbox Live results cover every console, not only the first WPF report. `xmat_get_finding_evidence` pages supporting record references tied to that result's source/snapshot. Synthetic telemetry references its originating captured row when available. Legacy packet threshold metrics report unavailable per-packet evidence explicitly. Full raw evidence/report documents are not exposed through these tools; a later SQL run creates a different snapshot.

Analysis uses the shared operation lease and analyzer core, does not create WPF reports or write report files, and preserves the desktop's filtered packet-analysis behavior. Cancellation/deadlines are cooperative: a running non-interruptible rule drains before releasing the analysis lease. Packet statistics use supported IPv4 decoding; unsupported headers are unknown. Matching payload pairs are a heuristic, not proof of retransmission or loss, and HTTP request duration is not network RTT.

The desktop data limits also apply to search/analysis input spools and serialized output, with a separate combined retained/running-result count (four by default). These are not a strict process-heap limit. Use `xmat_release_data_result` when finished; source-summary revocation/disconnect discards those results without changing captured data or desktop reports. Captured metadata is untrusted evidence and may still identify devices, services, or users.

### Querying capture data

Grant summary access for `xmat_list_sources` and `xmat_describe_schema`. Sources are the active database and the single currently loaded capture database, identified independently of the selected tab. Schema discovery lists actual tables, fields, SQLite declarations, and example queries. Stop captures before discovering schema or creating a query snapshot; XMAT never silently stops another capture.

Arbitrary SQL needs **raw data and read-only SQL** permission for the exact source, even for an aggregate. For example, after discovering the source ID:

```json
{
  "sourceId": "<source ID from xmat_list_sources>",
  "sql": "SELECT RequestHost, RequestStatus, COUNT(*) AS Calls FROM WebProxyConnections GROUP BY RequestHost, RequestStatus ORDER BY Calls DESC",
  "idempotencyKey": "endpoint-status-counts-1"
}
```

Submit those arguments to `xmat_run_query`, poll `xmat_get_operation`, then use `xmat_get_result` for the completed `resultId` or protected query error details. `xmat_get_query_page` reads that immutable result using `nextCursor`; pages never rerun SQL. Types, duplicate/empty column labels, NULL, and ordinals are preserved. Large text/BLOB cells have marked previews and byte lengths; use `xmat_get_query_value` with the row/column ordinals and byte offset for exact base64 byte chunks. Reassemble UTF-8 bytes before decoding. Existing body/payload fields that store base64 text remain base64 text.

Only one read-only result-producing statement is accepted. Writes, batches, transactions, `ATTACH`, `PRAGMA`, and extension loading are rejected. Queries use stopped-capture snapshots through the existing application/data-layer interfaces, not another database connection in the MCP tools.

Defaults are a 60-second snapshot/execution deadline, a 512 MiB result-store limit, and four retained/running query results per connection. The desktop can change these limits; individual query arguments can only lower them. Snapshots and SQLite scratch space require additional disk space. `xmat_release_query_result` frees a result without removing capture data. Source replacement does not change completed results, but new queries require the new source's own grant.

### Exporting query results

Enable the source's separate **CSV/JSON result downloads** permission. Call `xmat_export_query_result` with `resultId`, `format` (`Csv` or `Json`), and a stable `idempotencyKey`. The exact result/format requires desktop approval, including the unsanitized-data and spreadsheet-formula warning. Poll the operation and obtain `artifactId` from `xmat_get_result`; acceptance or approval alone is not completion.

Download using `xmat_get_query_export_chunk`, advancing `byteOffset` by the decoded chunk length until `hasMore` is false. Chunks are base64 bytes and exports are UTF-8. Export includes **all stored rows**, not just the visible page, without rerunning SQL. CSV quotes text/headers and uses invariant numbers and base64 BLOBs. JSON has ordered `columns`, row-value arrays, `rowCount`, and `complete`; BLOBs and nonfinite REALs use explicit `$type` objects, so duplicate labels and SQLite value types are not lost.

XMAT creates only private temporary artifacts; these tools do not accept a server-side destination path or grant arbitrary filesystem access. Each artifact has the same approved byte/deadline limits as a query, with a separate retained-artifact count (four by default). Output expansion can exceed the export budget even when the query result store fits. Use `xmat_release_query_export` to free downloads. Current raw/export grants and connection ownership are checked on every read. Revocation/disconnect cleans temporary results/artifacts; regranting access does not resurrect them. Hard process termination can leave temporary files; these files are not encrypted by XMAT.

These exports are **not sanitized**. They can contain credentials, tokens, bodies, and personal data. CSV quoting does not neutralize formulas interpreted by spreadsheet software. Raw-data permission and local transport do not prevent an external agent from saving or forwarding returned bytes.

### Saving query results in an approved folder

In **Agent Access**, choose an ordinary local export folder, select **Allow new exports in this folder**, and choose **Apply selected grants**. This folder grant is separate from the query source's raw/export permissions. The agent cannot choose or grant a folder itself. Call `xmat_list_export_folders` for the granted `folderId`; `offset`/`nextOffset` page the list.

Submit `xmat_save_query_result` with the completed owned `resultId`, `folderId`, one `fileName` ending in the matching `.csv`/`.json`, `format` (`Csv`/`Json`), and a stable `idempotencyKey`. Desktop approval shows the exact folder, filename, format, frozen row count and privacy/formula warnings. The complete stored result is pinned and serialized through the same exporter, without rerunning SQL or depending on the selected tab/page.

Only **new files** are supported here. Existing destinations, subdirectory/relative paths, alternate streams, reserved device names, junction/reparse paths, UNC/mapped/network roots and mismatched extensions are rejected. Roots must be ordinary local NTFS/ReFS directories. XMAT pins their directory identities during publication and uses an atomic create-if-absent rename of its owned temporary handle, so a file created after approval is never overwritten. Loaded source paths are protected.

The existing timeout/output quotas apply. Grants and cancellation are checked immediately before publication; unpublished temporary data is removed using its owned handle. Completed results distinguish `published` from an accepted request. A completed user file **remains after revoke/disconnect**; cancellation cannot undo a publication that already committed. The same policy supports the capture/script exports below, not generic filesystem access. Capture imports, report exports, overwrite and network-folder support are still pending. Same-privilege code on the machine is not sandboxed by this interface.

### Saving captures and SAZ exports

Grant the source's separate **capture archive exports**, its raw-data permission, and the selected export folder's FileWrite permission. Call `xmat_save_capture` with `sourceId`, `format` (`CaptureDatabase`, `FiddlerSaz`, or `SanitizedFiddlerSaz`), `folderId`, a new matching `.xmatcap`/`.saz` filename, and a stable idempotency key.

Native save includes **the entire exact active or currently loaded capture database**, including device registrations and capture tables. Loaded-native save uses the existing read-only DAL connection's backup capability, not a file copy or a new connection opened by the agent. Its original capture file is not modified, and active-source grants do not authorize a loaded-source backup. SAZ reads that same explicit source; omit `deviceName` for all Web Proxy rows or provide an exact device name to filter it. No selected device, desktop tab or grid filter is inherited. Native database saves reject device filters.

`FiddlerSaz` is raw and may include credentials, headers and bodies. `SanitizedFiddlerSaz` uses the existing sanitizer and returns `sanitization` counts for sessions, redacted values, omitted headers/bodies and work-budget omissions. It is **not anonymization or a guarantee that all private data was removed**: host names, timings and allowed content may still identify users/services. Raw-source/export/folder permission and exact desktop approval are required even for sanitized SAZ. The receipt names its source/device scope and `sanitized` status explicitly.

All captures must be stopped. The shared stopped-data lease binds the source while the DAL backup or existing SAZ serializer creates a private complete archive. Native backup is not forcibly cancellable: cancellation waits for that phase to drain, then prevents publication. Later capture changes do not alter the staged archive. Output limits/deadlines apply; staging can require additional temporary disk space and is not encrypted.

The approved-folder handle policy then publishes the complete archive as a new file, without overwriting a destination or changing selected tabs/sources. Source replacement, revocation and invalid destinations fail safely. Published files remain on disconnect, and replay returns the original operation rather than creating a second export. Desktop Save/raw Export also use these shared staging services, retaining their file-picker overwrite behavior; their commands now require stopped capture data for a consistent archive.

### Operational boundaries

The typed surface covers capture/device control, native/SAZ/ETL workflows, SQL, structured and raw investigation, reports, scripts, host policy, preferences, diagnostics, and explicit desktop/lifecycle requests. Query/capture/script/diagnostic/report outputs use approved ordinary local roots and new-file/new-directory publication. This is the final fail-closed agent path policy: no arbitrary files, agent overwrite, nested arbitrary destinations, UNC/mapped roots or reparse traversal. Human SQL exports retain their separate file-picker overwrite/privacy consent. No SQL writes or generic property/reflection/shell endpoint is exposed.

Platform prerequisites remain real: capture hardware/GDK/elevation must already be available; public-certificate export requires initialized desktop proxy state; an elevated restart request does not trigger UAC or promise a successful restart. Normal close still honors unsaved SQL, capture recovery, exports and running work.

Web Proxy actions can change connectivity and Windows proxy configuration. Starting a listener does not automatically configure or reboot an Xbox/manual device. Starting a capture with already-enabled C# scripts requires a separate capture-scoped script-execution grant and approval of the current source; scripts execute trusted code in-process and are not sandboxed.

Disconnect/revoke cancels and drains agent work and attempts cleanup only for still-agent-owned resources. Human-owned/adopted work is not stopped. Xbox/manual listeners that might still be needed remain visible as **RecoveryRequired** rather than being silently disabled; resolve the device's configuration or explicitly take over/stop it in the desktop app. Cancellation cannot undo an already committed transition.

### Preparing and enabling scripts

Scripts target an explicit writable Web Proxy capture. Grant **Read script source and compile diagnostics** to discover `SslConnectionRequest`, `WebRequest`, and `WebResponse` through `xmat_list_scripts`, and inspect the existing `Params` globals/types through `xmat_describe_scripting`. Capture metadata and raw database permissions do not imply script-source access. Source and diagnostics may contain credentials or private text and may be forwarded by an external agent.

`xmat_read_script` returns bounded base64 UTF-8 chunks for a script ID and exact revision. Reassemble bytes before decoding; changed revisions are rejected rather than mixing source chunks. No server filename is exposed or accepted.

With the separate **Edit scripts** grant, submit `xmat_update_script` with capture/script IDs, `expectedRevision`, `expectedSourceHash`, complete replacement `source`, and a stable `idempotencyKey`. The desktop shows the exact diff and hashes for approval. Editing immediately invalidates/disables the old executable, including during capture, but **never enables the new text**. Drafts are preserved on disconnect.

Call `xmat_validate_script` for that exact revision/hash to compile without installing, enabling, or executing code. Poll the operation and use `xmat_get_result` for `validationId`, currentness/hash/revision, and diagnostic count. `xmat_get_script_diagnostics` pages warnings/errors with one-based locations; long messages are explicitly marked previews. Validation does not replace an already-enabled human script. Editing while validation runs makes that result stale.

Activating requires the separate **trusted C# activation and scripted capture** grant. Submit `xmat_set_script_enabled` with the exact IDs/revision/hash, explicit `enabled`, and an idempotency key. XMAT requires another desktop approval showing the exact source. Failed compilation leaves the script disabled and returns a protected diagnostic receipt, not a successful activation. Use the returned revision for subsequent actions; an edit-away-and-back cannot reuse an old approval even if its hash matches.

Compile-only validation and agent activation do not resolve `#load`/`#r` directives, preventing source-read permission from becoming arbitrary compiler file access. This restriction **is not a C# sandbox**: approved enabled code executes in-process for every proxied host of that controller and can access files, the network, and application internals. It can ignore cancellation; an in-flight callback may need to finish even after future execution is disabled. Normal trusted desktop activation retains its existing compiler settings.

Only newly committed agent activations become owned resources. Already-enabled human scripts are not adopted. Revoke/disconnect disables future execution only when the exact activation epoch is still owned; human edits, disable/re-enable, and explicit **Take Over** are preserved. Starting capture remains a separate action. Another agent cannot overwrite/control an owned script merely by borrowing its ID.

Compilation uses the desktop data-operation timeout/output ceiling, with a separate retained-validation limit (four by default). Incoming MCP messages still have the host's 1 MiB ceiling. `xmat_release_script_validation` frees diagnostic caches without editing source or disabling code. Source/diagnostics are protected on every read; ordinary runtime/audit status excludes their text. File saving and approved input loading are described below.

### Saving script source

Grant the capture's **Read script source** and separate **exact script-source file exports** permissions, plus FileWrite for a desktop-approved folder. Submit `xmat_save_script` with `captureId`, `scriptId`, `expectedRevision`, `expectedSourceHash`, `folderId`, a new `.txt` `fileName`, and a stable `idempotencyKey`. Desktop approval names the exact revision/hash and destination and warns that source may contain credentials or private text.

Saving writes the complete approved source as UTF-8 without a BOM, using the same serializer as the desktop editor. It **does not edit, compile, enable or execute code**, require ScriptEdit/ScriptExecute, or change the editor's remembered filename. Capture/script identity, revision/hash, current grants and destination are rechecked before publication; edit-away-and-back and replaced script objects invalidate the pending approval. Each file obeys the approved output/deadline limits, with a separate running script-file limit matching the session's result count.

Poll `xmat_get_operation` and `xmat_get_result` for publication, bytes, revision and hash. Revocation/cancellation or a detected source change before commit discards only the unpublished temporary output. Published files remain on disconnect; retries return the original job even if a later human draft changed. Export permissions do not grant loading, arbitrary filenames/paths or overwrite access.

### Loading scripts from approved files

In **Agent Access**, choose a script input `.txt` file, select **Allow this exact input file**, and choose **Apply selected grants**. Choosing a file alone does not grant it. This is a separate exact-input `FileRead` permission, not access to its directory. Only ordinary local, non-reparse NTFS/ReFS files are supported; UNC/mapped/device paths and arbitrary agent-provided paths are rejected. The catalog binds the selected file and parent identities, byte length and SHA-256. A different identity or content hash requires a new selection/grant.

`xmat_list_input_files` lists only granted input metadata using `offset`/`nextOffset`, without raw content or absolute paths. Submit `xmat_load_script` with `inputId`, `captureId`, `scriptId`, `expectedRevision`, `expectedSourceHash`, and a stable `idempotencyKey`. The target needs separate ScriptRead and ScriptEdit permissions. Desktop approval shows the input's original-byte hash, the BOM-decoded source hash and the exact source diff. Input text is untrusted data, never approval authority.

XMAT pins the ordinary source handle against writes/replacement while hashing and decoding, then rechecks the input and target after approval. Loading uses the same BOM-aware decoder and compare-and-set edit core as desktop Load/Revert. Changed source invalidates/disables the old executable; an identical no-op preserves existing state. Loading **never compiles, enables or executes code**, changes the selected tab, or rebinds the editor's remembered filename. Capture files are not accepted by this script-input workflow.

File verification/reads use the session's data timeout and original/decoded-byte ceilings, sharing running file-operation admission with script exports. Source and diff text can be retained in memory; these byte ceilings are not a hard process-heap limit. Cancellation, revoked grants, changed/replaced input or stale target prevent an edit from committing. A committed draft remains after disconnect, just like an approved source edit; replay returns the prior operation rather than rereading changed input. No generic file-read or file-registration tool is exposed.

### Loading native captures from approved files

Choose a `.xmatcap` input in **Agent Access** and grant both **Allow this exact input file** and **Allow approved capture-file loading**. Registration alone does not grant either permission. `xmat_list_input_files` identifies the granted input's `CaptureDatabase` kind, original byte length and SHA-256; script inputs cannot be used as captures.

Submit `xmat_load_capture` with `inputId`, a stable `idempotencyKey`, and `expectedLoadedSourceId` from `xmat_list_sources`. Omit/null that last value only when no source is loaded. Desktop approval binds the reviewed file hash and current source. Source identity and the read-only view registry are rechecked after approval and immediately before commit. No arbitrary path or SQLite connection is accepted.

Loading shares **File > Load**'s application service: copy verified bytes to private storage, open through the DAL, validate devices/schema and prepare every view off-thread, then publish the source and views together on the dispatcher. A failed, cancelled or stale preparation leaves existing data/views unchanged. Matching read-only device contexts are reused without appending duplicate packets; unmatched older views retain their original datasets. Active writable data/captures are not changed, no capture or script is started, and the agent does not select a tab. Desktop File > Load retains its own explicit selection behavior.

The completed receipt returns the new `sourceId`, added/updated context counts, `published`, and `viewsNotified`. A view-notification failure is reported as a **committed warning**, not an invalid load or automatic rollback. Loading does **not grant summary/raw/export access** to the new source; grant those separately before search or SQL. Loaded captures remain after agent disconnect. Retrying the same key returns the original job even if the original input later changes.

One native load or SAZ import may prepare per application at a time; preparation prevents app close but does not hold the global data lease or block an unrelated capture stop. Agent copy/preparation phases use the session's data deadline and per-input byte ceiling. Provider calls drain cooperatively, not by forced thread termination. Private database copies and decoded view models add disk/memory usage beyond the input ceiling; older copies remain until their last retained view closes and they are no longer the current source. Normal shutdown drains/releases owned copies; interrupted shutdown or filesystem cleanup failure can leave unencrypted temporary data. SAZ import is described below; ETL and multi-file reports remain separate pending workflows.

### Importing SAZ into the active source

Choose a `.saz` in **Agent Access**, grant **Allow this exact input file**, select the **active** source, and separately grant **Allow approved SAZ imports into this active source**. This source-scoped `CaptureImport` grant is independent of RawDataRead, CaptureLoad, device control and output folders. Input selection alone grants nothing.

Submit `xmat_import_saz` with `inputId`, the active `sourceId`, and a stable `idempotencyKey`. Desktop approval names the file/hash, source, and exact replacement scope: **all active WebProxy rows whose DeviceName equals `Fiddler - <input filename>`**. Orphaned matching rows are included. An empty archive replaces that scope with zero rows. The corresponding read-only view is added/reused; unrelated rows and the current loaded native source are unchanged. Writable contexts with that name are rejected rather than overwritten.

All captures must be stopped. Desktop **File > Import** and the agent share input staging, the existing SAZ decoder, prepared filters/models, atomic DAL replacement and dispatcher publication. Invalid archives, quota failures, cancellation before commit and failed transactions preserve the original rows/IDs/views. After the database commits, publication drains even if cancellation arrives; cancelling is not proof of undo. A notification failure is a published warning, not a failed/rolled-back import. The shared operation preserves tab selection; the desktop adapter retains its explicit selection of the imported tab.

Poll the operation/result for source/capture IDs, record count, `addedContext`, `published`, and `viewsNotified`. Imported views are read-only and are not remembered as writable devices. No capture/script starts or new data grants are added. **Existing summary/raw grants for the active source cover the imported records**, including grants held by another connected client; approve imports with that exposure in mind. Search/query/export uses that active source, not the independent loaded-native source. Committed data remains on disconnect; replay never imports a second time.

Agent limits apply separately to physical input bytes, expanded archive bytes (including a 256-byte entry envelope), and encoded stored-row bytes. Expansion/body/line/frame loops and transaction boundaries check cancellation; metadata rejects XML DTDs. These limits are not a hard process-heap/disk ceiling: private compressed staging and decoded models add overhead, and provider/parser phases drain cooperatively. Normal completion removes only the owned input workspace; failed cleanup is logged/retried on shutdown and can leave unencrypted temporary data. No archive entries are extracted to agent-selected paths.

### Offline ETL import and clearing

`xmat_import_etl` uses the same source-scoped CaptureImport/exact-input FileRead grants and approval/job workflow, with a desktop-selected `NetworkTraceEtl` input. It replaces only `NetworkTracePackets` rows named `NetTrace - <input filename>`, prepares every packet before atomic replacement, preserves unrelated data/loaded sources, and adds/reuses a read-only view without selecting it. A complete zero-packet file replaces that scope with zero rows. The shared offline reader does not run netsh, start/stop tracing, request elevation, or configure a device. Native fragment bounds and converted UTC timestamps are validated; callbacks/cancellation drain before publication or cleanup.

`xmat_clear_capture` requires exact CaptureControl, capture revision, idempotency and destructive approval. Stop the writable target first and resolve transitions/recovery; clearing atomically removes its matching rows and resets its HTTP/WebSocket/packet view, without changing scripts/settings/other contexts. This stopped-only rule prevents late capture callbacks from resurrecting deleted rows. The desktop Clear action uses the same service. `xmat_remove_device` also closes imported/loaded read-only views with separate application Navigation permission and exact approval; imported HTTP close follows existing desktop row-removal semantics, while original input files remain untouched.

### Raw evidence, WebSockets and completed reports

Safe search/analysis summaries remain unchanged. `xmat_get_data_details` and `xmat_get_data_detail_value` require the original result's exact-source RawDataRead in addition to its summary access. Details are tied to the original immutable snapshot, matching record/finding and connection; changing/replacing the app source cannot redirect them. Large text/BLOB values use marked previews and bounded exact chunks. Raw revocation invalidates retained details; regrant does not resurrect them. Detail messages and captured text stay out of ordinary operation/audit metadata.

`xmat_list_websocket_captures` reports actual controller/session availability and epochs. WebSocket inspection uses explicit registered capture/source scope and frozen message metadata/payloads, never the selected tab or a loaded-file fallback. These messages may not be persisted in capture files. Metadata is bounded; payloads require separate exact-source raw access.

`xmat_list_completed_reports`, `xmat_open_completed_report` and `xmat_get_report_manifest` use explicit immutable run/source/snapshot provenance, including retained desktop results. Full report details require raw access. `xmat_export_analysis_report` additionally requires AnalysisReportExport for that exact source, approved-folder FileWrite, and sensitive manifest/destination approval. Export consumes the completed report's all-console artifact manifest without rerunning analysis or using the currently selected report/source. Native publication remains pinned, atomic and new-directory-only; published user directories survive disconnect. Grant historical report sources explicitly through Agent Access when they are no longer the current loaded slot.

`xmat_prepare_data_navigation` produces a server-owned provenance-bound target for explicit reveal through `xmat_reveal_desktop_target`. Stale or unrelated live records are not selected by guessing row IDs; frozen completed reports can open in a dedicated report window. Navigation returns availability/action metadata, not a way around raw-data grants.

### Host policy, preferences, diagnostics and desktop requests

Grant application HostPolicy for `xmat_get_host_policy` / `xmat_update_host_policy`. Changes share the actual Block/Bypass normalization, revisions and desktop model, affect every relevant proxy view in this application session, and require exact change approval. They are not capture-file preferences and intentionally persist after agent disconnect; no automatic undo can overwrite a subsequent human edit.

Application Preferences permits `xmat_get_preferences` / `xmat_update_preferences` for typed supported port/proxy/analyzer/language/theme settings. The existing validation/reservations, dispatcher mutation and atomic settings persistence remain authoritative. Updates bind an expected revision and exact approved values, not arbitrary property names. Committed-but-unsaved outcomes are distinct from rollback; language changes can require a normal restart. Desktop preferences and theme toggles use the same operations.

Application Diagnostics permits bounded `xmat_get_diagnostics` and sensitive `xmat_export_diagnostics`. Export also requires an approved folder/new filename and exact approval; logs may contain credentials, captured data or private paths. Public certificate artifacts are DER/PEM **without private keys** and require an initialized desktop root; export never creates/installs trust. The desktop log/certificate commands share these typed serializers/publication rules.

Application Navigation permits `xmat_list_navigation_targets` / `xmat_reveal_desktop_target`; application Lifecycle permits `xmat_get_desktop_lifecycle` / `xmat_request_desktop_lifecycle` with mandatory approval and current guards. Normal close runs existing capture/recovery, dirty SQL and drain checks. Elevated-restart requests deliver trusted operator instructions; the agent does not silently launch UAC, bypass unsaved state or report an unverified restart as completed. After any restart, reconnect using fresh instance configuration.

### Network Trace control and recovery

Create the Local PC or Xbox Network Trace capture through the desktop device UI or approved `xmat_add_device`, then grant capture control for that exact capture. Use `xmat_get_capture_availability` (application summary permission) to inspect its current revision, prerequisites, reset effects, and recovery state before `xmat_set_capture_running`.

Local trace requires an **already elevated** XMAT process and matching-elevation client/companion. Agent calls do not trigger UAC, restart XMAT, or silently elevate `netsh`. The desktop Start action can offer its existing explicit elevation workflow before clearing packets. Regenerate instance configuration after a restart. Local commands use a unique owned ETW session name, not a global/default session; retries query only that name.

Network Trace Start requires desktop approval of clearing this capture's previous packet data. Prerequisite/connection failure does not clear it. Stop is not complete until the trace is stopped, downloaded, atomically published through the DAL, and disconnected. Captured timestamps retain the full UTC date. Downloads use private temporary JSONL staging; incomplete downloads are never published as successful results.

Failed/uncertain operations retain **RecoveryRequired**, a may-be-running indication, and the exact pending phase: Stop, Download, Publish, or Disconnect. Retry Stop to finish only that phase; it does not start again, duplicate a partial download, or erase already committed packets. The desktop keeps Stop/recovery controls available rather than reinitializing the controller and losing its handle. Temporary staging is removed on normal completion/failure; hard process termination may leave unencrypted temporary files.

Disconnect/revoke stops only an unchanged, safely owned Local PC trace and preserves its retrieved data. Xbox traces or uncertain sessions remain visible for human recovery rather than being silently stopped/rebooted. A remote response can be lost after its command commits, and the device portal may not provide enough identity to reconstruct that trace automatically.

After manually stopping or taking responsibility for external state, the desktop **Acknowledge manual recovery** action or approved `xmat_acknowledge_trace_recovery` request can drop the pending handle. This may abandon unretrieved events but retains captured database rows and trace files. Its result explicitly reports `traceStoppedByApp: false`; it is **not** verification that the external trace stopped. Do not approve it merely to hide an unknown active trace.
