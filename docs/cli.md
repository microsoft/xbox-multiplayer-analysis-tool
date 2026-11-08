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
xmat replay INPUT --request SPEC --out DIR [--save-saz] [--device ID] [--repeat N] [--parallel N] [--unconditional]
            [--verify] [--verify-diff] [--url URL | --origin URL] [--method METHOD] [--header "NAME: VALUE"]...
            [--remove-header NAME]... [--body-file FILE] [--form NAME=VALUE|NAME=@FILE]...
            [--form-string NAME=VALUE]... [--form-type N=TYPE]... [--follow-redirects N] [--timeout SECONDS]
            [--max-response-mib N] [--http-version auto|1.1|2] [--connect-to IP:PORT] [--proxy URL]
            [--client-cert THUMBPRINT] [--allow-client-cert ORIGIN] [--windows-auth] [--allow-windows-auth ORIGIN]
            [--allow-unsafe-methods] [--allow-origin-change] [--allow-connect-to] [--allow-proxy] [--max-input-mib N]
            [--max-data-mib N] [--max-record-mib N] [--max-records N]
xmat compose --url URL | --request-file FILE | --raw FILE --target ORIGIN --out DIR [--save-saz] [--allow-raw]
             [--fix-content-length] [--sequence START-END] [--method METHOD]
             [--header "NAME: VALUE"]... [--remove-header NAME]... [--clear-headers] [--body-file FILE] [--empty-body]
             [--form NAME=VALUE|NAME=@FILE]... [--form-string NAME=VALUE]... [--form-type N=TYPE]...
             [--repeat N] [--parallel N] [--follow-redirects N] [--timeout SECONDS] [--max-response-mib N]
             [--http-version auto|1.1|2] [--connect-to IP:PORT] [--proxy URL] [--client-cert THUMBPRINT]
             [--allow-client-cert ORIGIN] [--windows-auth] [--allow-windows-auth ORIGIN] [--allow-unsafe-methods]
             [--allow-origin-change] [--allow-connect-to] [--allow-proxy]
xmat save-request INPUT --request SPEC | --request-file FILE | --url URL --out DIR [--device ID] [--method METHOD]
                  [--header "NAME: VALUE"]... [--remove-header NAME]... [--clear-headers] [--body-file FILE]
                  [--empty-body] [--form NAME=VALUE|NAME=@FILE]... [--form-string NAME=VALUE]... [--form-type N=TYPE]...
                  [--unconditional] [--http-version auto|1.1|2] [--include-sensitive]
                  [--allow-origin-change] [--max-input-mib N] [--max-data-mib N] [--max-record-mib N] [--max-records N]
xmat curl INPUT --request SPEC | --request-file FILE | --url URL --out DIR [--include-sensitive] [--device ID]
          [--method METHOD] [--header "NAME: VALUE"]... [--remove-header NAME]... [--clear-headers] [--body-file FILE]
          [--empty-body] [--form NAME=VALUE|NAME=@FILE]... [--form-string NAME=VALUE]... [--form-type N=TYPE]...
          [--unconditional] [--http-version auto|1.1] [--allow-origin-change] [--max-input-mib N]
          [--max-data-mib N] [--max-record-mib N] [--max-records N]
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
  reports the warning `analysis.input.malformed-header` once. Header lines are read as Latin-1, and a header name may
  contain an apostrophe, as HTTP allows. The header limits cover the sanitized exports that were
  tested, not every archive that can be written: XMAT can record messages whose headers exceed them and export them in a
  plain or sanitized Fiddler archive; xmat refuses such an archive (`analysis.input.limit`) and the GUI does not import
  it, rather than truncating it. A request without a usable `Host` header (missing, empty, whitespace-only or on a
  malformed line) is kept and analyzed with an empty host: xmat reports it as an unknown service (`service-<n>`), and
  the GUI's Block and Bypass actions do not apply to it. A frame whose non-empty request line or status line has no
  second field (the URL or the status code) is skipped like an incomplete frame, one without its request or its
  metadata, with the warning `analysis.input.incomplete-frame`. The other frames are still analyzed; when no frame is
  left, there is nothing to analyze (`analysis.no-data`, exit code 3) and nothing is published. A request whose time is
  unknown, such as a timer that Fiddler never set, or earlier than 1601 is read but not analyzed, as the rules compare
  calls by their time.
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
  like Fiddler's Composer. Both send from this PC, the way the GUI's request composer does: directly, unless `--proxy`
  names an upstream proxy, and never through the system's or the environment's proxy settings (so, unless you name
  XMAT's own proxy, XMAT's capture, scripts and host lists do not apply and the replays are not captured), without
  cookies of their own, without a client certificate unless `--client-cert` names one, without answering an
  authentication challenge unless `--windows-auth` is given, without decompressing
  responses, and with Windows' certificate validation. `replay` reads
  `INPUT` exactly as `analyze` does, with the same limit options and `analysis.input.*` errors. `--request` selects
  requests by the number the GUI shows in its `#` column: `all`, or numbers and ranges such as `1,5,9-12` (at most
  10,000 numbers). A
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
- `--form NAME=VALUE`, `--form NAME=@FILE` and `--form-string NAME=VALUE`, each repeatable and kept in command-line
  order, make the body a `multipart/form-data` form in place of `--body-file` or `--empty-body`, for `replay`,
  `compose`, `save-request` and `curl`. `--form NAME=@FILE` sends that file's bytes, read once when the run starts,
  under the file's own name, never its folder; `--form-string` is always text, so a value that starts with `@` is sent
  as it is. Nothing else is ever read as a file: a captured or typed `@` or `<@INCLUDE ...@>` is just text.
  `--form-type N=TYPE` gives the N-th part, counting `--form` and `--form-string` together, a media type such as
  `text/plain; charset=utf-8`. Text is always sent as UTF-8, so a `charset` must say so; a file part without a type is
  `application/octet-stream`. A NAME is 1 to 256 printable ASCII characters (on the command line it cannot hold `=`).
  A file name's bytes that are not ASCII, and any `%` in it, are sent percent-encoded (RFC 7578, never `filename*`),
  which a server may or may not decode.
