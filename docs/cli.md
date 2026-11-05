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
             [--max-record-mib N] [--max-records N]
xmat replay INPUT --request SPEC --out DIR [--device ID] [--repeat N] [--parallel N] [--unconditional] [--verify]
            [--verify-diff] [--url URL | --origin URL] [--method METHOD] [--header "NAME: VALUE"]...
            [--remove-header NAME]... [--body-file FILE] [--follow-redirects N] [--timeout SECONDS]
            [--max-response-mib N] [--http-version auto|1.1|2] [--connect-to IP:PORT] [--allow-unsafe-methods]
            [--allow-origin-change] [--allow-connect-to] [--max-input-mib N] [--max-data-mib N] [--max-record-mib N]
            [--max-records N]
xmat compose --url URL --out DIR [--sequence START-END] [--method METHOD] [--header "NAME: VALUE"]...
             [--body-file FILE] [--repeat N] [--parallel N] [--follow-redirects N] [--timeout SECONDS]
             [--max-response-mib N] [--http-version auto|1.1|2] [--connect-to IP:PORT] [--allow-unsafe-methods]
             [--allow-connect-to]
xmat doctor --for analyze
xmat help [COMMAND]
xmat --version
```

- `xmat analyze` reads a Fiddler archive (`.saz`) or an XMAT capture (`.xmatcap`), recognized by its content, and runs
  the Xbox Live service call rules on it. It writes `summary.json` (schema `xmat-analysis/1`) to `DIR`, which must not
  exist yet: xmat prepares the results in a private `.xmat-staging-<run id>` folder next to `DIR` and renames that
  folder to `DIR` only when the run completes successfully, so `DIR` is either complete or absent. Only the user who
  ran xmat can open it, so `DIR` must be on a local NTFS volume and reached directly. Before it creates or reaches
  anything, xmat refuses (`cli.output.unprotected`, exit code 6) a network path, a mapped or substituted drive, and a
  junction, mounted folder or symbolic link in any folder above `DIR` (folders that OneDrive keeps online are fine);
  give the folder's real path instead. It then reads the staging folder's owner and permissions back before it writes
  anything, and refuses FAT and exFAT drives the same way. While it runs, xmat holds the staging folder and every folder
  above it open, so none of them can be renamed, replaced or turned into a link until the results are published or
  removed. `--device` limits a capture of several devices to one, by the id (`d1`, `d2`, ...) that an earlier run
  reported. With `--fail-on findings`, xmat exits with code 10 when the rules report findings (the results are still
  written).
- xmat treats its input as untrusted. By default it refuses an input (exit code 3) larger than 512 MiB
  (`analysis.input.too-large`), or one with more than 100,000 requests (a Fiddler archive) or rows in its device and
  connection tables together (an XMAT capture), an archive entry or row over 32 MiB, or more than 256 MiB of data in
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
  column may be NULL, which counts nothing; a NULL in a known column is rejected (`analysis.input.malformed`). A Fiddler
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
  (256 by default, at most 512), one archive entry or row (32 by default, at most 128, and never more than
  `--max-data-mib`) and the requests or rows (100,000 by default, at most 250,000). Each takes a whole number of ASCII
  digits from 1 to its maximum, at most once; any other value, or a record limit above the data limit, is a usage
  error (`cli.usage.invalid-value`, or `cli.usage.duplicate-option` for a repeated option; exit code 2) whose message
  names the option, with its range for an invalid value or both options for a record limit above the data limit, but
  never the value. `--max-records` is an upper bound, not a guaranteed capacity: a
  Fiddler archive with fewer requests can still exceed the data limit or the archive limits above. The JSON output and
  `summary.json` report the effective values in `limits`. An XMAT capture's file may be as large as `--max-input-mib`,
  and SQLite may read values as large as `--max-record-mib` plus 1 MiB. Raising the limits lets xmat read larger
  captures, but it needs more memory and time: in testing with the limits at their maximums, xmat used up to about
  2.4 GiB of memory and took up to about 28 seconds on the test machine, depending on its load. These are observations,
  not bounds. The maximums, like the 750,064-entry and 80 MiB archive ceilings above, are policy ceilings, not a
  guaranteed capacity or a bound on memory use, so still run xmat with time and memory limits. The limits do not
  change the rule budget below, so a larger capture can still stop with `analysis.rules.work-limit`, nor the fixed
  limits above.
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
- Neither the JSON output nor the published files of `xmat analyze` contain captured content (hosts, URLs, headers,
  bodies, Xbox user ids, IP addresses or device names) or local paths such as the input's; the only paths in them are
  artifact paths relative to `DIR`, such as `summary.json`. They hold rule names, levels and counts, service labels,
  ordinal device and console ids and the SHA-256 of the input. The input is never modified.
- `xmat replay` sends requests from a capture again, like Fiddler's Reissue, and `xmat compose` sends one new request,
  like Fiddler's Composer. Both send directly from this PC, the way the GUI's request composer does: never through a
  proxy (so XMAT's capture, scripts and host lists do not apply and the replays are not captured), without cookies of
  their own, without decompressing responses, and with Windows' certificate validation. `replay` reads `INPUT` exactly
  as `analyze` does, with the same limit options and `analysis.input.*` errors. `--request` selects requests by the
  number the GUI shows in its `#` column: `all`, or numbers and ranges such as `1,5,9-12` (at most 10,000 numbers). A
  number given on its own must exist (`replay.request.not-found`) and be an HTTP or HTTPS request without a protocol
  upgrade, recorded with its request body (`replay.request.unsupported`); a range or `all` skips CONNECT tunnels,
  WebSocket handshakes, replays that XMAT recorded without their request body (`X-XMAT-Request-Body-Not-Kept`) and
  other traffic with the warning `replay.request.skipped`. Unless `--request all`, a selected number that more than one
  request has is refused (`replay.request.ambiguous`), whether the requests are on one device or on several; in a
  capture of several devices, `--device` limits the selection to one of them. Each selected request is sent once per
  `--repeat` (1 by default, at most 1,000), all of them in turn before the next round, in capture order; `--parallel`
  sends up to that many at a time (1 by default, at most 16), and `--parallel` times `--max-response-mib` may not
  exceed 1,024 MiB. A run makes at most 10,000 sends (`replay.plan.too-large`), which xmat checks before it prepares
  any request, counting every selected request, including any that a range or `all` would skip; each send can
  follow up to `--follow-redirects` redirects, so with `--follow-redirects 10` a run makes at most 110,000 HTTP
  requests.
