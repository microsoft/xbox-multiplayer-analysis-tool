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
| Configure proxying, inspect, edit and resend requests, view WebSockets, and manage hosts | [Web proxy](docs/web-proxy.md) |
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
