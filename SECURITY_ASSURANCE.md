# Security Assurance Case

This document provides the security assurance case for the Xbox Multiplayer
Analysis Tool (XMAT). It describes the threat model, trust boundaries, how
secure design principles are applied, and how common implementation weaknesses
are addressed.

---

## 1. Threat Model

### 1.1 Application Context

XMAT is a **local desktop diagnostic tool** for Windows used by Xbox game
developers. It captures, inspects, and analyzes Xbox Live web-service traffic
to identify compliance issues. It is not a server, does not accept inbound
connections from the internet, and is designed to run in a developer
workstation environment.

### 1.2 Assets

| Asset | Description |
|---|---|
| Captured network traffic | HTTP/HTTPS request and response data from Xbox consoles |
| Developer credentials | Xbox Live tokens that may appear in captured traffic |
| TLS interception certificates | Root CA and per-host certificates generated for proxy MITM |
| Analysis scripts | User-authored C# scripts executed by the scripting engine |
| Local SQLite databases | Captured data stored on disk during analysis sessions |

### 1.3 Threat Actors

| Actor | Motivation | Access |
|---|---|---|
| Malicious capture file | Crafted `.saz` file shared between developers | File-level input |
| Compromised Xbox console | Device sending malformed traffic through the proxy | Network input via local proxy |
| Malicious script | Untrusted C# script loaded into the scripting engine | Script execution context |
| Local attacker | Another user or process on the same workstation | File system, certificate store |

### 1.4 Threat Categories (STRIDE)

| Category | Applicable Threats |
|---|---|
| **Spoofing** | A malicious device could impersonate an Xbox console on the local network. Mitigated by explicit user-initiated device pairing via IP address. |
| **Tampering** | A crafted `.saz` file could contain malformed XML/JSON to exploit parsers. Mitigated by input validation and static analysis (see §4). |
| **Repudiation** | Low risk — single-user desktop tool with no multi-user audit trail requirement. |
| **Information Disclosure** | Captured traffic contains sensitive tokens and credentials. Mitigated by local-only storage and Windows user-level access controls. |
| **Denial of Service** | Decompression bombs or excessively large captures could exhaust memory. Mitigated by .NET runtime memory limits and process isolation. |
| **Elevation of Privilege** | C# scripting engine executes with application privileges. Mitigated by requiring explicit user action to load and run scripts (see §3.4). |

---

## 2. Trust Boundaries

The following diagram illustrates the trust boundaries in XMAT:

```
┌─────────────────────────────────────────────────────────────┐
│                    XMAT Process (Trusted)                    │
│                                                              │
│  ┌──────────────┐   ┌───────────────┐   ┌───────────────┐   │
│  │  WPF UI      │   │  Analysis     │   │  Scripting    │   │
│  │  Layer        │   │  Engine       │   │  Engine       │   │
│  └──────┬───────┘   └───────┬───────┘   └───────┬───────┘   │
│         │                   │                    │            │
│  ═══════╪═══════════════════╪════════════════════╪═══════ TB1 │
│         │                   │                    │            │
│  ┌──────┴───────┐   ┌──────┴────────┐   ┌──────┴───────┐   │
│  │  File I/O    │   │  SQLite DAL   │   │  Script      │   │
│  │  (SAZ, ETL)  │   │               │   │  Compiler    │   │
│  └──────┬───────┘   └───────────────┘   └──────┬───────┘   │
│         │                                       │            │
│  ═══════╪═══════════════════════════════════════╪═══════ TB2 │
│         │                                       │            │
│  ┌──────┴───────────────────────────────────────┴───────┐   │
│  │              MITM Proxy (Kestrel / ASP.NET Core)      │   │
│  │  ┌─────────────────┐      ┌───────────────────────┐   │   │
│  │  │ Certificate Mgr │      │ Forward Proxy Handler │   │   │
│  │  └─────────────────┘      └───────────────────────┘   │   │
│  └──────┬────────────────────────────┬───────────────────┘   │
│         │                            │                       │
└═════════╪════════════════════════════╪═══════════════════ TB3 ┘
          │                            │
   ┌──────┴──────┐              ┌──────┴──────┐
   │ Xbox Console│              │ Xbox Live   │
   │ (Device)    │              │ Services    │
   └─────────────┘              └─────────────┘
```

### Trust Boundary Descriptions

| Boundary | Description | Data Crossing |
|---|---|---|
| **TB1** — UI ↔ Core Logic | Separates user-facing controls from business logic. All user input (file paths, filter text, preferences) is validated before reaching core logic. | File paths, filter strings, configuration values |
| **TB2** — Core Logic ↔ I/O | Separates application logic from file parsing, database access, and script compilation. Untrusted data (`.saz` contents, capture files, user scripts) enters the application here. | Raw file bytes, ZIP entries, XML/JSON payloads, C# source code |
| **TB3** — Process ↔ Network | Separates the XMAT process from external network entities. The MITM proxy accepts connections from Xbox consoles and forwards to Xbox Live services. All network data is untrusted. | Raw HTTP requests/responses, TLS handshakes, WebSocket frames |