- Edits apply to every selected request before anything is sent: `--method`; `--header "NAME: VALUE"`, which replaces
  every header of that name or adds it (repeat it for more headers; the value must be printable ASCII or tabs, and
  `Host`, `Content-Length` and connection headers cannot be set); `--remove-header`; `--body-file`, whose bytes
  replace the body (at most 64 MiB, read before anything else happens); `--unconditional`, which removes `If-Match`,
  `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since` and `If-Range` so the server cannot answer 304 or 412;
  `--url`, which replaces the whole URL of a single selected request (`replay.edit.url-needs-one-request` otherwise);
  and `--origin`, which sends every selected request to another scheme, host and port with its path and query, for
  example to replay captured traffic against a local test server.
- Every selected request is checked before the first one is sent, and nothing is sent when one fails a check (exit
  code 3). A request that is not valid after the edits, for example one with a header value that is not ASCII, which
  the HTTP stack cannot send, fails with `replay.request.invalid`. Unless you allow it, xmat
  refuses to send a method other than GET, HEAD and OPTIONS, which can change data on the server
  (`replay.policy.unsafe-method`; `--allow-unsafe-methods` allows it), or captured headers, such as credentials, to
  another origin than the one they were captured for (`replay.policy.origin-change`; `--allow-origin-change`, which
  only `replay` takes, allows the origins that `--url` or `--origin` name). Redirects are returned, not followed,
  unless `--follow-redirects N` follows up to `N` of them (0 by default, at most 10), and only within the origin that
  was sent to: a 303 continues as GET (a HEAD stays a HEAD) and a 301 or 302 to a POST continues as GET, both without
  the body, as browsers do, while a 307 or 308 keeps the method and the body. A redirect to another origin is never
  followed, whatever the options, so a server never decides where the headers and body of a request go next; nor is
  a redirect from HTTPS to HTTP, to anything other than HTTP and HTTPS, to a URL with credentials, or without exactly
  one usable `Location`. With `--follow-redirects 0`, a redirect is the result as it is, without a code; otherwise a
  redirect that is not followed is the result, with the code `replay.redirect.origin-change`,
  `replay.redirect.insecure`, `replay.redirect.unsupported` or `replay.redirect.limit`.