- A form has at most 128 parts and at most 64 MiB in all, its framing included. xmat measures it before it reads any
  file, refuses a file that cannot fit before reading it (`replay.multipart.too-large`), and refuses a missing or
  unreadable file or a value that is not valid Unicode (`replay.multipart.file-not-found`, `.file-unreadable`,
  `.invalid-text`), all before it creates anything (exit code 3); `curl` checks `--include-sensitive` before it
  reads any of them. The form's random boundary appears nowhere in its parts (`replay.multipart.boundary-collision`
  after five tries). Its `Content-Type: multipart/form-data; boundary=...` replaces every `Content-Type` header, so
  `--header Content-Type` and `--remove-header Content-Type` cannot be used with a form, and the method is never
  changed. xmat sends and writes nothing (exit code 3) when a request's `Connection` header nominates `Content-Type`,
  which sending would drop with the boundary (`replay.multipart.content-type-dropped`; `--remove-header Connection`
  removes the nomination), or when a request file left out the header block that the form's `Content-Type` needs
  until `--clear-headers` supplies it (`replay.multipart.headers-omitted`). A form completes a body that a request
  file left out, as `--body-file` does. A captured `Content-Encoding` is kept as it is: the form is not encoded with it.
- Every selected request is checked before the first one is sent, and nothing is sent when one fails a check (exit
  code 3). A request that is not valid after the edits, for example one with a header value that is not ASCII, which
  the HTTP stack cannot send, fails with `replay.request.invalid`. Unless you allow it, xmat
  refuses to send a method other than GET, HEAD and OPTIONS, which can change data on the server
  (`replay.policy.unsafe-method`; `--allow-unsafe-methods` allows it), or captured headers, such as credentials, to
  another origin than the one they were captured or saved for (`replay.policy.origin-change`; `--allow-origin-change`
  allows the origins that `--url` or `--origin` name: `replay` takes it, `compose` only with `--request-file`, and
  `save-request` and `curl` only with `--include-sensitive`). Redirects are returned, not followed,
  unless `--follow-redirects N` follows up to `N` of them (0 by default, at most 10), and only within the origin that
  was sent to: a 303 continues as GET (a HEAD stays a HEAD) and a 301 or 302 to a POST continues as GET, both without
  the body, as browsers do, while a 307 or 308 keeps the method and the body. A redirect to another origin is never
  followed, whatever the options, so a server never decides where the headers and body of a request go next; nor is
  a redirect from HTTPS to HTTP, to anything other than HTTP and HTTPS, to a URL with credentials, or without exactly
  one usable `Location`. With `--follow-redirects 0`, a redirect is the result as it is, without a code; otherwise a
  redirect that is not followed is the result, with the code `replay.redirect.origin-change`,
  `replay.redirect.insecure`, `replay.redirect.unsupported` or `replay.redirect.limit`.
- Wherever xmat compares origins (a redirect, a changed origin, the requests of a `--connect-to` run), two URLs are of
  one origin when their schemes and ports are the same and so are their hosts, compared both as written, ignoring case,
  and in their canonical ASCII (IDN) form. So `ασ.example` and `ας.example`, which differ only in a Greek final sigma,
  are two origins, as DNS has them, and so are an IPv6 address with two zones, or with one zone written two ways, such
  as `[fe80::1%25A]` and `[fe80::1%25a]`. A host that has no canonical form is of no origin: a redirect to it is not
  followed, and a `--connect-to` run with it is refused (`replay.connect-to.origin-count`).
- A request that carries one of Fiddler's control headers, `Fiddler-Host`, `X-OverrideHost`,
  `X-IgnoreCertCNMismatch` or `X-OverrideGateway`, is refused (`replay.request.control-header`): xmat never acts on
  a captured header, so a capture never chooses where its requests go or which certificate is accepted. Remove the
  header (`--remove-header`) to replay the request.
