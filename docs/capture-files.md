# Capture files and sanitized export

[Back to README](../README.md)

Save captures for later, import traces from other tools, and export copies for sharing.

## Prerequisites

Open XMAT and select the capture you want to export. Exports also work while a capture
runs: sessions whose response is not complete yet are skipped, and the confirmation
says how many. Loaded and imported web captures are supported.

## Save, load, import, or export

1. Choose the appropriate **File** menu command.
2. Select the source or destination file.
3. Check the resulting capture tab or exported file.

| Menu option | What it does |
|---|---|
| **Save Captures** | Saves all open web proxy and network trace captures to an `.XMATCAP` file |
| **Load Captures** | Opens an `.XMATCAP` file in read-only tabs |
| **Import** | Opens a Fiddler `.SAZ` archive or a network `.ETL` trace captured with `netsh` in a read-only tab; re-importing the same-named `.SAZ` replaces that tab's data |
| **Export Selected Tab...** | Exports every session of the selected web proxy capture to an `.XMATCAP` file or a Fiddler `.SAZ` archive, as chosen in the save dialog |
| **Export Selected Tab Sanitized...** | Exports a sanitized `.SAZ` copy of the selected web capture without changing the original |

You can also drag `.XMATCAP`, `.SAZ`, or `.ETL` files onto the main window.
**WebSocket messages are not saved to or loaded from `.XMATCAP` files.**

**Expected result:** loaded/imported captures appear in read-only tabs; exports are
written to the selected destination.

### Export selected or visible sessions

In a capture list, right-click a request and choose **Export Selected Sessions...**
(the selected requests) or **Export Visible Sessions...** (every request that the
status, host, method and process filters show). XMAT confirms how many sessions it
will export, then asks for the file; the save dialog's file type chooses an
`.XMATCAP` subset (the original rows, ids and columns), a `.SAZ` archive or a
sanitized `.SAZ`. A file name that does not end in the chosen type's extension is
refused with an explanation and the save dialog opens again; nothing is written. The
completion message reports **Exported N of M sessions**, where M counts the capture's
sessions when the export started, and any sessions it skipped. An existing file is
replaced only after you confirm it in the save dialog and the export completes. XMAT
never replaces a capture open in a tab, including the `.SAZ` an imported tab was read
from or another name for the same file. While an export runs, the capture cannot be
stopped. `xmat export` makes the same exports from the command line (see
[Command line](cli.md)).

An exported file can be opened only by your Windows account, also when it replaces a
file, and so can the temporary folder next to it that XMAT writes it in. On storage
where Windows cannot restrict access (such as FAT drives or some network shares), XMAT
asks before exporting there; if you agree, the file is exported without that
protection and the completion message says so. The answer covers only that export;
`xmat export` never exports there.

A `.SAZ` export also keeps XMAT's own values as session flags: the request number,
connection id, exact request and response times, scheme, request path (with its
query) and client IP address. XMAT's import restores each value whose flag is
present and valid, and otherwise reads the archive as Fiddler wrote it.

### Streamed responses

