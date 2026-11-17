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
  skipped. A request whose request line, other than a `CONNECT`'s, names an absolute
  `http`, `https` or `ftp` URL with a valid host name or address and a port from 1 to
  65535 takes its scheme, host and port from that URL, as HTTP requires, even when its
  `Host` header is missing, malformed or names another host; its request line and
  headers are kept as they are. Any other request line, such as one with a path, `*`
  or a `CONNECT`'s target, gives an unknown scheme, shown blank rather than guessed,
  and its request can't be replayed. Earlier versions of XMAT showed the method
  instead for a path, `*` or text that is no URL, `http` for a `CONNECT`, and
  otherwise what .NET read as the target's scheme, such as `file` for a drive or UNC
  path; captures they saved keep it. A request with neither such a URL nor a usable
  `Host` header is imported without a host, and the Block and Bypass actions do not
  apply to it. Analysis names a request that has a host by its `Host` header when it
  has one, even an empty one, and otherwise by that host, and a request without a host
  by an empty host. It analyzes a request named by an empty host, or whose scheme is
  unknown, with its path only.
- A session's client address (its `x-clientip` flag) is kept as written, except that
  an IPv4 address written in its IPv6 form, such as `::ffff:10.0.0.1`, is kept as
  `10.0.0.1`. A session without the flag has the address `0.0.0.0`, and an archive
  with a flag that has no value is malformed and is not imported.
- Header lines are read as Latin-1, so a byte from 0x80 to 0xFF shows as that
  character rather than `?`, and a line of such characters alone, such as 0x85 or 0xA0,
  is a malformed header line rather than the empty line that ends the headers. A header
  name may contain an apostrophe, as HTTP allows. Each header line is kept in its order
  and with its name as written, its value without the spaces around it, so a name that
  repeats keeps each of its lines: two `Set-Cookie` lines stay two, though their dates
  hold commas. The import and analysis look a header up with the lines of a repeated
  name joined by commas, as before; replay and `--verify` take the last line of a
  repeated name instead, as in a capture that XMAT recorded. Earlier versions joined
  the lines of a repeated name with commas on import; a capture saved from such an
  import keeps them joined. A time with an offset or `Z` is read as that instant; one
  without, as XMAT's own exports write it, is this PC's local time. A duration is the
  time between two instants, also across a daylight saving change, and exports keep it.
  A time is read as unknown when it is the start of the year 1 without an offset, which
  Fiddler writes for a timer it never set and XMAT's exports for a time they do not
  know, or when it names no instant this PC can show as a local time, such as the first
  hours of the year 1 in UTC, read in a time zone west of UTC. An unknown time stays
  unknown when the capture is saved, exported or reloaded. Analysis and the timeline
  leave out a request whose time is unknown or earlier than 1601, which includes the
  year-1 times that earlier versions of XMAT saved for unknown times.
- XMAT stores each request and response head with the empty line that ends it, and
  Save Captures and Load Captures keep heads exactly. A `.SAZ` export writes each
  stored head unchanged. Characters inside header values, double quotation marks
  included, are kept.

### Replays saved by xmat

`xmat replay` and `xmat compose` with `--save-saz` also save their sends as
`replays.saz`, which **Import** opens like any other `.SAZ`. Session flags name each
send, as [Command line](cli.md) lists them. Three say how a send went out, never to
what or with what: `x-xmat-replay-transport` (`connect-to` or `proxy`, never the
endpoint or the proxy) for a send that was not direct,
`x-xmat-replay-client-certificate-configured` (`true`) for a send that could offer a
client certificate, never which certificate or whether one was sent, and
`x-xmat-replay-windows-auth-configured` (`true`) for a send that could answer with
Windows authentication, never whether the server asked or authentication succeeded.
The request of such a send is saved without the `Authorization` header that Windows
authentication added, and the `WWW-Authenticate` and `Proxy-Authenticate` headers of its
response keep only the scheme names of their `Negotiate`, `NTLM` and `Kerberos`
challenges, without their tokens; other challenges are saved as received.

A raw send of `xmat compose --raw` is saved normalized, as other replayed sends are: its
method and HTTP version as sent, with its target in absolute form, the target origin in
ASCII followed by the path and query as sent; `Host` first, written as that origin's
authority whatever `Host` the request had; its other fields without the spaces around
their values; a chunked body's `Transfer-Encoding` replaced by `Content-Length` with the
decoded length; and the decoded body. Only `replays/NNNNN/request.http` holds its exact
bytes, the request as attempted. Its session has two
more flags: `x-xmat-replay-raw` (`true`) and `x-xmat-replay-request-write` (`none`,
`partial`, `uncertain` or `complete`): how much of the request XMAT handed to the
connection, never proof of what the server received.

XMAT keeps the origin that a replayed send inherits, the one that it was captured,
saved or first sent for, only in memory with its row in the **Replays** tab, so that a
later send of it to another origin asks for consent. Saved and exported captures do not
keep it.

## Capture text markers and marked copies

Current `.XMATCAP` files carry SQLite `user_version = 1`, which means XMAT
stored their text literally. This is a storage classification only; it does
not certify that every attempted network byte was transmitted or recorded
without omission. An older or foreign file with marker `0` is
**AmbiguousUnmarked**. XMAT opens it read-only and uses every stored character
unchanged, with a warning: adjacent double quotation marks may be original data
or may have been added by an older XMAT version. Any `user_version` other than
0 or 1 is unsupported and refused before SQLite opens.

| Stored-text source | View and analyze | Import into a live marked session | Sanitized export |
|---|---|---|---|
| Marked (`user_version = 1`) | Proceed without a warning. | Allowed literally. | Allowed. |
| Unmarked (`user_version = 0`) | Preserve the stored text and warn that its historical interpretation is ambiguous. | Refused. | Refused; create and open a marked copy first. |
| Unsupported `user_version` | Refused before SQLite opens. | Refused. | Refused. |
| Unclassified/custom source | Treat as ambiguous and fail closed. | Refused. | Refused; use a marked native capture. |

Replaying a request, saving it as a request file and exporting it as a cURL script
use its stored text too. The GUI refuses them for an unmarked capture; create
and open a marked copy first. `xmat replay`, `xmat save-request` and `xmat curl`
use an unmarked capture's text as stored only with `--accept-stored-text`, for
that run's input alone (see [Command line](cli.md)).

Create a separate marked copy from the command line:

```text
xmat mark-capture-text INPUT --out DIR --interpret as-stored|legacy-one-layer
```

`as-stored` preserves the file's stored values. `legacy-one-layer` is
explicit: it accepts only TEXT values that are strict UTF-8 and whose every run
of `"` has even length, then removes one quote-doubling layer. The source
remains unchanged, extension columns are untouched, and marker 1 is written
only after the selected interpretation succeeds. XMAT never guesses an
interpretation and never applies this repair to a Fiddler archive or request
file.

Versions before capture-text fidelity cannot reliably filter a marked capture
whose device name contains a double quotation mark, so such a tab can appear
empty; open it with the current XMAT version.

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
the raw request path or the client process.

An unmarked or unclassified selected capture is refused before export work
starts. Create a marked copy with `xmat mark-capture-text`, open that copy, and
export it instead.

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
redaction.
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