- `--http-version` chooses the HTTP version: `auto` (the default) sends HTTP/2 to an HTTPS server that offers it and
  HTTP/1.1 otherwise, as before; `1.1` and `2` ask for exactly that version, and a request that cannot be sent with it
  fails (`replay.send.failed`) rather than go out with another. `2` needs HTTPS URLs (`replay.http-version.https-required`).
  Unless `--windows-auth` is given, xmat never answers an authentication challenge, such as NTLM or Negotiate, with
  your Windows credentials: the challenge is the result.
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
- `--proxy URL` sends every request through that upstream HTTP proxy, as Fiddler's gateway does: an `http://` request
  goes to the proxy whole, and an `https://` one through a `CONNECT` tunnel, with TLS to the server inside it. `URL` is
  `http://HOST` or `http://HOST:PORT`, with or without a final `/`: `HOST` is an ASCII host name (an international one
  in its `xn--` form) or an IP address, an IPv6 one in brackets without a zone, and `PORT` is 1 to 65535, 80 by
  default. For the proxy only, anything else is a usage error that never repeats the value (exit code 2): a user name
  or password, a path, query or fragment, another scheme (HTTPS and SOCKS proxies included), a host name that ends
  with a dot or whose last label is a number, or an address in another form, such as `127.1` or `0x7f.0.0.1`. Every
  request of the run goes through the proxy, a loopback URL included, and so does every redirect that is followed;
  `--proxy` cannot be used with `--connect-to` (`replay.proxy.connect-to`). The proxy is a trusted recipient of the
  whole request: it receives the whole of every `http://` request, its `Authorization`, `Cookie` and other headers and
  its body included. For HTTPS, the unencrypted CONNECT request exposes the destination host and port and, when
  supplied, User-Agent information that may identify the application/title, SDK or device. If the proxy intercepts
  HTTPS with a certificate this PC trusts, it also receives the whole request and it—not XMAT—decides whether the
  server is genuine. XMAT's own proxy does not check servers' certificates. xmat checks the server's certificate as it
  does without a proxy, so a proxy that intercepts HTTPS with a certificate that this PC does not trust fails the send.
  Use only a proxy that you trust with the requests' credentials: xmat sends nothing without `--allow-proxy`
  (`replay.policy.proxy`). An HTTP 407 on a send through the proxy, whether it answers the request, the `CONNECT` of
  its tunnel or comes inside the tunnel, fails the send without a response or status, with
  `replay.proxy.auth-required`, which means: HTTP 407 (Proxy Authentication Required) was received. XMAT does not
  authenticate to proxies and did not retry the request. A proxy that answers the `CONNECT` of an `https://` request
  with a status other than 200 or 407 fails the send with `replay.proxy.tunnel-failed`, and one that cannot be reached
  fails it with `replay.proxy.unreachable`; xmat never falls back to sending directly.
- `--client-cert THUMBPRINT` offers a certificate from your Personal certificate store (`Cert:\CurrentUser\My`) to a
  server that asks for a client certificate, as Fiddler can. `THUMBPRINT` is the certificate's SHA-1 thumbprint, 40
  hexadecimal digits without spaces. A client certificate identifies you to the server that receives it, so xmat sends
  nothing unless `--allow-client-cert ORIGIN` names the run's one origin (`replay.policy.client-cert`). `ORIGIN` is
  `https://HOST` or `https://HOST:PORT` (443 by default), with or without a final `/`. `HOST` is a host name (an
  international one in either form) whose last label, once in ASCII, is not a number, an IPv4 address written as four
  decimal numbers, or an IPv6 address in brackets without a zone. For both options anything else is a usage error that
  never repeats the value (exit code 2). Two origins are the same when their hosts, in ASCII, and their ports are; a
  host that ends with a dot is another origin. The run's URLs must be HTTPS (`replay.client-cert.https-required`), each
  with an origin that a certificate can be allowed for (`replay.client-cert.origin-invalid`, for example an IPv6
  address with a zone, or a host name whose last label is a number once in ASCII, such as `127。1`), and share that one
  origin (`replay.client-cert.origin-count`). `--client-cert` cannot be used with `--proxy` (`replay.client-cert.proxy`);
  with `--connect-to` the endpoint must still prove that it is the URL's host.
- Only after every check and permission passed does xmat open the store, read-only, and look for the certificate, valid
  or not. It refuses one when:
  - the store cannot be read (`replay.client-cert.store-unavailable`), or no certificate, or more than one, has that
    thumbprint (`replay.client-cert.not-found`, `.ambiguous`);
  - its properties cannot be read (`.metadata-unavailable`);
  - it is a certificate authority's (`.ca`);
  - it is not allowed for client authentication (`.not-client-auth`) or digital signatures (`.key-usage`);
  - its key is not RSA or ECDSA (`.unsupported-key`, which includes a key for RSA-PSS only), or this PC has no private
    key recorded for it (`.no-private-key`).

  xmat itself never asks for a PIN, a smart card or consent. Before it sends anything, it uses the key once, silently,
  to sign a fixed value; a key that cannot be used that way, such as one that needs a PIN or a smart card, is refused
  (`replay.client-cert.key-unavailable`). During the TLS handshake xmat asks Windows to use the key silently as well,
  which has not been verified for every key storage provider, such as smart cards: one could still ask then. An ECDSA
  key in a CryptoAPI provider and a key in a CryptoAPI provider that cannot sign SHA-256 hashes, such as the older
  "Microsoft Enhanced Cryptographic Provider", are refused as `key-unavailable` too. xmat never changes the store or the
  certificate. Each of these refusals is exit code 3, and nothing is sent.
- The certificate is sent only to a server that asks for one, and only after that server showed a certificate that
  this PC accepts for the allowed origin: the same check that xmat makes on every HTTPS server, made before the
  certificate leaves. A server whose certificate is untrusted or names another host never receives it. TLS sessions are
  never resumed, so every connection checks the server again. A server that does not ask for a certificate receives
  none, and the send goes on without it. A redirect is followed only within that one origin. An expired certificate is
  offered as it is: the server decides.
- `--windows-auth` answers the server's `Negotiate` and `NTLM` challenges with the Windows credentials that xmat runs
  with, as Fiddler's Composer can. Windows authentication: XMAT answers the origin's challenge with the Windows
  credentials that it runs with, which may be a service or machine account's; Negotiate may fall back to NTLM, which a
  recipient can relay or attack offline; a DNS attacker can steer the Kerberos service name through CNAMEs, while HTTPS
  still proves the host. So xmat sends nothing unless `--allow-windows-auth ORIGIN` names the run's one origin
  (`replay.policy.windows-auth`). `ORIGIN` is written as for `--allow-client-cert`, and anything else is a usage error
  that never repeats the value (exit code 2). Before anything is sent, a run with `--windows-auth` is also refused when
  it uses `--proxy` (`replay.windows-auth.proxy`) or `--connect-to` (`replay.windows-auth.connect-to`); when its URLs
  are not HTTPS (`replay.windows-auth.https-required`), not each of an origin that it can be allowed for
  (`replay.windows-auth.origin-invalid`) or not of one origin (`replay.windows-auth.origin-count`); with
  `--http-version 2` (`replay.windows-auth.http2`); or when a request has an `Authorization` header of its own, even an
  empty one or one that its `Connection` header names (`replay.windows-auth.authorization-header`). With
  `--client-cert` too, the run needs both permissions, each for its one origin, and the certificate's checks come
  first.
