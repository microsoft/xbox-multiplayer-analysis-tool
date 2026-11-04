# Web proxy

[Back to README](../README.md) | [Getting started](getting-started.md)

Capture and inspect web requests, responses, and WebSocket messages from a device.
XMAT automates the console proxy setup traditionally described under "Fiddler" in
the GDK documentation.

## Prerequisites

Check the [device requirements](../README.md#requirements) and start XMAT.
An Xbox kit must be reachable and ready for the reboots required when enabling or
disabling proxying. Generic devices must support a certificate and manual proxy settings.

## Add a device

1. Click the **+** tab and choose a device and the web proxy capture method.
2. Select **Default Console** to use the designated default console without
   entering its IP address, or specify another device.
3. Click **Add capture**. A new device/capture tab opens with an empty connections list.

![Xbox development kit before proxying is enabled](../ReadmeImages/1.png)

The kit shown above runs the June 2021 recovery and is set to the Dev Home experience.

![Adding the default console for web proxy capture](../ReadmeImages/2.png)

### Automatic proxy ports

Open **Edit > Preferences > Web Proxy** to change the available automatic proxy ports.
The example below uses port 8080.

![Web Proxy preferences showing the available proxy ports](../ReadmeImages/3.png)

Changing the automatic range does not change an existing device session's assigned
port or free its reservation. Each open capture keeps its port reserved even while
stopped and after remembered devices are restored.

Only one managed proxy endpoint is supported per device. Adding a known device
selects its existing tab rather than creating a second proxy session. A separate
Network Trace capture and read-only imported captures are still supported.

## Enable console proxying

1. Select the console's capture tab.
2. Make sure the kit is ready to reboot, then click **Enable Xbox Proxy**.
3. Wait for the console to restart, then generate web traffic from an app.

**Warning:** enabling proxying reboots the kit and starts the local proxy immediately.
Proxy settings persist across console reboots and XMAT restarts until you explicitly
disable them.

![Enabling Xbox proxying from the capture toolbar](../ReadmeImages/4.png)

The console's "Fiddler enabled" notification indicates successful proxy setup even
though Fiddler itself is not running.

![Console notification confirming that proxying is enabled](../ReadmeImages/5.png)

**Expected result:** connections populate the capture's connections list.

![Proxied web traffic in the connections list](../ReadmeImages/6.png)

**Stop Capture** stops only the local listener; it does not disable or reboot the
console. **Disable Xbox Proxy** remains available while capture is stopped or after
a failed start, so clearing stale console settings does not require starting a listener.

### Check device settings

**Check Proxy Status** reports the Xbox endpoint instead of treating any nonempty
proxy host as a match. Re-enabling a verified matching endpoint does not rewrite
settings or request another reboot. A different existing endpoint is not overwritten
automatically.

This action is not available for Local PC captures. Generic/manual devices offer
**Proxy Setup Details**, which displays connection instructions rather than querying
device state.

If endpoint verification fails, see [Xbox proxy recovery](troubleshooting.md#xbox-proxy-recovery).

## Change a proxy port

1. Select the device's existing tab and stop capture.
2. Choose **Actions > Change Proxy Port**.
3. Choose an unassigned port from **1 through 65535**. It may be outside the automatic
   range in Preferences.
4. Choose when to apply the change:

| Choice | Behavior |
|---|---|
| **Apply now** | Starts the replacement listener before changing device settings. Xbox proxying is disabled/re-enabled with the required restarts; the console may reboot twice. The popup explains this before consent. Other devices remain stoppable during restarts. |
| **Later** | Saves a pending replacement without changing the device or current assigned port. Both ports stay protected, including after XMAT restarts. Starting/enabling the capture offers the change again; **Discard pending change** keeps the current port. |
| **Cancel** | Closes the popup without accepting a new selection. |

Capture history, filters, and selection are retained. The bottom status bar shows
the selected capture's running/stopped state, assigned port, and pending-change or
device-configuration details. Endpoint confirmation is separate from local capture
activity, so stopping capture does not leave a message claiming it is still running.

**Warning:** canceling an apply prevents later steps, but a console reboot or SDK
operation already requested cannot be undone. An in-flight mutation must settle.

XMAT reports binding, saving, or configuration failures and preserves assignments
needed for recovery rather than silently substituting ports. Once a replacement is
saved and the device may be using it, uncertain activation does not automatically
tear down its listener or revert to the old port. For Xbox, use **Check Proxy Status**
before retrying, stopping, or disabling.

For generic/manual devices, XMAT cannot edit settings remotely. Stop the device/app
using the old endpoint, confirm that in the popup, then update its settings to the
new endpoint shown by XMAT. Different apps can use independently configured generic
listeners, but two global proxy endpoints do not duplicate the same device traffic.

## Inspect requests and responses

1. Launch an app on the kit, such as the ATG NetRumble sample.
2. Select a connection to view its request and response.
3. Use **Headers** and **Body** to inspect formatted data and the connection details
   to see request time and duration.

![Xbox services sign-in traffic from the ATG NetRumble sample](../ReadmeImages/7.png)

![Headers, body, and timing for a selected connection](../ReadmeImages/8.png)

**Auto Scroll** is enabled by default to follow incoming connections. Turn it off
to keep a request in view. Status codes appear when responses arrive.

### Filter connections

Use the toolbar lists to filter by status code, host, or method. Lists contain the
values found in the capture; only checked values are shown. Filters combine and
update as traffic arrives. **ALL/NONE** helps include or exclude a small set of values.
Status codes of 400 or higher are colored red.

![Filtering connections by host](../ReadmeImages/9.png)

![Combining host and status-code filters](../ReadmeImages/10.png)

### Capture timeline

The Waterfall and By Host views retain complete capture history, including gaps
between capture sessions. Only visible request bars, host labels, and time-axis ticks
are rendered, so scrolling into older history does not rebuild the entire timeline.

Incoming changes are combined into at most ten timeline refreshes per second. This
limits presentation only, not request capture or storage. Scrolling, resizing,
switching modes, and stopping refresh the visible timeline without waiting for the
live-update cadence. Stopping displays final response statuses and durations even
if no further requests arrive.

## Edit and resend a request

1. Right-click an HTTP request in the connections list and select **Edit and resend**.
2. Edit the method, URL, headers, or body in the composer window.
3. Click **Send** and inspect the new response. The original capture is not modified.

Loaded and imported captures can be resent too. Binary, compressed, and non-UTF-8
request bodies are preserved as editable Base64; text bodies use UTF-8.

Requests are sent directly from this PC, bypassing system proxies and capture scripts.
HTTPS certificates are validated, redirects are not followed unless you select **Follow
same-origin redirects** (see below), and response cookies are not reused. **Captured
headers, including credentials, are sent to the edited URL.** Host and Content-Length
are regenerated, and connection-only headers are omitted. Methods other than exactly
GET, HEAD, and OPTIONS, in capitals, require confirmation, as does changing the captured
origin. CONNECT and WebSocket handshakes cannot be resent.

Use **Cancel request** to stop waiting, or **Copy as cURL (PowerShell 7)** to copy a
command for PowerShell 7 with `curl.exe` available. **Copied commands can contain
credentials and body data.** Sending has a 30-second timeout and a 16 MiB response
limit for each request. Cancelling or timing out cannot undo changes already processed
by the server.

To send a request that was never captured, select **Actions > Compose a New Request**.
The composer opens with an empty GET request and works the same way.

Each send from the composer is also recorded in the **XMAT Replays** tab, like the
requests that **Replay** sends (see below). A send that you cancel is not recorded, but
it may already have reached the server, and the status line says so. While a replay, an
analysis, or a capture file operation such as an import runs, the composer does not
send and says so; send again when it has finished.

### Composer options

- **Follow same-origin redirects, at most** *N* is off by default; *N* is 1 to 10, and
  10 at first. XMAT follows a redirect only to the scheme, host, and port that the
  request was sent to, and never from HTTPS to HTTP, so captured headers and the body
  never go to another origin. Each redirect followed is another request: after a 303,
  or a 301 or 302 that answers a POST, it is a GET without the body, as browsers send
  it. When XMAT does not follow a redirect, the redirect response is the result and the
  status line says why. Without the option, a redirect response is shown as it is.
- **Inspect session** is off by default. When it is selected, XMAT selects the last
  request of the send in the **XMAT Replays** tab once the send has ended, also when
  that request failed or timed out. It selects nothing when you cancelled the send,
  when nothing was recorded, or when the tab or the composer was closed meanwhile.

### Numbered URLs

As in Fiddler's composer, a `#` in the path or query of the URL sends one request per
number. **Send** asks for **Start At** and **End At**, 1 and 1 at first, and every `#`
in the URL is replaced by each number in turn. A leading `0` in **Start At** pads every
number to its width: `http://example.com/#/?a=#` from `08` to `11` sends `/08/?a=08`,
`/09/?a=09`, `/10/?a=10`, and `/11/?a=11`. Only the URL changes; a `#` in a header or
the body is sent as it is. Write `%23` for a literal `#` in the URL. A `#` in the
scheme, host, or port is refused, so numbers never lead to other hosts.

The requests are checked before anything is sent and go out one at a time, at most
10,000 of them. When they need confirmation, one question covers them all and says how
many there are. **Cancel request** stops before the next request: the requests already
sent stay in the **XMAT Replays** tab. The composer shows the last response only.
**Copy as cURL** is not available for a numbered URL or while **Follow same-origin
redirects** is selected, since the command would not send the same requests.

### Upload a body from a file

**Upload File** replaces the whole body with the bytes of a file, up to 64 MiB. The file
is read once, when you choose it: later changes to it are not sent, and the composer
keeps only its bytes and its name. Headers such as `Content-Type` are not changed.
**Clear body** removes the uploaded body. A file that cannot be read leaves the body as
it was. Fiddler's `<@INCLUDE@>` tokens and multipart forms are not supported: such text
is sent as typed, and no file is read.

### Drag a request into the composer

Drag a request from a connections list onto an open composer to edit and send it there.
One request is dragged at a time; CONNECT tunnels, protocol upgrades, and requests
recorded without their body cannot be dragged. The composer loads a copy of the request
as it was when the drag began, clears the previous response, and sends nothing. If you
have edited the request in the composer, it asks before replacing it. Only requests
dragged from XMAT can be dropped; text, links, and files are refused, and nothing can be
dropped while the composer sends or reads a file.

## Replay requests

Right-click a request, or one of several selected requests, and open **Replay**:

- **Reissue** sends the selected requests again, as captured, one after another.
- **Reissue unconditionally** first removes `If-Match`, `If-None-Match`,
  `If-Modified-Since`, `If-Unmodified-Since` and `If-Range`, so the server sends a full
  response instead of 304 Not Modified.
- **Reissue and verify** then compares each response with the captured one, by status
  code and body (without chunked framing or gzip, deflate or Brotli encoding), and lists
  the requests whose response differs. Headers are not compared. A request whose
  captured response was recorded without its body (see the body limit below) is not
  verified: XMAT counts it as not verified, neither matched nor differed, and warns.
- **Reissue several times** sends the selected requests 2 to 100 times, all of them in
  turn before the next round.
- **Cancel replay** stops a replay that is running.

Replays are sent the way the composer sends requests. Methods other than exactly GET,
HEAD, and OPTIONS, in capitals, need confirmation, which states how many requests the
replay sends, repeats included, and how many of them can change server data. The
requests are copied when you choose the menu item, so the confirmed requests are exactly
the ones sent, even if a running capture updates their rows meanwhile.

Each request that was sent appears in the read-only **XMAT Replays** tab, with its
response, or without one when the request failed or timed out. **Replay of** in the
request details shows where it comes from: the tab and request number of the captured
request, or that the request was composed from scratch. This link is kept in memory
only. You can inspect and analyze replays like captured traffic, and edit and resend
them.

When you cancel a replay, the requests already recorded stay in the tab, and XMAT tells
you how many there are. The request that was being sent when you cancelled is not
recorded, but it may already have reached the server, which may have processed it.

The tab belongs to the replay feature. No capture can have its name, so its rows never
mix with a capture's, in analysis or in saved captures; a loaded or imported tab is
never used for replays, and loads and imports never change the replay tab. While
another tab with that name is open, such as one loaded from a saved capture, replays go
to **XMAT Replays (2)**, and so on.

**File > Save** saves the **XMAT Replays** tab with the other tabs, and opening the saved
capture shows it as an ordinary read-only tab, without **Replay of**. **File > Export**
to a Fiddler archive includes its requests.

The **XMAT Replays** tab keeps at most 256 MiB of request and response bodies, counting
the rows it already has. This limits the bodies it keeps, not XMAT's memory use. A
request whose bodies do not fit still gets its row, with the status, headers, and timing
but without those bodies. A note in the request details says so, and an
`X-XMAT-Request-Body-Not-Kept` or `X-XMAT-Response-Body-Not-Kept` header in the
recorded request or response gives the size of the body that was not kept, so saved
captures and exports never show it as an empty body. A request whose body was not kept
is never sent again, from the GUI or with `xmat replay`. After the replay, XMAT tells
you how many bodies were not kept. Close the tab to make room for new bodies: the next
replay starts a new one.

While a replay or a send from the composer is in progress, XMAT does not load, import,
save, or export captures, analyze, start a capture, or close tabs; **Cancel replay**
stops a replay. Exiting XMAT cancels a replay or send in progress and waits for it to
stop before closing. When Windows ends your session, or XMAT restarts itself as
administrator, a replay or send in progress is cancelled without waiting, and nothing
more is recorded.

The command line does the same with `xmat replay` and `xmat compose`; see
[Command line](cli.md).

## Block or bypass hosts

The **Block List** and **Bypass List** are shared by every capture in the current
session, including imported captures and captures created later. Closing a tab keeps
them; exiting XMAT clears them. Each capture's list tabs edit the same lists, so
changes, including **Clear All**, affect every capture.

1. Right-click a host in the connections list, a timeline bar or By Host label, or
   an analysis Per Endpoint summary row or details header.
2. Add or remove that exact host. **Remove** appears only for an existing exact entry;
   the menu notes any wildcard that still matches.
3. Use **Actions > Manage Host Lists...** to open the editors even without a selected capture.

The menu key acts on the focused row, not the selected row.

| List | Effect |
|---|---|
| **Block** | Captures requests but does not forward them. Applies to new requests immediately and wins if a host is in both lists. An already-open bypassed tunnel continues until closed. |
| **Bypass** | Skips HTTPS decryption for new CONNECT tunnels; open connections retain their mode. Tunnels appear only as CONNECT rows. Scripts and path-based block patterns cannot inspect them. Plain HTTP is never bypassed. This is not the Windows proxy bypass list. |

### Entry rules

- A host entry covers every port, but not subdomains. Use `*.example.com` for subdomains.
- DNS names are case-insensitive, but a final dot matters: `example.com.` and
  `example.com` are different entries.
- IP addresses match by address: `[::1]`, `::1`, and `0:0::1` are one entry. A complete
  IPv6 address such as `2001:db8::1:443` is an address, not a host and port.
- Ports are not allowed: `example.com:443`, `example.com:`, and `[::1]:443` are
  rejected with an explanation.
- Entries containing `*` or `/` are glob patterns, such as `example.com/api/*`.
  A colon in a pattern is literal, not a port filter.
- Write IPv6 patterns without brackets, such as `2001:db8::*` or `::1/api/*`.
  A bracketed IPv6 pattern is accepted but never matches, including one that matched
  HTTP/2 traffic in earlier versions.

Analysis endpoints offer host actions only if every request came from the same
captured host. Synthesized telemetry endpoints (such as `inGameEvents`), endpoints
combining hosts, and older data without host provenance explain why no action is available.

## View WebSocket messages

WebSocket connections through the proxy are captured automatically.

1. Open the capture's **WebSockets** tab to see messages in send/receive order.
2. Filter by **direction** (client to server or server to client) or **message type**
   (text, binary, or close).
3. Select a message and inspect its payload as **text**, formatted **JSON**, or
   **hexadecimal** bytes.

Only the most recent messages are retained: up to **5,000 messages or 32 MB of
payload per capture**, whichever limit is reached first. Individual payloads are
captured up to **1 MB**. WebSocket messages are **not saved to or loaded from
`.XMATCAP` files**.

## Stop proxying safely

1. Use **Stop Capture** to stop the local listener. Active proxy connections are
   canceled and new requests are no longer forwarded; captured data is retained
   when stopping and restarting.
2. To restore normal console routing, use **Disable Xbox Proxy**. This reboots the kit.

If stopping takes more than half a second, a progress dialog shows the shutdown
phase and pending capture callbacks or UI updates while the app remains responsive.

**Warning:** a console with proxying still enabled cannot successfully use Xbox
services while XMAT is not proxying for it. Service status checks may become "unknown."

![Console showing unknown service status after the proxy listener stops](../ReadmeImages/11.png)

The **Edit > Preferences > Web Proxy > Prompt to disable Xbox proxy on close**
setting applies immediately to existing tabs. When enabled, closing a device tab
or exiting XMAT (including **File > Exit** and the window close button) prompts for
each reachable console whose proxy is enabled. **Yes** disables proxying and reboots
the console; **No** leaves settings unchanged. Imported read-only captures do not prompt.

Tabs left open when XMAT exits are remembered and reopened at the next start. Their
connections lists remain empty until proxying resumes.

![Remembered capture tab reopened after restarting XMAT](../ReadmeImages/12.png)

For unexpected exits and Local PC proxy restoration, see
[Troubleshooting](troubleshooting.md#if-xmat-closes-unexpectedly).