- A request that carries one of Fiddler's control headers, `Fiddler-Host`, `X-OverrideHost`,
  `X-IgnoreCertCNMismatch` or `X-OverrideGateway`, is refused (`replay.request.control-header`): xmat never acts on
  a captured header, so a capture never chooses where its requests go or which certificate is accepted. Remove the
  header (`--remove-header`) to replay the request.
- `--http-version` chooses the HTTP version: `auto` (the default) sends HTTP/2 to an HTTPS server that offers it and
  HTTP/1.1 otherwise, as before; `1.1` and `2` ask for exactly that version, and a request that cannot be sent with it
  fails (`replay.send.failed`) rather than go out with another. `2` needs HTTPS URLs (`replay.http-version.https-required`).
  xmat never answers an authentication challenge, such as NTLM or Negotiate, with your Windows credentials: the
  challenge is the result.
- `--connect-to IP:PORT` connects to that endpoint instead of the address of the URL's host, as Fiddler's
  `Fiddler-Host` does, for example to send a request for a production host name to a test server. The endpoint is a
  numeric address and a port (`IPv4:PORT` or `[IPv6]:PORT`, without a zone; the port is 1 to 65535); a host name, a
  URL or another form is a usage error (exit code 2). The URL, the `Host` header, the TLS server name and the
  certificate check stay those of the URL, so the endpoint must present a certificate that is valid for the URL's
  host, and xmat never accepts another. It needs HTTPS URLs (`replay.connect-to.https-required`) that share one origin
  (`replay.connect-to.origin-count`), refuses unspecified, multicast and broadcast addresses
  (`replay.connect-to.target-denied`), and sends nothing without `--allow-connect-to` (`replay.policy.connect-to`),
  whatever the address: the requests, with their headers and body, go to that endpoint. Redirects within the origin
  go there too. When the endpoint cannot be reached, the send fails (`replay.send.failed`); xmat never falls back to
  the URL's host, a proxy or a name lookup.
- Each request waits for its response up to `--timeout` seconds (30 by default, at most 600) and keeps a body of up to
  `--max-response-mib` (16 by default, at most 512). HEAD, 1xx, 204 and 304 responses have no body, whatever their
  `Content-Length` says. A request without a complete response is a result too: its
  `outcome` is `failed` (`replay.send.failed`), `timedOut` (`replay.response.timeout`) or `tooLarge`
  (`replay.response.too-large`, which keeps the status and headers but not the body). Once every request was sent,
  xmat publishes `DIR` with `summary.json` (schema `xmat-replay/1`) and, for each send, `replays/NNNNN/request.http`
  (the request as xmat built it) and `replays/NNNNN/response.http` (the response, with the chunked framing removed)
  whatever the responses were. Unlike the output of `analyze`, these files hold captured content, including
  credentials and cookies from the capture: `DIR` is private to your account as for `analyze`, so keep it that way.
  The exit code is 12 when a request got no complete response; otherwise, with `--verify`, it is 11 when a response
  differs from the response captured for its request in its status code or body. Bodies are compared without their
  chunked framing and gzip, deflate or Brotli content coding, each decoded up to 64 MiB (or `--max-response-mib` if
  that is smaller); a body that does not decode within that size is compared as received. Headers are not compared,
  since dates, request ids and cookies differ on every response. A request is not verified when no response was
  captured for it (its captured status is not a number) or when the GUI recorded its captured response without its
  body (`X-XMAT-Response-Body-Not-Kept` on an empty body; the header on a body that was kept changes nothing): its
  `verify` is null and the run warns `replay.verify.skipped`. The JSON output and `summary.json` hold only request
  numbers, ordinal device ids, outcomes, status codes, durations, sizes, codes and the options that are numbers or
  switches, never URLs, methods, headers, bodies or the `--body-file` path. Cancelling a run publishes nothing, but
  requests already sent stay sent.