- With `--windows-auth`, xmat answers only the allowed origin's `Negotiate` and `NTLM` challenges: a `Basic`, `Digest`
  or any other challenge, and a 407's `Proxy-Authenticate`, are the result. It never sends credentials before the
  server asks, asks for exactly HTTP/1.1 (`auto` becomes `1.1`, and the output says so), follows a redirect only within
  the allowed origin, and sends on connections of the run's own, closed when the run ends, so no other request uses a
  connection that it authenticated. The `Authorization` header that Windows authentication adds while a request is
  sent is removed from it after each hop, so `request.http` and `replays.saz` show the request as xmat built it, without
  that header. In each response, `WWW-Authenticate` and `Proxy-Authenticate` keep only the scheme names of their
  `Negotiate`, `NTLM` and `Kerberos` challenges, without their tokens; other challenges stay as received, and a value
  that is not a well-formed challenge list is left out.
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
- `--save-saz` (`replay` and `compose`) also publishes `replays.saz`, a Fiddler archive with each send as a session,
  which the GUI's import, `xmat analyze` and `xmat replay` read. It is a transcript of what xmat sent and got, not a
  capture of the wire. A request names its final URL in full, in ASCII (an international host name in its IDN form,
  an IPv6 address without its zone), with the `Host` that was sent, the headers of `request.http`, one line each (a
  reader joins repeated ones with commas), and its body. A `compose --raw` send is saved normalized instead: its
  target in that absolute form, `Host` written as that URL's authority whatever `Host` the request had, its other
  fields without the spaces around their values, and a chunked body decoded, with `Content-Length` in place of its
  `Transfer-Encoding`; only its `request.http` holds its exact bytes. A response has the head and body of
  `response.http`. A
  response larger than `--max-response-mib` keeps its head, followed by `X-XMAT-Response-Body-Not-Kept` with the size
  that the response declared (unverified) or `unknown`, and no body, so a replay of the archive does not verify it. A
  request without a complete response has no response. A session's number is its send's id, and sessions come in the
  order the sends completed, which `--parallel` can change. Session flags name the send: `x-xmat-replay-id`,
  `x-xmat-replay-of` (the request number), `x-xmat-replay-device`, `x-xmat-replay-sequence`, `x-xmat-replay-outcome`
  (`completed`, `failed`, `timed-out` or `too-large`), `x-xmat-replay-code`, `x-xmat-replay-redirects`, for a send
  that was not direct, `x-xmat-replay-transport` (`connect-to` or `proxy`, never the endpoint or the proxy), for a
  send that could offer a client certificate, `x-xmat-replay-client-certificate-configured` (`true`; never the
  certificate, nor whether the server received it) and, for a send that could answer with Windows authentication,
  `x-xmat-replay-windows-auth-configured` (`true`; never whether the server asked or authentication succeeded). The
  archive
  is completed only when the run publishes `DIR`; a run that publishes nothing removes it with the staging folder. It
  holds captured content, as the request and response files do, and is published privately with them. xmat reads it
  back only within its input limits (`--max-input-mib`, `--max-record-mib` and the others below), refusing a larger
  one (`analysis.input.limit`).
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
- `xmat save-request` saves requests as request files (`xmat-request/1`) and sends nothing: the requests that
  `INPUT --request SPEC` selects, the request of a `--request-file`, or a new request with `--url`, after the same edits
  as `replay`. It publishes `DIR/summary.json` (schema `xmat-save-request/1`) and `DIR/requests/NNNNN.json`, one file
  per request, at most 10,000 of them (`replay.plan.too-large`, exit code 3, checked before any request is built).
- A request file is a template by default. It keeps the HTTP version, the method when it is `GET`, `HEAD`, `OPTIONS`,
  `POST`, `PUT`, `PATCH`, `DELETE` or `TRACE`, and an empty header block or body only when there is none. It leaves out
  the URL, every header name and value, any other method and the body, and its `omitted` member lists what it left out.
  `--include-sensitive` saves everything instead, captured credentials and cookies included: such a file is as private
  as the capture. A request whose origin an edit changed is then saved only with `--allow-origin-change`
  (`replay.policy.origin-change`, exit code 3), since its file's URL becomes the origin that a later change needs consent
  for. The JSON output and `summary.json` give, for each file, its profile, what it leaves out, and how many header and
  query parameter names the sanitized export would treat as secret: never a name or a value. A body is never inspected,
  only counted (`bodyBytesNotInspected`).
- `xmat compose --request-file FILE` sends a request file's request. A request file is data: it never grants consent
  and carries no transport option but its HTTP version, which `--http-version` overrides. xmat reads it as untrusted: at
  most 96 MiB, a body of at most 64 MiB, at most 256 headers of at most 256 KiB together, and nothing but the members of
  `xmat-request/1` (`replay.request-file.invalid`, `.unsupported`, `.not-found`, `.too-large` or `.unreadable`, exit code
  3). With a file, `--url`, `--method`, `--header`, `--remove-header` and `--body-file` edit its request, and these
  supply what the file left out: `--url` the URL, `--method` the method, `--clear-headers` an empty header block, which
  `--header` and `--remove-header` then edit, and `--body-file` or `--empty-body` the body. Adding a header never
  supplies a header block that the file left out. A request that still leaves anything out is never sent
  (`replay.request.incomplete`, exit code 3). The file's URL is the request's origin: changing it needs
  `--allow-origin-change`, which compose takes only with `--request-file`. The JSON output's `input` gives only the
  file's kind (`request-file`) and SHA-256.