---

## 3. Secure Design Principles

### 3.1 Least Privilege

- The application runs as a **standard user process** — no administrator
  privileges required for normal operation.
- The proxy binds to **localhost or a developer-specified address** only; it
  does not listen on all interfaces by default.
- Certificate installation uses the **CurrentUser** certificate store (not
  LocalMachine), limiting scope to the running user.
- Device connections require explicit user-initiated pairing by IP address —
  no automatic network discovery or broadcast listening.

### 3.2 Defense in Depth

Multiple layers of security controls are applied:

| Layer | Control |
|---|---|
| **CI Pipeline** | CodeQL semantic analysis on every push to `main` and on weekly schedule |
| **CI Pipeline** | OpenSSF Scorecard supply-chain assessment (weekly) |
| **CI Pipeline** | Dependency review on every pull request blocks known-vulnerable packages |
| **Build System** | `TreatWarningsAsErrors=True` — all compiler warnings are build-breaking |
| **Build System** | `AnalysisLevel=latest-recommended` — Roslyn CA/IDE analyzers enforced |
| **Build System** | `.editorconfig` enforces coding standards including security-relevant rules |
| **Runtime** | .NET managed runtime provides memory safety, bounds checking, type safety |
| **Runtime** | ASP.NET Core Kestrel server handles HTTP parsing with built-in protections |
| **Process** | Microsoft Security Development Lifecycle (SDL) vulnerability reporting via MSRC |

### 3.3 Fail Secure

- Network capture parsing wraps all deserialization in `try/catch` blocks —
  malformed data is logged and skipped, never silently accepted.
- HTTP header parsing via `WebHeaderCollection` rejects invalid headers; parse
  failures are caught, logged, and the offending header is discarded.
- The proxy's forward connection handler validates URIs with `Uri.TryCreate()`
  before forwarding — malformed targets are rejected.
- Analysis engine rules produce explicit `RuleResult` objects with pass/fail
  outcomes — ambiguous states are treated as violations.

### 3.4 User Consent and Explicit Action

The following security-sensitive operations require explicit user action:

- **MITM proxy activation** — The user must manually start the proxy and
  configure the target device. The proxy does not start automatically.
- **Root CA installation** — Certificate installation occurs only when the user
  initiates a capture session. A system prompt confirms trust store modification.
- **Script execution** — Users must explicitly load, review, and run C# scripts.
  Scripts are not auto-executed on file open or application start.
- **Device pairing** — Console connections require the user to enter the device
  IP address manually.

### 3.5 Minimal Attack Surface

- The application is a **local desktop tool**, not a network service. It has no
  listening ports except the explicitly user-activated MITM proxy.
- No telemetry or analytics data is transmitted.
- The application does not auto-update or download remote resources.
- Build output is a self-contained Windows desktop application with no web
  endpoints.

---

## 4. Common Implementation Security Weaknesses