- `--verify-diff` verifies as `--verify` does (the verdicts and exit codes are the same) and also writes, for each
  verified send, `replays/NNNNN/diff.json`: how the replayed response differs from the captured one. It holds both
  status codes; for each side how its body was compared (`basis`: `plain`, `decoded`, or the stored bytes when the
  coding is not decoded, `unsupportedCoding`, `malformedCoding` or `decodedLimit`; `framing`; and `end`, whether
  decoded content is proven to be all of it, `complete`, `incomplete`, `unproven` or `notApplicable`); and the body
  differences as text (hunks of lines with three lines of context, each line with its break), as JSON values (each
  changed, added or removed value with its path and both values as written, numbers exactly; or why there is no JSON
  view), or as bytes (the lengths and the differing ranges, with excerpts of up to 16 bytes). A detail is `complete`,
  `limited` (its `limitations` say why: a side examined only in part, an end not proven, or a limit reached) or
  `unavailable` (no body differences, for example when they would not fit). Each side is examined up to 4 MiB, a
  detail shows at most 64 Ki characters, 256 JSON changes or byte ranges and 128 text hunks, and a `diff.json` is at
  most 256 KiB. JSON values are compared only when both sides are whole UTF-8 documents without a member name that
  appears twice in one object; text that is equal except for its charset or byte order mark, and JSON whose values
  are equal except for whitespace, member order or escapes, say so. `diff.json` holds captured content, as the
  request and response files do, and is published privately with them. Only `replay` takes `--verify-diff`.
- A capture that the GUI saved can include its replays as a device named `XMAT Replays`: `xmat analyze` reports it
  like any other device, and `xmat replay` can select its requests. Their numbers can repeat those of another device,
  so a selected number may need `--device` (`replay.request.ambiguous`).