- `xmat compose --raw FILE --target ORIGIN --allow-raw` sends the request in `FILE` as its exact bytes (Raw mode, like
  the Raw tab of Fiddler's Composer but constrained): one HTTP/1.1 request to one HTTP or HTTPS origin, on a connection
  of its own, without the normalization that every other send gets. `--target` is that origin, `http://HOST` or
  `https://HOST` with an optional `:PORT`; a path, query, fragment or user information is a usage error. `--raw` itself
  selects HTTP/1.1.
  - The file is the whole request. Its size is checked first, at most 64 KiB + 65 MiB (`replay.raw.input-too-large`);
    it is read once, never past that size, and only those bytes are used; it cannot be read: `replay.raw.file-unreadable`.
    Its path never reaches the output, and the JSON output's `input` gives only its kind (`raw-file`) and SHA-256.
  - xmat checks the request before anything is resolved, connected or sent, and refuses it with exit code 3 and one of
    these codes; for a fault in the head, standard error names its line number:
    - `replay.raw.input-too-large`: a head over 64 KiB, a request line over 16 KiB, more than 256 field lines, a
      method over 64 characters, a body over 64 MiB, more than 1 MiB of chunk framing, or a chunk line over 8 KiB;
    - `replay.raw.invalid`: anything but ASCII with CRLF line ends, a folded line, a line before the request line,
      other than single spaces in the request line, or a field that is not `NAME: VALUE`;
    - `replay.raw.method`: a method that is not a token of 1 to 64 characters, `CONNECT`, or a standard method written
      in another case (`get`);
    - `replay.raw.target`: anything but a path (origin-form), or an absolute URL followed by a path whose scheme and
      authority are the target origin's, written as xmat writes them: the scheme and the host in lower case (an
      international name in its `xn--` form, an IPv6 address in brackets) and the port, which may be left out only
      when it is the scheme's default; a fragment, user information, `*` or an authority alone is refused;
    - `replay.raw.version`: anything but `HTTP/1.1`;
    - `replay.raw.duplicate-field`: a field name that appears twice, in any case;
    - `replay.request.control-header`: a Fiddler control header;
    - `replay.raw.host`: not exactly one `Host` naming the target origin, in any case, with its port, which may be left
      out only when it is the scheme's default; with an absolute URL as the target, `Host` must be exactly the
      authority written there;
    - `replay.raw.connection`: `Connection` other than exactly `close`, or `Keep-Alive`, `Proxy-Connection` or `TE`;
    - `replay.raw.expect` and `replay.raw.upgrade`: `Expect`, `Upgrade`, `HTTP2-Settings` or a WebSocket field;
    - `replay.raw.trailer`: a `Trailer` field, a chunk extension or a trailer field;
    - `replay.raw.framing`: a `Content-Length` that is not the body's length (digits only, no leading zero),
      `Transfer-Encoding` other than exactly `chunked`, both of them, incomplete chunks, or bytes after the body;
    - `replay.raw.body-not-allowed`: a body for `HEAD` or `TRACE`;
    - `replay.raw.target-denied`: a `--target` IP address that no request may be sent to: an unspecified (`0.0.0.0`,
      `::`), multicast or limited broadcast (`255.255.255.255`) address, or an IPv4-mapped IPv6 form of one. Private
      and loopback addresses are allowed: Raw mode is meant for servers that you own or may test. A `--target` host
      name is checked when it is looked up, once its send has begun: when every address it resolves to is denied,
      that send fails with `replay.raw.target-denied` and is recorded like any failed send (exit code 12), rather
      than refused before anything is sent.
  - `--fix-content-length` first replaces the value of the request's one `Content-Length` with the body's length, as
    Fiddler's Fix Content-Length does; it needs exactly one `Content-Length` and no `Transfer-Encoding`
    (`replay.raw.fix-content-length`). The file itself is never changed.
  - Consent: without `--allow-raw`, nothing is sent (`replay.policy.raw`, exit code 3). The checks refuse what xmat
    can see as ambiguous, but they do not prove how a front end, proxy or load balancer between xmat and the server
    reads the request: those can read it differently from the server behind them, and it can affect other users'
    requests on a back-end connection that they share. `Connection: close` binds only xmat's own connection. Use Raw
    mode only with servers that you own or may test. A method other than `GET`, `HEAD` and `OPTIONS`, and `GET` or
    `OPTIONS` with a body, also needs `--allow-unsafe-methods` (`replay.policy.raw-unsafe`): a server that does not
    expect a body may read it as another request.
  - Raw mode sends no proxy, client certificate, Windows authentication or automatic cookies and credentials: only the
    fields in the file. It follows no redirect, retries nothing, and never sends HTTP/2, `Expect`, an upgrade or
    `CONNECT`. Its one transport option is `--connect-to`, for an HTTPS target only, with `--allow-connect-to`. With
    `--raw`, `--url`, `--method`, `--header`, `--remove-header`, `--clear-headers`, `--body-file`, `--empty-body`,
    `--form`, `--form-string`, `--form-type`, `--request-file`, `--sequence`, `--proxy`, `--allow-proxy`,
    `--client-cert`, `--allow-client-cert`, `--windows-auth`, `--allow-windows-auth`, `--allow-origin-change`,
    `--http-version` other than `1.1` and `--follow-redirects` other than `0` are usage errors (exit code 2), and so are
    `--target`, `--allow-raw` and `--fix-content-length` without `--raw`. `replay`, `save-request` and `curl` take none
    of these four options.
  - Over HTTPS, xmat offers only `http/1.1` (`replay.raw.alpn` when the server chooses another protocol), never
    resumes a session and offers no client certificate. It checks the server's certificate as Windows does, with two
    limits. Revocation is not checked (`NoCheck`), as in every replay path, so a revoked certificate can pass when the
    rest of its validation succeeds. And Raw mode turns certificate downloads off: validation never fetches an issuer
    certificate from a URL in the server's certificate and fetches no revocation data, so a raw HTTPS server must send
    every intermediate certificate that this PC does not already have in its certificate stores or cache; Windows'
    automatic root update may still ask Windows Update about an unknown root. Other sends keep Windows' default, which
    allows such downloads, so for them `NoCheck` is no promise that validation makes no network request; one policy
    for every replay path is an open follow-up.
  - xmat reads one HTTP/1.x response within Raw mode's limits (`replay.raw.response-invalid`), with a body framed by
    `Content-Length`, `chunked` alone (`replay.raw.response-coding` otherwise) or the end of the connection. A body that
    the connection ends cannot be proven complete, and one that ends early is `replay.raw.response-incomplete`.
    `response.http` is the normalized response: a chunked body decoded, with `Content-Length` in place of its
    `Transfer-Encoding` and `Trailer` fields; its trailer fields are counted but their values are not kept.
  - `replays/NNNNN/request.http` is the request's exact bytes, the attempted request, whatever happened to it. The
    send's `requestWrite` says how much of it xmat handed to the connection: `none`, `partial`, `complete`, or
    `uncertain` when a write failed or was interrupted; `requestBytesWritten` counts those bytes. Neither proves what
    the server received, and `complete` does not either.