A response body that passes the capture limit (see
[Streaming and large responses](web-proxy.md#streaming-and-large-responses)) is captured
only up to the limit, and the response pane says where it was truncated: "Capture
truncated at 32 MB; additional bytes are not captured." XMAT keeps forwarding the rest of
the body to the device. The stored headers stay as the server sent them, so a
truncated body can be shorter than its `Content-Length`. Trailer fields that followed a
response body are listed under its headers. `.XMATCAP` files keep both in two optional
columns of the `WebProxyConnections` table: `ResponseTrailers` (the trailer lines) and
`ResponseBodyTruncatedAt` (the body bytes kept; 0 when the body is complete). Captures
saved without these columns load with complete bodies and no trailers. A `.SAZ` export
writes them as the `x-xmat-response-trailers` and `x-xmat-response-body-truncated-at`
flags, which an import restores when they are valid. A sanitized `.SAZ` keeps the
truncation but never writes the trailers (see below). Exports of a running capture
skip a response that is still streaming, as they skip any response that is not
complete yet.

### Process information

Web proxy captures on a PC record the program that opened each connection (see
[Filter by process](web-proxy.md#filter-by-process)): its image name, never its path,
its process id and start time, and whether XMAT identified it on this PC or read it
from an imported archive. `.XMATCAP` files keep it in four optional columns of the
`WebProxyConnections` table (`ClientProcessName`, `ClientProcessId`,
`ClientProcessStartTime` and `ClientProcessStatus`). Captures saved without these
columns, `.SAZ` archives without process metadata and traffic from consoles or other
devices show **Unknown** or **Remote device**.

A `.SAZ` export writes each session's program to its metadata as Fiddler does, in
`x-ProcessInfo` (`name:pid`), with XMAT's `x-xmat-clientprocess-starttime` and
`x-xmat-clientprocess-status` flags. **Import** reads `x-ProcessInfo` whatever its
case, keeps only the image name if it includes a path, and treats the result as
imported metadata, not as a program on this PC. A `:pid` suffix counts only when it
is a positive number; a value XMAT cannot use leaves the session without process
information. A sanitized `.SAZ` omits all three flags (see below).

If a new `.SAZ` import fails, XMAT reports the error and removes only the tab created
for that attempt. If re-importing a same-named `.SAZ` fails, the existing tab,
selection, and data stay unchanged. Retry without restarting XMAT; details are in
`application.log` in the application's `Logs` directory.

XMAT checks capture files before reading them and never changes them:

- A `.XMATCAP` file may contain only the tables and columns that XMAT writes, plus
  extra untyped, TEXT or INTEGER columns, which can be empty (NULL). A file with any
  other table, index, view, trigger, constraint or generated column, or with a schema
  larger than XMAT supports, is refused. So is a file that another program has open
  for writing, or one with SQLite journal or write-ahead log files next to it
  (`-journal`, `-wal` or `-shm`): open a clean capture that has been closed or
  exported instead; XMAT leaves those files unchanged. A capture in a location where
  Windows cannot report a normalized path, such as some network shares, is refused
  too; copy it to a local folder first. If loading fails, the open tabs are unchanged.
  While a capture is open, XMAT reads only that file, and other programs can read it
  but not change it. A loaded capture reuses the read-only tab of the same device
  instead of opening a duplicate, and closing the last tab that shows a file releases
  the file. While a network capture is running, a loaded tab for the same device shows
  its new packets only from when the tab opens or reloads; packets that arrive while
  the file loads appear in the running capture's tab, and every packet is still saved.
- A `.SAZ` archive's directory is checked before it is read: an archive that lists
  more than 750,064 files, has a directory larger than 80 MiB, spans several disks or
  has an inconsistent directory is refused.
- Each request or response in a `.SAZ` may have at most 16,384 header lines and 16 MiB
  of header data (its first line, the header lines and the empty line that ends them);
  a larger message stops the import rather than being truncated. Malformed header
  lines and incomplete frames are skipped, and the status bar notes that data was
  skipped. A request without a usable `Host` header is imported without a host;
  analysis keeps its path, and the Block and Bypass actions do not apply to it.

## Export a sanitized capture

1. Select the web proxy capture to share.
2. Choose **File > Export Selected Tab Sanitized...** (or export selected or visible
   sessions and choose the sanitized file type), confirm, and select a destination.
3. Review the completion dialog's redactions and omissions.
4. **Review the exported archive manually before sharing.**

Sanitization is per export, not a preference. Ordinary exports and native
**Save Captures** remain unsanitized. Only the chosen sessions are exported;
the original capture is not sanitized in place. A sanitized archive keeps XMAT's
request numbers, connection ids, times and schemes, but not the client IP address,
the raw request path, the client process or response trailers.

### What is inspected

The exporter redacts recognized credential/cookie headers, sensitive query and form
parameters, URL credentials, sensitive JSON/XML fields (including OAuth device codes
and private JWK members), and common token/private-key patterns in text.

Credential discovery covers headers, URLs, forms, and structured fields before
either direction of a session is written. This includes decoded Basic/userinfo
components, token parameters, and individual cookies from repeated or combined headers.
Header names are case-insensitive and repeated headers are retained.

JSON keys and query/form parameter names are inspected. Learned values in names are
masked when long (at least eight characters) or numeric identifiers (at least four
digits); short and recognized schema labels are preserved. Renamed JSON keys use
collision-safe names. Xbox profile IDs/display names, recognized structured-body
credential echoes across a request/response pair, and client-IP metadata are masked.
Client process metadata (`x-ProcessInfo` and XMAT's process flags) is never written;
each session for which an ordinary export would write any of it counts as one
redaction. Response trailers (`x-xmat-response-trailers`) are never written either,
because their values can hold secrets; each trailer left out counts as an omitted
header.
JWT recognition does not depend on a particular encoded-header prefix. Standalone
Xbox credentials and PGP private-key armor are recognized.

### Formats and framing

- JSON, XML, URL-encoded forms, and plain text are inspected. Gzip, deflate, Brotli,
  and declared character encodings are supported.
- Exported text is UTF-8 with updated HTTP framing, including bodyless
  HEAD/CONNECT/1xx/204/304 responses.
- Genuine chunk framing is decoded before scanning; already-dechunked captures
  remain supported. Ambiguous or malformed framing is omitted.
- Sensitive namespace URLs are remapped consistently in XML. Password-input
  values in inspected XML are redacted.
- Percent-encoded paths and nested URI/form values are inspected through at most
  eight decoding passes, preserving untouched values' original representation.
- Invalid UTF-8 or excessively nested URL/form components are redacted without
  discarding unrelated components or aborting the archive.
- URLs are compared literally and with form decoding, preserving literal `+` and
  matching credentials spanning path segments.
- Learned values in URL authorities use a valid replacement host. Structural Host
  and generated Content-Type headers are not subject to echo replacement.

### Omissions and processing limits

Unsupported content (including binary, HTML/XHTML, and multipart bodies), malformed
structured data, JSON/XML nesting beyond 64 levels, unknown compression/character
encodings, and bodies exceeding the **4 MiB** inspection limit are omitted rather
than copied unchecked.

Scanning has a finite work budget and remembers at most **64 distinct sensitive
values or representations**, each up to **4,096 characters**, per request/response
pair. XML discovery includes individual values and sensitive containers' combined
text. Learned values are matched at token boundaries and reset before the next pair.

If credential discovery exceeds capacity, or scanning exhausts its work budget or
regex timeout in either pass, partial output is discarded. The pair's original URLs,
headers, and bodies are replaced with minimal HTTP framing. Large authorization
values or profile batches can therefore lose diagnostic content; these limits do not
guarantee diagnostic completeness.

The completion dialog reports redactions and omissions, and SAZ session metadata
identifies omitted request/response bodies. Exports can retain headers exceeding the
import limits (16,384 lines or 16 MiB, see above); XMAT refuses to import such an
archive rather than truncating it.

### Cancellation and failures

Longer exports show a progress window with the current step and Cancel. Cancel,
Escape, or closing that window requests cancellation and waits for the worker and
cleanup before releasing the operation guard. An existing destination is replaced only
after a complete export, not on cancellation or failure; one that another program has
open is reported and left as it is. An `.XMATCAP` export fails with an error and
leaves nothing behind when its temporary file, in a folder next to the destination,
would have a path longer than Windows' 260-character limit; choose a shorter folder.
`.SAZ` exports are not affected.

### Review before sharing

**Sanitization is best-effort detection, not a guarantee of anonymity.** Arbitrary
unnamed secrets, custom encodings, and unrecognized identifiers may remain.
Nonsensitive service endpoints, paths/values, timing, and request IDs are retained
for diagnosis.

HTTP header names, XML element/attribute names, and protocol method/version tokens
are not identifier-scrubbed. Arbitrary percent-encoded text outside URL/form contexts,
unrecognized identifier fields, and JWTs joined directly to ordinary letters can
remain. Short learned values can also redact benign data. Review the archive and
explicitly decide which identifiers your sharing scenario requires removing.

Sanitized capture export does **not** sanitize
[analysis report exports](live-trace-analysis.md#export-an-analysis-report).

## Import network traces from another PC

1. On the remote PC, open an elevated command prompt and start a trace:

   ```powershell
   netsh trace start capture=yes
   ```

2. Reproduce the issue, then stop tracing:

   ```powershell
   netsh trace stop
   ```

3. Copy the generated `.ETL` file from the location printed by `netsh`.
4. Use **File > Import** or drag the file onto XMAT.

XMAT need not be installed on the remote PC. Only packet capture data
(`Microsoft-Windows-NDIS-PacketCapture`) is imported; other trace events are ignored.
Traces captured with `pktmon` are not currently supported.

For capturing and analyzing packets inside XMAT, see [Network capture](network-capture.md).