- `xmat compose --sequence START-END` sends one request per number, like Fiddler's sequential requests: every `#`
  in `--url` is replaced by each number from `START` to `END` in turn, so `--url "https://localhost:5001/item/#?page=#"
  --sequence 08-11` sends `/item/08?page=08` up to `/item/11?page=11`. A `START` with a leading zero pads every number
  to its width; wider numbers are never cut. `START` and `END` have 1 to 15 digits, `START` is at most `END`, and a
  sequence has at most 10,000 numbers (exit code 2 otherwise). The numbers change only the path and query: a `#` in
  the scheme, host or port is refused (exit code 2), and every number must keep the URL's scheme, host and port
  (`replay.sequence.invalid`). Write `%23` for a `#` that the URL should keep. The numbers times `--repeat` count
  toward the 10,000 sends of a run (`replay.plan.too-large`, exit code 3), which xmat checks before it builds any
  request, and so does the length of the URLs that the sequence makes: together they may not exceed 16,777,216
  characters, for example 10,000 URLs of up to about 1,600 characters. The requests are planned in ascending order,
  round after round; with `--parallel` they can reach the server in another order. Each send's `sequence` in the
  JSON output is its number.
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
  "devices": [ { "id": "d1", "consoles": [ { "id": "c1", "calls": 4, "findings": [
    { "rule": "XR049Rule", "service": "presence_service", "level": "Warning", "count": 1 } ] } ] } ],
  "totals": { "calls": 4, "findings": 1 },
  "artifacts": [ { "kind": "summary", "path": "summary.json" } ],
  "cleanup": { "state": "completed" },
  "warnings": [],
  "errors": []
}
```

`status` is `succeeded`, `findingsFound`, `mismatchFound`, `requestsFailed`, `cancelled` or `failed`. `warnings` and
`errors` hold stable codes, such as `analysis.input.not-found`, with fixed messages, and `artifacts` lists what was
published. `analyze` publishes only when the run completes successfully: status `succeeded`, or `findingsFound` with
`--fail-on findings`; otherwise `artifacts` is empty. `replay` and `compose` publish once every request was sent,
whatever the responses were (status `succeeded`, `mismatchFound` or `requestsFailed`). Once its command line is valid, `analyze` reports the effective input limits in `limits`, even when it fails.
`summary.json` holds `schema` (`xmat-analysis/1`), `command`, `runId`, `version`, the same `input`, `limits`,
`devices` and `totals`, and the run's `warnings`. Instead of the analysis fields, `doctor` reports `target`, `checks`
(each with its `id`, `status` and, when it failed, its `code`), `environment` (`framework`, the .NET runtime, and
`architecture`, the process architecture such as `x64`) and `capabilities` (each with its `id`, `status` and
`slice`); `help` reports `commands`, each with its `name`, `status` (`supported`), `usage` and `capabilities` (the ids
of the capabilities that the command provides); and `--version` reports `version`. Instead of the analysis fields,
`replay` and `compose` report `options` (`repeat`, `parallel`, `timeoutSeconds`, `maxResponseMib`, `followRedirects`,
`unconditional`, `verify`, `verifyDiff`, `allowUnsafeMethods`, `allowOriginChange`, `httpVersion`, `connectTo`,
which says whether `--connect-to` was given but never its endpoint, and `allowConnectTo`), `replays` (one per send, in
order, each with its `id`, `device` and `request` (null for `compose`), `sequence` (the number that `--sequence` put in
the URL, otherwise null), `iteration`, `outcome`, `status` (null without a response), `durationMs`, `responseBytes`,
`redirects`, `transport` (`direct` or `connect-to`, as the send was configured), `httpVersion` (the version asked
for: `auto`, `1.1` or `2`), `responseVersion` (the version of the response, such as `1.1` or `2`, null without
one), `code`, `verify` (null without `--verify`, otherwise its `result`, `match` or `mismatch`, its
`differences`, a list of `status` and `body`, and its `detail`, null without `--verify-diff`, otherwise the `state` of
its `diff.json` and, once published, its `file`) and, once published, its `files`) and `totals` (`replays`,
`completed`, `failed`, `matched` and `mismatched`); `replay` also reports `input` and `limits` like `analyze`.
`summary.json` of a replay holds `schema` (`xmat-replay/1`), `command`, `runId`, `version`, the same fields and the
run's `warnings`.

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
| 3 | Invalid input or nothing to analyze: the input is missing, unreadable, unsupported, corrupt or over a safety limit, or `--device` names no device; for `replay` and `compose` also a selection, edit, sequence, `--body-file`, HTTP version or connect-to endpoint that cannot be used, a Fiddler control header, or a request that needs `--allow-unsafe-methods`, `--allow-origin-change` or `--allow-connect-to` (nothing is sent) |
| 4 | A prerequisite is missing or damaged, such as the analysis data next to `xmat.exe` |
| 5 | Conflict: `--out` already exists |
| 6 | The operation failed, for example the output could not be written, or `--out` is not on a local NTFS volume reached without a network path, substituted drive or link (`cli.output.unprotected`) |
| 7 | Cleanup failed: a staging folder could not be removed (overrides the other codes, cancellation included) |
| 10 | `--fail-on findings` and the rules reported findings |
| 11 | `--verify` (or `--verify-diff`) and a replayed response differs from the captured one (the results are published) |
| 12 | A replayed or composed request got no complete response (the results are published) |
| 70 | Internal error, including a `help`, `--version` or passing `doctor` run whose JSON document could not be produced (`cli.internal.output`) |
| 130 | Cancelled before the results were published |

## Compatibility

- The JSON on standard output (`xmat-cli/1`) and `summary.json` (`xmat-analysis/1`, `xmat-replay/1`) only grow within
  their version: a release may add fields, diagnostic codes, commands and capabilities, so ignore what you do not know.
  A change that removes or renames a field, changes its type or meaning, or changes what a status value means ships as
  a new schema version (`xmat-cli/2`, `xmat-analysis/2`, `xmat-replay/2`) together with migration guidance in this
  document.
- Exit codes keep their meaning: new codes may be added, but a code is never renumbered or reused.
- The command lines of available commands keep working.
- Intentional diagnostic changes from the earlier headless draft: XMAT capture sidecars and WAL mode now report
  `analysis.input.live-database`, unsupported normalized-path locations report
  `analysis.input.location-unsupported`, and writer sharing violations report `analysis.input.unreadable`; each is
  invalid input with exit code 3.

## What runs headless

The capability registry (`XMAT/Operations/Capabilities/CapabilityRegistry.cs`) classifies every GUI entry point as
**Supported** (available in `xmat.exe`), **Planned** (with the delivery slice and its owner; it has no command line
until it is available) or **GUI only** (presentation only). Tests fail when a GUI command, dialog, device or capture
type, analyzer or preference is not classified, when non-UI code starts using GUI-only APIs such as message boxes or
the dispatcher, or when the table below no longer matches the registry. See
[Headless Parity](../CONTRIBUTING.md#headless-parity) in CONTRIBUTING.md. The first capture slice (S4) keeps WebSocket
handshakes and reports the frames it observed without their payloads; archiving the payloads is planned separately.

<!-- xmat-capabilities:begin -->
| Capability | Command line | Headless |
|---|---|---|
| Analyze with the LTA rule set, custom rules or other thresholds (`analysis.compatibility`) | (not available yet) | Planned (S1b, XMAT-021) |
| Write the analysis HTML report (`analysis.html-report`) | (not available yet) | Planned (S3, XMAT-008) |
| Analyze network traces (`analysis.network-trace`) | (not available yet) | Planned (S8b, XMAT-021) |
| Analyze Xbox Live service calls (`analysis.xbox-live`) | `xmat analyze INPUT --out DIR [--device ID] [--fail-on findings] [--max-input-mib N] [--max-data-mib N] [--max-record-mib N] [--max-records N]` | Supported (S1) |
| Export captures as Fiddler archives (.saz) (`capture-file.export-saz`) | (not available yet) | Planned (S2, XMAT-021) |
| Read network traces (.etl) (`capture-file.import-etl`) | (not available yet) | Planned (S8b, XMAT-021) |
| Read Fiddler archives (.saz) (`capture-file.import-saz`) | `xmat analyze INPUT.saz --out DIR` | Supported (S1) |
| Read XMAT capture files (.xmatcap) (`capture-file.read-xmatcap`) | `xmat analyze INPUT.xmatcap --out DIR` | Supported (S1) |
| Sanitize a capture into a Fiddler archive (`capture-file.sanitize`) | (not available yet) | Planned (S2, XMAT-021) |
| Save captures (.xmatcap) (`capture-file.save-xmatcap`) | (not available yet) | Planned (S5, XMAT-021) |
| Choose the device and capture method (`capture.add-device`) | (not available yet) | Planned (S5, XMAT-021) |
| Clear captured data (`capture.clear`) | (not available yet) | Planned (S8a, XMAT-021) |
| Capture an explicit-proxy (generic) device (`capture.generic`) | (not available yet) | Planned (S5, XMAT-021) |
| Block or bypass hosts during capture (`capture.host-lists`) | (not available yet) | Planned (S8a, XMAT-021) |
| Capture this PC (`capture.local-pc`) | (not available yet) | Planned (S6, XMAT-021) |
| Capture low-level network traces (`capture.network-trace`) | (not available yet) | Planned (S8b, XMAT-021) |
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
| Compose and send a new request (`request.compose`) | `xmat compose --url URL --out DIR [--sequence START-END] [--method METHOD] [--header "NAME: VALUE"]... [--body-file FILE] [--repeat N] [--parallel N] [--follow-redirects N] [--timeout SECONDS] [--max-response-mib N] [--http-version auto\|1.1\|2] [--connect-to IP:PORT] [--allow-unsafe-methods] [--allow-connect-to]` | Supported (S9) |
| Replay captured requests, as captured or edited (`request.replay`) | `xmat replay INPUT --request SPEC --out DIR [--device ID] [--repeat N] [--parallel N] [--unconditional] [--verify] [--verify-diff] [--url URL \| --origin URL] [--method METHOD] [--header "NAME: VALUE"]... [--remove-header NAME]... [--body-file FILE] [--follow-redirects N] [--timeout SECONDS] [--max-response-mib N] [--http-version auto\|1.1\|2] [--connect-to IP:PORT] [--allow-unsafe-methods] [--allow-origin-change] [--allow-connect-to] [--max-input-mib N] [--max-data-mib N] [--max-record-mib N] [--max-records N]` | Supported (S9) |
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