- `xmat curl` exports requests as cURL scripts and sends nothing: the requests that `INPUT --request SPEC` selects,
  the request of a `--request-file`, or a new request with `--url`, after the same edits and completions as
  `save-request`. Each script is a PowerShell 7 file that runs the `curl.exe` of Windows (in `System32`) with arguments
  that make curl send what xmat sends over HTTP/1.1: the same request line, `Host`, header lines in the same order and
  body bytes. Two things differ: a header value that is not ASCII, which xmat does not send, goes out as its UTF-8
  bytes, and an empty value as `Name:` rather than `Name: `.
  A script holds its request as captured, credentials, cookies and body included, so `curl` needs
  `--include-sensitive`: without it, xmat refuses before it reads any input or creates anything
  (`replay.curl.consent-required`, exit code 3). A request whose origin an edit changed also needs
  `--allow-origin-change` (`replay.policy.origin-change`, exit code 3). xmat publishes `DIR/summary.json` (schema
  `xmat-curl/1`), `DIR/scripts/NNNNN.ps1`, one script per request, and `DIR/bodies/NNNNN.bin` for each body that is
  not empty, at most 10,000 scripts (`replay.plan.too-large`, exit code 3, checked before any request is built). The
  JSON output and `summary.json` give, for each script, its body's size, the HTTP version it asks for, whether it needs
  a `curl.exe` with Unicode support, and how many header and query parameter names the sanitized export would treat as
  secret: never a name or a value.
- A request that curl is not known to send the same way is refused (exit code 3): a request file that asks for HTTP/2
  only (`replay.curl.unsupported`; pass `--http-version auto` or `1.1` to export it), a URL with a `#`
  (`replay.curl.unsupported`), HEAD with a body (`replay.curl.head-body`), a URL and headers too long for a Windows
  command line (`replay.curl.arguments-too-large`), a Fiddler control header (`replay.request.control-header`), a
  request that still leaves anything out (`replay.request.incomplete`) and an invalid request
  (`replay.request.invalid`), such as one with a header value that is not well-formed Unicode. `--http-version 2` is a
  usage error, and so are the options that only sending uses: `--repeat`, `--parallel`, `--timeout`,
  `--max-response-mib`, `--follow-redirects`, `--connect-to`, `--allow-connect-to`, `--proxy`, `--allow-proxy`,
  `--client-cert`, `--allow-client-cert`, `--windows-auth`, `--allow-windows-auth`, `--sequence`,
  `--allow-unsafe-methods`, `--verify`, `--verify-diff` and `--origin` (`cli.usage.unsupported-option`, exit code 2).
- A script needs PowerShell 7.2 or later on Windows. It refuses to send unless `curl.exe` is at least version 8.21.0
  and, when the URL or a header is not ASCII, supports Unicode. It runs curl without a configuration file (`-q`) and
  with only `SystemRoot`, `windir`, `SystemDrive`, `TEMP` and `TMP` in its environment, so no proxy, CA bundle or other
  curl setting from your profile or environment applies. It reads its body from `../bodies/NNNNN.bin` next to it and
  sends nothing unless that file still has the exact length and SHA-256 that the script holds. That check only catches
  a body file that changed apart from its script: whoever can change the script can also change its URL, headers, body
  file, length and hash, so keep scripts and body files as private as the capture. Its exit code is
  curl's, or 1 when the script refused or failed before curl finished; when it fails or is interrupted, it stops the
  curl that it started, and an interrupted script leaves `$LASTEXITCODE` at 1 in the PowerShell session that ran it.