This section addresses how common weakness categories (aligned with
[CWE Top 25](https://cwe.mitre.org/top25/archive/2024/2024_cwe_top25.html))
are countered.

### 4.1 Injection (CWE-89, CWE-79, CWE-78)

| Weakness | Mitigation |
|---|---|
| **SQL Injection** | The SQLite data abstraction layer (`SqLtDataset`, `SqLtDataTable`) uses `FieldDefinition.EscapeText()` to escape quotes in all field values before query construction. Table and column names are controlled by application code, not user input. |
| **Cross-Site Scripting** | Not applicable — XMAT is a desktop WPF application, not a web application. WebView2 is used only for rendering static local report HTML. |
| **Command Injection** | The application does not shell out to external processes based on user input. Device communication uses structured API calls via GDK tooling, not command-line invocation. |

### 4.2 Memory Safety (CWE-787, CWE-125, CWE-416)

| Weakness | Mitigation |
|---|---|
| **Out-of-Bounds Write/Read** | The application is written in C# (.NET managed runtime), which provides automatic bounds checking on array and buffer access. `AllowUnsafeBlocks` is enabled for P/Invoke interop with Windows ETW APIs only; unsafe code is limited to `TraceEventNativeMethods` for kernel trace consumption. |
| **Use-After-Free** | .NET garbage collection eliminates use-after-free vulnerabilities in managed code. Native interop uses `SafeHandle` patterns where applicable. |

### 4.3 Authentication and Access Control (CWE-862, CWE-287, CWE-306)

| Weakness | Mitigation |
|---|---|
| **Missing Authentication** | XMAT is a single-user desktop tool — there is no multi-user authentication model. Access control is delegated to the Windows operating system (file permissions, user session isolation). |
| **Missing Authorization** | All operations are performed by the interactive desktop user. No privilege escalation paths exist within the application. |

### 4.4 Cryptographic Issues (CWE-327, CWE-295)

| Weakness | Mitigation |
|---|---|
| **Weak Cryptography** | Root CA certificates use RSA-4096 with SHA-256. Per-host certificates use RSA-2048 with SHA-256. No deprecated algorithms (MD5, SHA-1 for signing, DES, RC4) are used for security purposes. SHA-1 is used only for the WebSocket handshake `Sec-WebSocket-Accept` computation as required by RFC 6455. |
| **Certificate Validation** | Upstream (backend) certificate validation is intentionally disabled for the MITM proxy — this is a core functional requirement of a traffic interception tool. The proxy's purpose is to intercept TLS traffic for analysis. This bypass is scoped exclusively to the proxy's outbound connections and does not affect any other part of the application. The CA5359 analyzer rule is configured to detect any additional cert validation bypass. |

### 4.5 Deserialization and Data Handling (CWE-502, CWE-611)

| Weakness | Mitigation |
|---|---|
| **Insecure Deserialization** | The application uses `System.Text.Json` (not `BinaryFormatter` or `Newtonsoft.Json` with `TypeNameHandling`). `System.Text.Json` does not support polymorphic deserialization by default, eliminating type-confusion attacks. |
| **XML External Entity (XXE)** | XML parsing uses `XDocument` (LINQ to XML), which does **not** process DTDs by default in .NET Core/.NET 5+. The `XDocument.Parse()` and `XDocument.Load()` methods in modern .NET disable DTD processing unless explicitly enabled via `XmlReaderSettings.DtdProcessing`. |
| **ZIP Handling** | `.saz` file entries are read into memory streams for processing — they are not extracted to the filesystem. This eliminates path traversal via malicious ZIP entry names (Zip Slip). Decompression is bounded by available process memory and .NET's `OutOfMemoryException` handling. |

### 4.6 Information Exposure (CWE-200, CWE-532)

| Weakness | Mitigation |
|---|---|
| **Sensitive Data in Logs** | The application logs operational events (connection status, analysis progress) but does not log captured request/response bodies or authentication tokens. Log files are stored in the user's AppData directory with user-level file permissions. |
| **Sensitive Data at Rest** | Captured traffic is stored in local SQLite databases within the user's AppData or user-specified directory. No data is transmitted externally. Cleanup of capture data is the user's responsibility. |

### 4.7 Supply Chain Security (CWE-1395)

| Control | Implementation |
|---|---|
| **Dependency Pinning** | GitHub Actions workflows pin all third-party actions to full commit SHAs with version comments (e.g., `actions/checkout@11bd71901bbe...  # v4.2.2`). |
| **Dependency Review** | The `dependency-review.yml` workflow runs on every pull request to detect newly introduced vulnerable dependencies. |
| **OSSF Scorecard** | The `scorecards.yml` workflow runs weekly to assess supply-chain security posture and publishes results to GitHub code scanning. |
| **CodeQL Analysis** | The `codeql.yml` workflow performs semantic code analysis for C# on every push to `main`, every PR to `main`, and on a weekly schedule. |
| **Hardened CI Runners** | All CI workflows use `step-security/harden-runner` with egress auditing to detect unexpected network activity during builds. |
| **SPDX License Compliance** | All source files include `SPDX-License-Identifier: MIT` headers for license traceability. |

---

## 5. Known Limitations and Accepted Risks

The following are known limitations that are accepted given the application's
threat model as a local developer diagnostic tool:

| Item | Risk | Justification |
|---|---|---|
| **C# scripting without sandbox** | Scripts execute with full application privileges | Scripts require explicit user action to load and run. The user is a developer who authors or reviews the script. This mirrors the trust model of IDEs and developer tools. Future enhancement: consider Roslyn analyzer restrictions on script compilation. |
| **MITM certificate validation bypass** | Upstream TLS certificates are not validated by the proxy | This is the core purpose of the tool — intercepting HTTPS traffic for analysis. The bypass is scoped to proxy connections only and is only active when the user explicitly starts a capture session. |
| **Unsafe code for ETW** | `AllowUnsafeBlocks` enabled for P/Invoke | Required for Windows ETW trace consumption. Unsafe code is isolated to `TraceEventNativeMethods` and does not process user-controlled input directly. |
| **Local data not encrypted at rest** | Captured traffic stored in plaintext SQLite | Standard for developer diagnostic tools. Encryption at rest is delegated to Windows (BitLocker, EFS) at the OS level. |

---

## 6. Security Contacts and Vulnerability Reporting

Security vulnerabilities should be reported through the Microsoft Security
Response Center (MSRC) at
[https://msrc.microsoft.com/create-report](https://msrc.microsoft.com/create-report).

See [SECURITY.md](SECURITY.md) for the full vulnerability disclosure policy.

---

*This document was last reviewed on April 2026 and should be updated when
significant architectural changes are made.*
