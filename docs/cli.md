# Command line

[Back to README](../README.md)

Xbox Multiplayer Analysis Tool includes a command-line tool, `xmat.exe`, that runs its capabilities without the GUI,
for example from scripts or CI. It ships next to `XboxMultiplayerAnalysisTool.exe`: the CI build artifact contains both,
and so does the build output of the `XMAT.Cli` project. Like the GUI, it needs x64 Windows with the .NET 10 Desktop
Runtime and ASP.NET Core Runtime; without them the .NET loader fails before xmat runs, so there is no JSON output and
none of the exit codes below. Where the GUI and `xmat.exe` share a capability, such as Xbox Live analysis, it is
implemented once as a UI-free operation under `XMAT/Operations` and both are thin adapters over it, so a fix applies to
both. Other capabilities move over time; the [capability table](#what-runs-headless) shows which ones run headless.

## Using xmat.exe

```
xmat analyze INPUT --out DIR [--device ID] [--fail-on findings] [--max-input-mib N] [--max-data-mib N]
             [--max-record-mib N] [--max-records N] [--process NAME_OR_PID]... [--exclude-process NAME_OR_PID]...
xmat export INPUT --out FILE [--format xmatcap|saz|sanitized-saz] [--device ID] [--process NAME_OR_PID]...
            [--exclude-process NAME_OR_PID]... [--host PATTERN]... [--exclude-host PATTERN]...
            [--host-mode glob|exact] [--status VALUE]... [--method VALUE]... [--match-none]
            [--max-input-mib N] [--max-data-mib N] [--max-record-mib N] [--max-records N]
xmat doctor --for analyze
xmat help [COMMAND]
xmat --version
```

- `xmat analyze` reads a Fiddler archive (`.saz`) or an XMAT capture (`.xmatcap`), recognized by its content, and runs
  the Xbox Live service call rules on it. It writes `summary.json` (schema `xmat-analysis/1`) to `DIR`, which must not
  exist yet: xmat prepares the results in a private `.xmat-staging-<run id>` folder next to `DIR` and renames that
  folder to `DIR` only when the run completes successfully, so `DIR` is either complete or absent. Only the user who
  ran xmat can open it, so `DIR` must be on a file system with Windows permissions, such as NTFS: xmat reads the
  staging folder's permissions back before it writes anything and otherwise refuses (`cli.output.unprotected`, exit
  code 6); FAT and exFAT drives and some network shares do not keep permissions. `--device` limits a capture of several
  devices to one, by the id (`d1`, `d2`, ...) that an earlier run reported. With `--fail-on findings`, xmat exits with
  code 10 when the rules report findings (the results are still written).
