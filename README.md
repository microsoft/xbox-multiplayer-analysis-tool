# Xbox Multiplayer Analysis Tool

[![XMAT Win64 Desktop Build](https://github.com/microsoft/xbox-multiplayer-analysis-tool/actions/workflows/dotnet-desktop.yml/badge.svg?branch=main)](https://github.com/microsoft/xbox-multiplayer-analysis-tool/actions/workflows/dotnet-desktop.yml)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/9612/badge)](https://www.bestpractices.dev/projects/9612)

Xbox Multiplayer Analysis Tool (XMAT) is a Windows desktop tool for debugging Xbox,
PlayFab, and other network traffic from PCs and Xbox development consoles.

- Capture and inspect HTTP/HTTPS requests, responses, and WebSocket messages.
- Proxy HTTP/1.1 and HTTP/2 over TLS, with ALPN negotiation and automatic HTTP/1.1 fallback.
- Modify traffic on the fly with C# scripts.
- Capture packet-level traffic and analyze service-call patterns and network issues.

![XMAT displaying captured web requests and response details](./ReadmeImages/8.png)

### HTTP/2 capture scope

For intercepted HTTPS, the device-to-XMAT and XMAT-to-origin connections negotiate independently.
An HTTP/2 client can reach an HTTP/1.1 origin, and an HTTP/1.1 client can reach an HTTP/2 origin.
The captured request version describes the client leg; the captured response version describes the
origin leg. Request and response records share a request number and, for HTTP/2 clients, the client
stream ID. Stream IDs are scoped to a connection, not globally unique.

Requests and responses are buffered completely before their respective script callbacks run.
Body edits are forwarded with a recalculated Content-Length, while received capture metadata remains
available to scripts. Connection-specific headers, including fields named by a Connection header,
are removed when forwarding regular intercepted requests and responses, without deleting them from
the capture model.

The origin can start responding before the buffered upload finishes: the upstream transport
reads and writes concurrently to avoid an HTTP/2 flow-control stall. XMAT still waits for
the complete response before running response scripts or sending it to the device.

This is buffered HTTP capture, not a claim of complete HTTP/2 feature coverage. Trailers are not
preserved or forwarded, so gRPC correctness is not guaranteed, including unary calls that depend
on status trailers. Long-lived streaming requires additional work. Cleartext
HTTP/2 (h2c) and HTTP/3/QUIC are not supported. HTTP/2 WebSocket extended CONNECT is not validated
by the HTTP request/response test matrix.
The plain-HTTP forwarding path is separate from the intercepted HTTPS path.

#### Buffered HTTP/2 acceptance

The automated `Http2ProxySupportTests` cover real loopback TLS/CONNECT traffic, including:

- All four HTTP/1.1 and HTTP/2 client/origin combinations, known and unknown body lengths,
  binary capture, and compiled script edits with regenerated lengths.
- Concurrent stream correlation, client cancellation, and capture-stop tunnel cleanup.
- Graceful origin connection retirement and replacement, origin resets before and during a
  response, upstream timeouts, canceled incomplete uploads, and early upload rejection.
- Three batches of eight concurrent 1,048,577-byte request/response transfers on reused
  connections, with exact client and captured body comparisons. This is a bounded regression
  workload, not a memory-leak or long-session certification.

Release acceptance also requires manual checks on a Windows PC and an Xbox development
console using the proxy and certificate setup below. Record the tested commit, device/OS,
client and origin protocols, workload, duration, and results:

1. Capture representative service traffic, including binary uploads/downloads and concurrent
   requests. Verify both negotiated protocol legs, matching request/response records, unchanged
   body bytes without scripts, and expected body edits with scripts. Exercise HTTP/1.1 fallback.
2. Run representative concurrent traffic for at least 30 minutes. Record request count,
   retained payload size, process memory, and managed-heap trends. Investigate memory growth
   not explained by retained captures; a short loopback run does not establish a safe memory
   ceiling. Confirm the application stays responsive.
3. Interrupt the origin connection and device network connection, restore them, and verify
   subsequent traffic succeeds. Stop capture with active traffic and restart it repeatedly;
   verify new captures work and neither the PC nor console is left using a stopped proxy.
4. Save, reload, and analyze the capture; verify body fidelity and request/response correlation.
   Record failures rather than marking an unrun or unsuccessful check complete.

Passing the automated suite alone does not establish live-device, GUI, or long-session
acceptance. Streaming, trailers/gRPC, h2c, HTTP/2 extended CONNECT, and HTTP/3 are separate
follow-ups, not capabilities certified by this checklist.

## Requirements

XMAT runs on **64-bit Windows 10 (build 17763 or higher) or Windows 11**.

| Capture device | Web proxy | Packet capture | Requirements |
|---|---|---|---|
| Xbox development kit | Yes | Yes | Xbox One or Xbox Series development kit running recovery 2106 or higher |
| Windows PC | Yes | Yes | Local packet capture requires running XMAT as administrator |
| Generic device | Yes | No | Must support a `.cer` certificate and configurable proxy IP address and port |

## Get started

Follow [Getting started](docs/getting-started.md) to obtain or build XMAT and capture
your first requests. Build prerequisites and commands are included in that guide.

**Xbox users:** enabling or disabling console proxying reboots the console. Stopping
a capture does not disable its proxy settings; a console left pointing at a stopped
proxy can lose access to Xbox services.

## Guides

| I want to... | Guide |
|---|---|
| Set up XMAT and make my first capture | [Getting started](docs/getting-started.md) |
| Configure proxying, inspect requests or WebSockets, and manage hosts | [Web proxy](docs/web-proxy.md) |
| Modify requests and responses with C# | [Scripting](docs/scripting.md) |
| Save, load, import, or safely share capture files | [Capture files and sanitized export](docs/capture-files.md) |
| Analyze Xbox service calls and export reports | [Live trace analysis](docs/live-trace-analysis.md) |
| Analyze captures from scripts or CI without the GUI | [Command line](docs/cli.md) |
| Capture and analyze packets from a console or PC | [Network capture](docs/network-capture.md) |
| Recover from proxy problems or collect diagnostic logs | [Troubleshooting](docs/troubleshooting.md) |

## Support and contributing

For bugs and feature requests, search existing GitHub issues before opening a new
one. For help using XMAT, contact xmat_oss@microsoft.com. See
[Support](SUPPORT.md) for the support policy and
[Troubleshooting](docs/troubleshooting.md#collect-debug-logs) for log collection.
**Logs may contain web request and response data; review them before sharing.**

Read [Contributing](CONTRIBUTING.md) before submitting changes. Developer background
is available in [Architecture](ARCHITECTURE.md); see the [Roadmap](ROADMAP.md) for
planning, the [Code of Conduct](CODE_OF_CONDUCT.md) for community expectations,
and [Security](SECURITY.md) for reporting security issues.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft trademarks or logos is subject to and must follow [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general). Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship. Any use of third-party trademarks or logos are subject to those third-party's policies.
