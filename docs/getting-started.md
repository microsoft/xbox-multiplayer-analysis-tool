# Getting started

[Back to README](../README.md)

Use this guide to run XMAT and capture your first web requests from an Xbox
development kit. For packet-level traffic, follow [Network capture](network-capture.md).

## Prerequisites

- A 64-bit Windows 10 PC (build 17763 or higher) or Windows 11 PC.
- For the console walkthrough, an Xbox One or Xbox Series development kit running
  recovery 2106 or higher, reachable from your PC, with the appropriate Xbox SDK
  tooling installed.
- For generic devices, support for installing a `.cer` certificate and setting a
  proxy IP address and port. Use the capture's **Proxy Setup Details** for instructions.

## Obtain and run XMAT

### Use a CI build

1. Open the [XMAT Win64 Desktop Build workflow](https://github.com/microsoft/xbox-multiplayer-analysis-tool/actions/workflows/dotnet-desktop.yml)
   and select a successful run for the source revision you want.
2. Download its `XMAT_CI_Build_Release` artifact and extract the complete archive.
   Downloading workflow artifacts requires signing in to GitHub.
3. This build is not self-contained: install the .NET 10 Desktop Runtime and
   ASP.NET Core Runtime for Windows x64 if they are not already installed.
4. Run `XboxMultiplayerAnalysisTool.exe` from the extracted directory.

### Build from source

Install the **.NET 10 SDK** on Windows. From the repository root, run:

```powershell
dotnet restore .\XboxMultiplayerAnalysisTool.sln
dotnet build .\XboxMultiplayerAnalysisTool.sln -c Release --no-restore
dotnet run --project .\XMAT\XboxMultiplayerAnalysisTool.csproj -c Release --no-build
```

The solution targets x64. You can also open `XboxMultiplayerAnalysisTool.sln` in a
Visual Studio installation that supports .NET 10 and WPF, select **Release / x64**,
and build the application.

For development and contribution requirements, see [Contributing](../CONTRIBUTING.md).

## Capture your first console requests

1. Start XMAT and click the **+** tab.
2. Choose your console and the web proxy capture method, then click **Add capture**.
   **Default Console** uses the designated default console without entering its IP address.
3. Check that the console is ready to reboot, then click **Enable Xbox Proxy**.
   Enabling proxying starts the local listener and reboots the kit.
4. After the console restarts, launch an app that uses Xbox services.
5. Select a request in the connections list and inspect its **Headers** and **Body**.

**Expected result:** requests appear in the connections list, with response status
codes and timing details as responses arrive.

**When finished:** use **Disable Xbox Proxy** to stop the console routing traffic
through XMAT. This reboots the console. **Stop Capture** only stops the local
listener; it does not disable the console's proxy settings. Leaving those settings
enabled without a running listener can prevent Xbox services from working.

For port changes, filtering, and device-specific behavior, see [Web proxy](web-proxy.md).
If requests do not appear, use [Troubleshooting](troubleshooting.md).

## Appearance

Toggle **Edit > Dark Mode** to switch between light and dark themes. XMAT remembers
the choice the next time it starts.