- xmat treats its input as untrusted. By default it refuses an input (exit code 3) larger than 512 MiB
  (`analysis.input.too-large`), or one with more than 100,000 requests (a Fiddler archive) or rows in its device and
  connection tables together (an XMAT capture), an archive entry or row over 128 MiB, or more than 256 MiB of data in
  total, unzipped or read (`analysis.input.limit`); the options below change these four limits. These other limits
  are fixed or follow the allowed requests (`analysis.input.limit`): for a Fiddler archive, more than 300,064 archive
  entries of any kind by default (three per allowed request plus 64, and never more than 750,064), a central directory
  over 32 MiB by default (32 MiB per 100,000 allowed requests, at least 32 MiB and at most 80 MiB; the entry count and
  the directory size are checked before anything is unzipped), or a request or response whose headers exceed 16,384
  lines (malformed lines included) or 16 MiB (its first line, the header lines and the empty or whitespace-only line
  that ends them, with their line breaks, but not its body); for an XMAT capture, a database schema with more than 16
  entries, an entry over 64 KiB, more than 256 KiB in total, more than 64 extra columns in a table, or more than 512
  pages, 16 b-tree levels below its root or 256 overflow pages for an entry to read (these and a few other structural
  bounds, such as on the length of names, are checked before SQLite opens the file). The schema may define only the
  tables CaptureDevices, WebProxyConnections and NetworkTracePackets, each with its row id and its known columns in
  their order and with their types, then extra columns that are untyped, TEXT or INTEGER and declare nothing else; any
  other schema object, such as another table, an index, a view or a trigger, and any constraint, default or generated
  column are rejected (`analysis.input.schema`). So is a table or column name that is an SQLite keyword unless it is
  quoted, a rule stricter than SQLite's own. xmat needs CaptureDevices and WebProxyConnections and does not read
  NetworkTracePackets. Every column of both tables counts: 8 bytes for each row's id and each integer value, and the
  UTF-8 length of each text value; a column of any other type is rejected too (`analysis.input.schema`). An extra
  column may be NULL, which counts nothing; a NULL in a known column is rejected (`analysis.input.malformed`).
  WebProxyConnections may also hold, among its extra columns, the optional client process columns that XMAT writes
  (`ClientProcessName` TEXT, `ClientProcessId` INTEGER, `ClientProcessStartTime` TEXT and `ClientProcessStatus` TEXT;
  see [Capture files](capture-files.md#process-information)) and its optional response columns (`ResponseTrailers`
  TEXT and `ResponseBodyTruncatedAt` INTEGER; see [Capture files](capture-files.md#streamed-responses)). One of them
  spelled or typed otherwise is rejected (`analysis.input.schema`), but a NULL or unreadable value in them is read as
  missing, never rejected: a session without usable process information counts as unknown, as in a capture saved
  without these columns, and one without response columns has no trailers and a complete body. A Fiddler
  archive whose directory records are missing, inconsistent or span several disks is rejected too
  (`analysis.input.malformed`); single-disk ZIP64 archives are supported. These limits bound the capture data that
  xmat reads, not its exact memory use, and how many calls fit depends on the data: typical Xbox Live traffic stores
  about 5.6 KB per call, so about 45,000 calls fit in the default 256 MiB. For a capture that only these limits
  refuse, raise them, up to their maximums, or use the GUI; the GUI applies the header and schema checks too, but they
  do not bound how much memory it uses. HTTP bodies are analyzed as stored: compressed or chunked bodies are never
  decoded. A malformed HTTP header line in a Fiddler archive is skipped instead of refusing the archive, and the run
  reports the warning `analysis.input.malformed-header` once. The header limits cover the sanitized exports that were
  tested, not every archive that can be written: XMAT can record messages whose headers exceed them and export them in a
  plain or sanitized Fiddler archive; xmat refuses such an archive (`analysis.input.limit`) and the GUI does not import
  it, rather than truncating it. A request without a usable `Host` header (missing, empty, whitespace-only or on a
  malformed line) is kept and analyzed with an empty host: xmat reports it as an unknown service (`service-<n>`), and
  the GUI's Block and Bypass actions do not apply to it. A frame whose non-empty request line or status line has no
  second field (the URL or the status code) is skipped like an incomplete frame, one without its request or its
  metadata, with the warning `analysis.input.incomplete-frame`. The other frames are still analyzed; when no frame is
  left, there is nothing to analyze (`analysis.no-data`, exit code 3) and nothing is published.
- Before hashing or reading an XMAT capture, xmat opens the same guarded file identity that SQLite reads immutable.
  A `-journal`, `-wal` or `-shm` file or a WAL-mode database is refused as
  `analysis.input.live-database`; a location for which Windows cannot provide a normalized final path is refused as
  `analysis.input.location-unsupported`; and a file already open for writing is
  `analysis.input.unreadable` (all exit code 3). Sidecars created after admission are never consumed. The SHA-256 and
  analyzed rows come from that one guarded file even if an alias is retargeted.
- `--max-input-mib`, `--max-data-mib`, `--max-record-mib` and `--max-records` set the four input limits of one run, in
  MiB (1,048,576 bytes) or records: the input's size (512 by default, at most 2,048), the data that xmat holds in total
  (256 by default, at most 512), one archive entry or row (128 by default, or the data limit when that is lower; at
  most 128, and never more than `--max-data-mib`) and the requests or rows (100,000 by default, at most 250,000). Each
  takes a whole number of ASCII digits from 1 to its maximum, at most once; any other value, or a record limit above
  the data limit, is a usage error (`cli.usage.invalid-value`, or `cli.usage.duplicate-option` for a repeated option;
  exit code 2) whose message names the option, with its range for an invalid value or both options for a record limit
  above the data limit, but never the value. A lower `--max-data-mib` alone also lowers the record limit; a
  `--max-record-mib` that was given is never lowered. `--max-records` is an upper bound, not a guaranteed capacity: a
  Fiddler archive with fewer requests can still exceed the data limit or the archive limits above. The JSON output and
  `summary.json` report the effective values in `limits`. An XMAT capture's file may be as large as `--max-input-mib`,
  and SQLite may read values as large as `--max-record-mib` plus 1 MiB. Raising the limits lets xmat read larger
  captures, but it needs more memory and time: in testing with the limits at their maximums, xmat used up to about
  2.4 GiB of memory and took up to about 28 seconds on the test machine, depending on its load. These are observations,
  not bounds. The maximums, like the 750,064-entry and 80 MiB archive ceilings above, are policy ceilings, not a
  guaranteed capacity or a bound on memory use, so still run xmat with time and memory limits. The limits do not
  change the rule budget below, so a larger capture can still stop with `analysis.rules.work-limit`, nor the fixed
  limits above.
- An XMAT capture stores each session's request and response bodies in its row as base64, which takes 4 bytes for every
  3 (a 32 MiB body takes about 42.7 MiB), so its rows count about a third more than their bodies against the record
  and data limits. With the default limits, xmat reads any single row that XMAT writes: the proxy reads request bodies
  of at most 32 MiB, and a capture keeps at most the capture limit of each response body (see
  [Streaming and large responses](web-proxy.md#streaming-and-large-responses)), so a row stays under 128 MiB. The
  limits on the whole capture still apply: 512 MiB of file, 256 MiB of data, which three rows with the largest bodies
  already exceed, and 100,000 rows. Raise them with the options above, up to their maximums, or use the GUI. Reading a
  capture is not the same as analyzing it: a capture that xmat reads can be exported, but `xmat analyze` can still stop
  at the rule budget below (`analysis.rules.work-limit`).
- `--process` and `--exclude-process` choose the sessions to analyze by the client process that sent them, with the
  same rules as the process filter of a capture tab in the GUI. Each takes one selector and may be given again, up to
  64 times each: an image name such as `game.exe` or `game*.exe` (`*` and `?` are wildcards, case does not matter and
  `.exe` is optional) or a process id such as `4321`. A session is analyzed when it matches a `--process` selector (or
  there is none) and no `--exclude-process` selector. Sessions without process information, such as those in captures
  recorded before XMAT stored it, in Fiddler archives without `x-ProcessInfo` and from Xbox consoles or other remote
  devices, are unknown: they never match a selector, so `--process` leaves them out and `--exclude-process` alone keeps
  them. The selectors only choose what is analyzed and reported; the input is never changed. An empty selector, one
  longer than 260 characters or with a path separator, a colon or a control character, a process id of 0 and more than
  64 selectors for one option are usage errors (`cli.usage.invalid-value`, exit code 2) whose message never repeats the
  value; the other options still take one value (`cli.usage.duplicate-option`). When the selectors leave no session,
  there is nothing to analyze (`analysis.no-data`, exit code 3), and the JSON output still reports the counts.
- `xmat export` writes the sessions that its selectors choose to a new `FILE`, through the operation that the GUI's
  exports use: an XMAT capture (`.xmatcap`, with the original rows, ids and any extra columns), a Fiddler archive
  (`.saz`) or, only with `--format sanitized-saz`, a sanitized Fiddler archive. The format otherwise follows `FILE`'s
  extension, and one that does not agree with it is a usage error. It reads `INPUT` with `analyze`'s reader and
  limits and never changes it, but equal limits do not mean equal codes: the reader's diagnostics keep their
  `analysis.*` codes, except that an input over a limit is `export.input.limit`, and session data that cannot be
  exported is `export.input.invalid-http` (an HTTP message that a sanitized archive cannot rewrite) or
  `export.input.invalid-body` (a stored body or value that is not valid data), all with exit code 3. `--process`
  and `--exclude-process` work as for `analyze`; `--host` and
  `--exclude-host` take a host or host/path pattern (as the Block and Bypass lists do, the default `--host-mode glob`)
  or, with `--host-mode exact`, the host exactly as the capture list shows it; `--status` and `--method` take exact
  values. A glob compares the host and, for a pattern with a path, the path and query as the proxy forwards them
  (canonical: `/a/../b` is `/b`). Either the captured Host or the host the proxy connects to can match, the latter in
  its network spelling: the target's host for an absolute target (`GET http://host/path`), otherwise the Host, with
  IDN names in `xn--` form and IPv4 in dotted decimal (`0177.0.0.1` also matches `127.0.0.1`). Exact mode ignores
  these spellings, and exported rows keep their captured values. Each of these is repeatable, and a session must
  match every option that was given and no exclusion; glob
  hosts, like process selectors, are limited to 64 values of 260 characters each, while exact hosts, statuses and
  methods are literal sets of any size. An empty `--status=`, `--method=` or exact `--host=` selects the sessions
  shown without one, and `--match-none` selects nothing, so the JSON shows the counts without writing a file. xmat
  never replaces a file: an existing `FILE`, or `FILE` naming the input, is a conflict (`export.output.exists`,
  `export.output.is-source`, exit code 5), and so is a destination that another program keeps locked while xmat
  publishes (`export.output.locked`). The file is staged privately, in a folder next to it that only the current
  user can open, and published whole with that access. Failure or cancellation before publication leaves no output;
  a cleanup failure after publication exits with code 7 and can still report `export.output.published` as `true`. Where
  Windows cannot restrict access to that folder (storage without Windows permissions, such as FAT drives or some network
  shares), xmat publishes nothing (`export.output.unprotected`, exit code 6). A selection with no session to write
  publishes nothing (`export.no-eligible-rows`, exit code 3). An `.xmatcap` export also publishes nothing
  (`export.write.failed`, exit code 6) when its staged file, in the folder next to `FILE`, would have a path longer
  than Windows' 260-character limit; choose a shorter folder. `.saz` outputs are not affected.
- Exact rule evaluation has one work and live-memory budget shared across all selected devices, consoles, endpoints
  and rules, and never samples, truncates or publishes partial findings. Analysis uses safety budgets, not a fixed
  duration limit. In testing, one regular 1 Hz polling key completed at 75 minutes but exceeded the budget at
  90 minutes and two hours. Budget exhaustion fails analysis with `analysis.rules.work-limit` (CLI exit 3, no
  results published; GUI error). A pattern in a hand-edited API map or rules file that the linear-time regular
  expression engine cannot run, such as one with a backreference, falls back to the backtracking engine: one such
  match taking more than 100 ms, or such matches taking more than 5 seconds in total in a run, also fails analysis
  with `analysis.rules.work-limit`. These two limits measure time, so whether a run reaches them depends on the
  machine and its load; the installed patterns never fall back. The budget bounds rule evaluation, not xmat's total
  time or memory use, so in automation run xmat with time and memory limits.
- Neither the JSON output nor `analyze`'s published files contain captured content (hosts, URLs, headers, bodies, Xbox
  user ids, IP addresses, device names or client process names and ids) or local paths such as the input's; the only
  paths in them are artifact paths relative to `DIR`, such as `summary.json`. They hold rule names, levels and counts,
  service labels, ordinal device and console ids, the SHA-256 of the input and the validated `--process` and
  `--exclude-process` selectors. The input is never modified. An exported `FILE` holds the selected sessions
  themselves, so treat it like the capture it came from.
- `xmat doctor --for analyze` checks what `xmat analyze` needs and writes nothing: the analysis rules (`data.rules`),
  the API map (`data.apimap`) and the English strings (`localization.en-us`) installed next to `xmat.exe`, and a 64-bit
  process (`runtime.64-bit`). A missing or damaged file fails its check with a code such as `analysis.assets.rules` and
  exits with code 4, and `xmat analyze` reports the same code as its error and exits with code 4 before it creates
  anything.
- Commands, options, values and doctor targets that xmat does not know, including those planned for later releases,
  are usage errors (exit code 2). Error messages name only commands, options and values that xmat knows: an unknown
  word on the command line is never repeated, since it could be a path or a secret.
- `xmat.exe` never starts the GUI and never reads or writes the GUI's settings and logs under `%APPDATA%`. The options
  `--proxy-guard` and `--telemetry-sample` are reserved for the GUI and rejected (exit code 2).
- Ctrl+C or Ctrl+Break cancels a run. xmat checks for cancellation while it works and just before each attempt to
  publish the results, which is the single rename of the staging folder to `DIR`. A run cancelled at one of these
  checks publishes nothing, removes its staging folder and exits with code 130. An attempt that has passed its check
  can still complete if Ctrl+C arrives at that moment: `DIR` is then complete and the run reports its result as usual
  (exit code 0 or 10). A run whose analysis had already ended with an error keeps that error's exit code, and if the
  staging folder cannot be removed the exit code is 7, whatever else happened. A second Ctrl+C ends xmat at once.
  Closing the console window, logging off or shutting down cancels too, but Windows ends xmat a few seconds later. If
  xmat is killed, delete the `.xmat-staging-*` folder it leaves next to `DIR`.

## Output

While standard output is open, every run that xmat completes writes exactly one JSON document (schema `xmat-cli/1`)
to it, and standard error carries diagnostics without captured data. For example, from PowerShell:

```powershell
$result = .\xmat.exe analyze capture.saz --out results | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { $result.errors | Format-Table code, message }
```

An abridged result:

```json
{
  "schema": "xmat-cli/1",
  "command": "analyze",
  "runId": "4f1c0e2a9b7d4c3e8a5f6b7c8d9e0f1a",
  "status": "succeeded",
  "exitCode": 0,
  "input": { "kind": "saz", "sha256": "..." },
  "limits": { "maxInputMib": 512, "maxDataMib": 256, "maxRecordMib": 32, "maxRecords": 100000 },
  "processFilter": { "applied": true, "include": [], "exclude": [],
    "counts": { "total": 4, "included": 4, "hidden": 0, "unknown": 4 },
    "devices": [ { "id": "d1", "total": 4, "included": 4, "hidden": 0, "unknown": 4 } ] },
  "devices": [ { "id": "d1", "consoles": [ { "id": "c1", "calls": 4, "findings": [
    { "rule": "XR049Rule", "service": "presence_service", "level": "Warning", "count": 1 } ] } ] } ],
  "totals": { "calls": 4, "findings": 1 },
  "artifacts": [ { "kind": "summary", "path": "summary.json" } ],
  "cleanup": { "state": "completed" },
  "warnings": [],
  "errors": []
}
```

`status` is `succeeded`, `findingsFound`, `cancelled` or `failed`. `warnings` and `errors` hold stable codes, such as
`analysis.input.not-found`, with fixed messages, and `artifacts` lists what was published. xmat publishes only when the
run completes successfully: status `succeeded`, or `findingsFound` with `--fail-on findings`; otherwise `artifacts` is
empty. Once its command line is valid, `analyze` reports the effective input limits in `limits`, even when it fails,
and its process selection in `processFilter`: `include` and `exclude` hold the validated `--process` and
`--exclude-process` selectors (empty when none was given). Once xmat has read the sessions, `applied` is `true` and
`counts` holds the sessions read (`total`), analyzed (`included`) and left out (`hidden`), and how many of them had no
process information (`unknown`), with the same counts for each device in `devices`; before that, `applied` is `false`
and `counts` is `null`. `summary.json` holds `schema` (`xmat-analysis/1`), `command`, `runId`, `version`, the same
`input`, `limits`, `processFilter`, `devices` and `totals`, and the run's `warnings`. Instead of the analysis fields,
`doctor` reports `target`, `checks` (each with its `id`, `status` and, when it failed, its `code`), `environment`
(`framework`, the .NET runtime, and `architecture`, the process architecture such as `x64`) and `capabilities` (each
with its `id`, `status` (`supported`, `planned`, `guiOnly` or `policyException`) and `slice`); `help` reports
`commands`, each with its `name`, `status` (`supported`), `usage` and `capabilities` (the ids of the capabilities that
the command provides); and `--version` reports `version`.

`export` reports an `export` block and keeps `artifacts` empty: `format`; `input` (`kind` and `sha256`); `limits`;
`selection`, which counts instead of repeating the values: `device`, `host` (`mode`, and how many `include` and
`exclude` values were given, `include` being `null` without `--host`), `statuses` and `methods` (counts, or `null` when
not given) and `matchNone`; `processFilter` as for `analyze`; `counts` with `total`, `matched` (by the filters),
`selected`, `eligible`, `exported`, `unavailable`, `skippedIncomplete` and `noRecordedResponse` (sessions without a
recorded response, part of `skippedIncomplete`), and the same counts per device in `devices`; `sanitization`
(`redactedValues`, `omittedBodies`, `omittedHeaders`) for a sanitized archive; and `output` (`published`, `bytes`,
`sha256`). Fields that the run did not reach are `null`.

If xmat cannot produce this document (an internal error), it writes a fixed fallback document instead, with only
`schema`, `command`, `runId`, `status`, `exitCode`, `artifacts`, `cleanup`, empty `warnings` and the error
`cli.internal.output`. The run keeps its exit code: 7 stays 7, and a run that published `DIR` keeps 0 or 10 and lists
its artifacts, so its results are in `summary.json`. Only a run that published nothing and would have succeeded, such
as `help`, `--version` or a passing `doctor`, exits with code 70 instead.

## Exit codes

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 2 | Usage error: an unknown command, option or doctor target (including those planned for later releases), a missing or invalid value, or no folder to create `--out` in |
| 3 | Invalid input or nothing to analyze or export: the input is missing, unreadable, unsupported, corrupt or over a safety limit, `--device` names no device, the process selectors leave no session, or `export` has no session to write |
| 4 | A prerequisite is missing or damaged, such as the analysis data next to `xmat.exe` |
| 5 | Conflict: `--out` already exists, or `export`'s `--out` names the input or is locked by another program (`export.output.locked`) |
| 6 | The operation failed, for example the output could not be written, or `--out` is on storage without Windows permissions (`cli.output.unprotected`; `export.output.unprotected` for `export`) |
| 7 | Cleanup failed: a staging folder could not be removed (overrides the other codes, cancellation included) |
| 10 | `--fail-on findings` and the rules reported findings |
| 70 | Internal error, for example a process selection that does not match the analyzed sessions (`analysis.process-filter.consistency`), or a `help`, `--version` or passing `doctor` run whose JSON document could not be produced (`cli.internal.output`) |
| 130 | Cancelled before the results were published |

## Compatibility

- The JSON on standard output (`xmat-cli/1`) and `summary.json` (`xmat-analysis/1`) only grow within their version: a
  release may add fields, diagnostic codes, commands and capabilities, so ignore what you do not know. A change that
  removes or renames a field, changes its type or meaning, or changes what a status value means ships as a new schema
  version (`xmat-cli/2`, `xmat-analysis/2`) together with migration guidance in this document.
- Exit codes keep their meaning: new codes may be added, but a code is never renumbered or reused.
- The command lines of available commands keep working.
- Intentional diagnostic changes from the earlier headless draft: XMAT capture sidecars and WAL mode now report
  `analysis.input.live-database`, unsupported normalized-path locations report
  `analysis.input.location-unsupported`, and writer sharing violations report `analysis.input.unreadable`; each is
  invalid input with exit code 3.

## What runs headless

The capability registry (`XMAT/Operations/Capabilities/CapabilityRegistry.cs`) classifies every GUI entry point as
**Supported** (available in `xmat.exe`), **Planned** (with the delivery slice and its owner; it has no command line
until it is available), **GUI only** (presentation only) or **GUI only by policy** (a reviewed exception with its
slice, owner and reason; today only deleting sessions, which stays a confirmed GUI action). Tests fail when a GUI
command, dialog, device or capture type, analyzer or preference is not classified, when non-UI code starts using
GUI-only APIs such as message boxes or the dispatcher, or when the table below no longer matches the registry. See
[Headless Parity](../CONTRIBUTING.md#headless-parity) in CONTRIBUTING.md. The first capture slice (S4) keeps WebSocket
handshakes and reports the frames it observed without their payloads; archiving the payloads is planned separately.

<!-- xmat-capabilities:begin -->
| Capability | Command line | Headless |
|---|---|---|
| Analyze with the LTA rule set, custom rules or other thresholds (`analysis.compatibility`) | (not available yet) | Planned (S1b, XMAT-021) |
| Write the analysis HTML report (`analysis.html-report`) | (not available yet) | Planned (S3, XMAT-008) |
| Analyze network traces (`analysis.network-trace`) | (not available yet) | Planned (S8b, XMAT-021) |
| Analyze Xbox Live service calls (`analysis.xbox-live`) | `xmat analyze INPUT --out DIR [--device ID] [--fail-on findings] [--max-input-mib N] [--max-data-mib N] [--max-record-mib N] [--max-records N] [--process NAME_OR_PID]... [--exclude-process NAME_OR_PID]...` | Supported (S1) |
| Export sessions as Fiddler archives (.saz) (`capture-file.export-saz`) | `xmat export INPUT --out FILE.saz` | Supported (S2) |
| Export sessions as an XMAT capture (.xmatcap) (`capture-file.export-xmatcap`) | `xmat export INPUT --out FILE [--format xmatcap\|saz\|sanitized-saz] [--device ID] [--process NAME_OR_PID]... [--exclude-process NAME_OR_PID]... [--host PATTERN]... [--exclude-host PATTERN]... [--host-mode glob\|exact] [--status VALUE]... [--method VALUE]... [--match-none] [--max-input-mib N] [--max-data-mib N] [--max-record-mib N] [--max-records N]` | Supported (S10) |
| Read network traces (.etl) (`capture-file.import-etl`) | (not available yet) | Planned (S8b, XMAT-021) |
| Read Fiddler archives (.saz) (`capture-file.import-saz`) | `xmat analyze INPUT.saz --out DIR` | Supported (S1) |
| Read XMAT capture files (.xmatcap) (`capture-file.read-xmatcap`) | `xmat analyze INPUT.xmatcap --out DIR` | Supported (S1) |
| Export sessions as a sanitized Fiddler archive (`capture-file.sanitize`) | `xmat export INPUT --out FILE.saz --format sanitized-saz` | Supported (S2) |
| Save captures (.xmatcap) (`capture-file.save-xmatcap`) | (not available yet) | Planned (S5, XMAT-021) |
| Choose the device and capture method (`capture.add-device`) | (not available yet) | Planned (S5, XMAT-021) |
| Clear captured data (`capture.clear`) | (not available yet) | Planned (S8a, XMAT-021) |
| Delete sessions from a capture (`capture.delete-sessions`) | (none) | GUI only by policy (S11, XMAT). Policy exception: deleting captured sessions is irreversible, so it stays a confirmed action in the capture list; headless runs export the sessions to keep instead |
| Capture an explicit-proxy (generic) device (`capture.generic`) | (not available yet) | Planned (S5, XMAT-021) |
| Block or bypass hosts during capture (`capture.host-lists`) | (not available yet) | Planned (S8a, XMAT-021) |
| Capture this PC (`capture.local-pc`) | (not available yet) | Planned (S6, XMAT-021) |
| Capture low-level network traces (`capture.network-trace`) | (not available yet) | Planned (S8b, XMAT-021) |
| Filter captured sessions by client process (`capture.process-filter`) | `xmat analyze INPUT --out DIR [--process NAME_OR_PID]... [--exclude-process NAME_OR_PID]...`<br>`xmat export INPUT --out FILE [--process NAME_OR_PID]... [--exclude-process NAME_OR_PID]...` | Supported (S9) |
| Stream responses and limit how much of each response body a capture keeps (`capture.response-streaming`) | (not available yet) | Planned (S8a, XMAT-021) |
| Run capture scripts (`capture.scripts`) | (not available yet) | Planned (S8a, XMAT-021) |
| Stop a running capture (`capture.stop`) | (not available yet) | Planned (S5, XMAT-021) |
| Record WebSocket handshakes and report observed frames without their payloads (`capture.websocket-handshakes`) | (not available yet) | Planned (S4, XMAT-021) |
| Archive WebSocket message payloads (`capture.websocket-payloads`) | (not available yet) | Planned (S8a, XMAT-021) |
| Capture an Xbox development kit (`capture.xbox`) | (not available yet) | Planned (S7, XMAT-021) |
| Show or export the existing root certificate; never creates one (`certificate.export`) | (not available yet) | Planned (S5, XMAT-021) |
| Check what a command needs before running it (`cli.doctor`) | `xmat doctor --for analyze` | Supported (S1) |
| Ping an Xbox development kit or read its status (read-only) (`device.status`) | (not available yet) | Planned (S7, XMAT-021) |
| Collect debug logs (`diagnostics.logs`) | (not available yet) | Planned (S8a, XMAT-021) |
| Show GDK for Xbox details (`environment.gdk`) | (not available yet) | Planned (S7, XMAT-021) |
| Disable a device's proxy: stop an owned run or recover an attributable one, never a blind reset (`proxy.disable`) | (not available yet) | Planned (S6, XMAT-021) |
| Enable a device's proxy for a bounded foreground session (`proxy.enable`) | (not available yet) | Planned (S6, XMAT-021) |
| Restore an Xbox's proxy when capture ends (`proxy.on-exit`) | (not available yet) | Planned (S7, XMAT-021) |
| Choose the proxy port (`proxy.port`) | (not available yet) | Planned (S5, XMAT-021) |
| Recover proxy settings after a crash (`proxy.recover`) | (not available yet) | Planned (S6, XMAT-021) |
| Check a device's proxy status (`proxy.status`) | (not available yet) | Planned (S6, XMAT-021) |
| List, inspect, recover or clean up headless runs (`run.records`) | (not available yet) | Planned (S4, XMAT-021) |
| Validate a capture script without running it (`script.validate`) | (not available yet) | Planned (S8a, XMAT-021) |
| Report navigation and analysis tabs (`view.analysis-navigation`) | (none) | GUI only. Presentation: moves between report views and tabs |
| Capture viewers (`view.capture-viewers`) | (none) | GUI only. Presentation: filters, timeline and the request, response, WebSocket and packet views |
| Control templates (`view.controls`) | (none) | GUI only. Presentation: scroll bar commands inside control templates |
| Display language (`view.language`) | (none) | GUI only. Presentation: the command line emits stable codes and English diagnostics |
| Main window (`view.main-window`) | (none) | GUI only. Presentation: hosts the GUI adapters of every capability |
| Preferences window (`view.preferences-editor`) | (none) | GUI only. Presentation: edits saved GUI preferences; headless runs take per-run options instead |
| Script editor (`view.script-editor`) | (none) | GUI only. Presentation: editor buffer, save, revert and error navigation; headless scripts are files |
| Light and dark theme (`view.theme`) | (none) | GUI only. Presentation: colors only |
| Choose the PC address a console sends traffic to (`xbox.source-address`) | (not available yet) | Planned (S7, XMAT-021) |
<!-- xmat-capabilities:end -->