- A script asks curl for exactly HTTP/1.1 when the request is exported with `--http-version 1.1`, or from a request
  file that asks for `1.1`. With `auto`, curl chooses the version itself, which need not be the one that xmat would use
  (xmat sends HTTP/2 to an HTTPS server that offers it). The rest is curl's own behavior, not xmat's: curl trusts the
  certificates that Windows trusts and also checks that the server's certificate was not revoked, which xmat does not,
  so a certificate whose revocation Windows cannot check fails in the script; it follows no redirect; it has no overall
  time limit and no limit on the size of a response, unlike `xmat replay` with `--timeout` and `--max-response-mib`;
  and it writes the response body, and any error, to the console.
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
- `xmat compose --raw` is the one exception: a raw send that began is always recorded. A cancellation before the first
  raw send began publishes nothing, as above (exit code 130, `cli.cancelled`). Once a raw send began, a cancellation
  ends the sends in flight as cancelled (`replay.send.cancelled`), starts no further send, and publishes the sends
  that began, and only those: `summary.json` and the JSON document then have the status `cancelled`, those sends'
  items, files and `replays.saz` sessions, and the errors `replay.cancelled` and `cli.cancelled-after-send`, with
  exit code 130. A run whose sends all ended before the cancellation keeps its outcome (exit code 0 or 12) and is
  published. Either way, publication (the sessions of `replays.saz` and the archive's completion, `summary.json`, the
  removal of the work folder and the rename) then runs on a finalization of its own, which the cancellation does not
  stop but bounds: if it has not finished 30 seconds after the cancellation arrived, even in the middle of a send's
  session, nothing is published and the run exits with code 130 (`cli.cancelled`). A write that fails, of a send's
  files or session or of the publication, publishes nothing either, even if the run was cancelled meanwhile (exit code
  6). In both cases the JSON document has no `cli.cancelled-after-send`. A second Ctrl+C during the finalization still
  ends xmat at once, and after a console close, logoff or shutdown Windows still ends xmat a few seconds later: `DIR` is
  then complete or absent, and the `.xmat-staging-*` folder can remain next to it.

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
`unconditional`, `verify`, `verifyDiff`, `allowUnsafeMethods`, `allowOriginChange`, `httpVersion` (the version that
the run asks for: `--http-version`, or else the request file's, and `1.1` for `auto` with `--windows-auth`), `connectTo`, which
says whether `--connect-to` was given but never its endpoint, `allowConnectTo`, `proxy`, which says whether `--proxy`
was given but never its URL, `allowProxy`, `clientCertificate`, which says whether `--client-cert` was given but never
its thumbprint, `allowClientCertificate`, which says whether `--allow-client-cert` was given but never its origin,
`windowsAuth`, which says whether `--windows-auth` was given, `allowWindowsAuth`, which says whether
`--allow-windows-auth` was given but never its origin,
`saveSaz`, and `raw`, `allowRaw` and `fixContentLength`, which say whether `--raw`, `--allow-raw` and
`--fix-content-length` were given but never the file or the `--target` origin), `replays` (one per send,
in order, each with its `id`, `device` and `request` (null for `compose`), `sequence` (the number that `--sequence` put
in the URL, otherwise null), `iteration`, `outcome`, `status` (null without a response), `durationMs`, `responseBytes`,
`redirects`, `transport` (`direct`, `connect-to` or `proxy`, as the send was configured), `httpVersion` (the version
asked for:
`auto`, `1.1` or `2`), `responseVersion` (the version of the response, such as `1.1` or `2`, null without one),
`authHeaderSchemeObserved` (with `--windows-auth`, the `Authorization` label that the send's final request carried,
`Negotiate` or `NTLM`, and otherwise null: a header label only, which proves neither that authentication succeeded nor
which package was negotiated, while null does not prove that none was used), `raw` (whether the send was a
`compose --raw` send), `requestWrite` and `requestBytesWritten` (a raw send's write state, `none`, `partial`,
`uncertain` or `complete`, and the bytes of its request that xmat handed to the connection, which prove nothing about
what the server received; null for other sends), `responseFraming` (a raw send's response framing, `content-length`,
`chunked`, `close` or `none`, null without a response and for other sends), `code`,
`verify` (null without `--verify`, otherwise its `result`, `match` or `mismatch`, its `differences`, a list of `status`
and `body`, and its `detail`, null without `--verify-diff`, otherwise the `state` of its `diff.json` and, once
published, its `file`) and, once published, its `files`) and `totals` (`replays`, `completed`, `failed`, `matched` and
`mismatched`); `replay` also reports `input` and `limits` like `analyze`. `summary.json` of a replay holds `schema`
(`xmat-replay/1`), `command`, `runId`, `version`, `status` (as in the JSON output: `cancelled` only for a `compose --raw`
run published after a cancellation), the same fields, and the run's `warnings` and `errors`.

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
| 3 | Invalid input or nothing to analyze: the input is missing, unreadable, unsupported, corrupt or over a safety limit, or `--device` names no device; for `replay` and `compose` also a selection, edit, sequence, `--body-file` or `--form` body, HTTP version, connect-to endpoint, proxy, client certificate or Windows authentication that cannot be used, a Fiddler control header, a `--raw` file or request that Raw mode refuses, or a request that needs `--allow-raw`, `--allow-unsafe-methods`, `--allow-origin-change`, `--allow-connect-to`, `--allow-proxy`, `--allow-client-cert` or `--allow-windows-auth` for its origin (nothing is sent) |
| 4 | A prerequisite is missing or damaged, such as the analysis data next to `xmat.exe` |
| 5 | Conflict: `--out` already exists |
| 6 | The operation failed, for example the output could not be written, or `--out` is not on a local NTFS volume reached without a network path, substituted drive or link (`cli.output.unprotected`) |
| 7 | Cleanup failed: a staging folder could not be removed (overrides the other codes, cancellation included) |
| 10 | `--fail-on findings` and the rules reported findings |
| 11 | `--verify` (or `--verify-diff`) and a replayed response differs from the captured one (the results are published) |
| 12 | A replayed or composed request got no complete response (the results are published) |
| 70 | Internal error, including a `help`, `--version` or passing `doctor` run whose JSON document could not be produced (`cli.internal.output`) |
| 130 | Cancelled: nothing was published, except by `compose --raw` once a raw send began (`cli.cancelled-after-send`) |

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
| Compose and send a new request (`request.compose`) | `xmat compose --url URL \| --request-file FILE \| --raw FILE --target ORIGIN --out DIR [--save-saz] [--allow-raw] [--fix-content-length] [--sequence START-END] [--method METHOD] [--header "NAME: VALUE"]... [--remove-header NAME]... [--clear-headers] [--body-file FILE] [--empty-body] [--form NAME=VALUE\|NAME=@FILE]... [--form-string NAME=VALUE]... [--form-type N=TYPE]... [--repeat N] [--parallel N] [--follow-redirects N] [--timeout SECONDS] [--max-response-mib N] [--http-version auto\|1.1\|2] [--connect-to IP:PORT] [--proxy URL] [--client-cert THUMBPRINT] [--allow-client-cert ORIGIN] [--windows-auth] [--allow-windows-auth ORIGIN] [--allow-unsafe-methods] [--allow-origin-change] [--allow-connect-to] [--allow-proxy]` | Supported (S9) |
| Export requests as cURL scripts for PowerShell 7, without sending them (`request.curl`) | `xmat curl INPUT --request SPEC \| --request-file FILE \| --url URL --out DIR [--include-sensitive] [--device ID] [--method METHOD] [--header "NAME: VALUE"]... [--remove-header NAME]... [--clear-headers] [--body-file FILE] [--empty-body] [--form NAME=VALUE\|NAME=@FILE]... [--form-string NAME=VALUE]... [--form-type N=TYPE]... [--unconditional] [--http-version auto\|1.1] [--allow-origin-change] [--max-input-mib N] [--max-data-mib N] [--max-record-mib N] [--max-records N]` | Supported (S9) |
| Replay captured requests, as captured or edited (`request.replay`) | `xmat replay INPUT --request SPEC --out DIR [--save-saz] [--device ID] [--repeat N] [--parallel N] [--unconditional] [--verify] [--verify-diff] [--url URL \| --origin URL] [--method METHOD] [--header "NAME: VALUE"]... [--remove-header NAME]... [--body-file FILE] [--form NAME=VALUE\|NAME=@FILE]... [--form-string NAME=VALUE]... [--form-type N=TYPE]... [--follow-redirects N] [--timeout SECONDS] [--max-response-mib N] [--http-version auto\|1.1\|2] [--connect-to IP:PORT] [--proxy URL] [--client-cert THUMBPRINT] [--allow-client-cert ORIGIN] [--windows-auth] [--allow-windows-auth ORIGIN] [--allow-unsafe-methods] [--allow-origin-change] [--allow-connect-to] [--allow-proxy] [--max-input-mib N] [--max-data-mib N] [--max-record-mib N] [--max-records N]` | Supported (S9) |
| Save requests as request files, without sending them (`request.save`) | `xmat save-request INPUT --request SPEC \| --request-file FILE \| --url URL --out DIR [--device ID] [--method METHOD] [--header "NAME: VALUE"]... [--remove-header NAME]... [--clear-headers] [--body-file FILE] [--empty-body] [--form NAME=VALUE\|NAME=@FILE]... [--form-string NAME=VALUE]... [--form-type N=TYPE]... [--unconditional] [--http-version auto\|1.1\|2] [--include-sensitive] [--allow-origin-change] [--max-input-mib N] [--max-data-mib N] [--max-record-mib N] [--max-records N]` | Supported (S9) |
| List, inspect, recover or clean up headless runs (`run.records`) | (not available yet) | Planned (S4, XMAT-021) |
| Validate a capture script without running it (`script.validate`) | (not available yet) | Planned (S8a, XMAT-021) |
| Report navigation and analysis tabs (`view.analysis-navigation`) | (none) | GUI only. Presentation: moves between report views and tabs |
| Capture viewers (`view.capture-viewers`) | (none) | GUI only. Presentation: filters, timeline and the request, response, WebSocket and packet views |
| Composer history (`view.composer-history`) | (none) | GUI only. Presentation: session-only navigation over rows already in the Replays tab; sends go through request.replay, and headless runs keep no session |
| Control templates (`view.controls`) | (none) | GUI only. Presentation: scroll bar commands inside control templates |
| Display language (`view.language`) | (none) | GUI only. Presentation: the command line emits stable codes and English diagnostics |
| Main window (`view.main-window`) | (none) | GUI only. Presentation: hosts the GUI adapters of every capability |
| Preferences window (`view.preferences-editor`) | (none) | GUI only. Presentation: edits saved GUI preferences; headless runs take per-run options instead |
| Script editor (`view.script-editor`) | (none) | GUI only. Presentation: editor buffer, save, revert and error navigation; headless scripts are files |
| Light and dark theme (`view.theme`) | (none) | GUI only. Presentation: colors only |
| Choose the PC address a console sends traffic to (`xbox.source-address`) | (not available yet) | Planned (S7, XMAT-021) |
<!-- xmat-capabilities:end -->
