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

### Use a release (recommended)

1. Open the [latest release page](https://github.com/microsoft/xbox-multiplayer-analysis-tool/releases/latest).
2. Download the XMAT ZIP under **Assets** (not **Source code (zip)**) and extract
   the complete archive.
3. Run `XboxMultiplayerAnalysisTool.exe` from the extracted directory.

### Use a CI build

Use CI builds when you need changes that are not yet in a release.

1. Open the [XMAT Win64 Desktop Build workflow filtered to push runs on main](https://github.com/microsoft/xbox-multiplayer-analysis-tool/actions/workflows/dotnet-desktop.yml?query=branch%3Amain+event%3Apush)
   and select a successful **push** run on **main**, not a pull request build.
2. Download its `XMAT_CI_Build_Release` artifact and extract the complete archive.
   Downloading workflow artifacts requires signing in to GitHub, and artifacts
   expire.
3. This build is not self-contained: install the .NET 10 Desktop Runtime and
   ASP.NET Core Runtime for Windows x64 if they are not already installed.
4. Run `XboxMultiplayerAnalysisTool.exe` from the extracted directory.

### Build from source

Install the **.NET 10 SDK** on Windows. From the repository root, run:

```powershell
dotnet restore .\XboxMultiplayerAnalysisTool.sln
dotnet build .\XboxMultiplayerAnalysisTool.sln -c Release --no-restore
.\XMAT\bin\x64\Release\net10.0-windows10.0.17763.0\win-x64\XboxMultiplayerAnalysisTool.exe
```

Launch the built executable directly rather than using `dotnet run`, which places
XMAT in a kill-on-close job that prevents Local PC capture.

The solution targets x64. You can also open `XboxMultiplayerAnalysisTool.sln` in a
Visual Studio installation that supports .NET 10 and WPF, select **Release / x64**,
and build the application.

For development and contribution requirements, see [Contributing](../CONTRIBUTING.md).

#### Iterating on the desktop UI

Open the solution in Visual Studio with .NET 10 and WPF support, select **Debug / x64**,
and set **XboxMultiplayerAnalysisTool** as the startup project. Start debugging and
use **XAML Hot Reload** for supported visual changes. If a change cannot be hot
reloaded, stop debugging, rebuild, and relaunch.

Without Visual Studio, build with
`dotnet build .\XMAT\XboxMultiplayerAnalysisTool.csproj -c Debug -p:Platform=x64`,
then launch
`.\XMAT\bin\x64\Debug\net10.0-windows10.0.17763.0\win-x64\XboxMultiplayerAnalysisTool.exe`.
Close that development instance before rebuilding. Use a saved capture to review
tables and inspectors without generating live traffic; see [Capture files](capture-files.md).

Shared corner radii, spacing and type sizes live in `XMAT\Looks\NewLook.xaml`; theme
colors live in `XMAT\Themes`. Use the heading, section, and text-area tokens for
their respective spacing rather than adding equal padding to every container.
Selected tabs join their content surface without a divider; inspector text areas
use inset surfaces rather than nested panel frames.
Narrow traffic panes scroll horizontally instead of shrinking status, method and
scheme values until they become unreadable.

Capture tabs identify documents; only a small gap separates them. Inspector tabs
use the same text size as the capture toolbar's buttons and the Headers/Body/JSON
format tabs above the recessed text area.
A selected tab's label keeps its size and turns semibold. Each tab is wide enough
for its semibold label, so switching tabs never moves a tab or a label.
Inspector, format and analysis tabs stay in one row: when a pane is too narrow,
tabs that don't fit move to **More**
at the end of the row. The selected tab always stays in the row, and the Left and
Right arrow keys move through every tab.
Capture tabs stay in one row too, with **More** for those that don't fit, and you
can drag a capture tab to another place in the row.
In a narrow inspector, request details move below the **Request** or **Response**
title, the script type reference moves below the editor, and the block and bypass
list buttons move below their entry fields. WebSocket messages sit above their
payload. A tab that is too short for its content scrolls as a page, and its list
or editor keeps a usable height. The **Script Editor** keeps its buttons at the
bottom of the tab, so only the content above them scrolls, and dividers resize its
editor, type reference and description.
The empty analysis panel explains how to capture traffic first, or offers
**Analyze Capture** for the selected capture once it contains data. Stop all
captures before analyzing.

## Capture your first console requests

1. Start XMAT and click the **+** tab.
2. Choose your console and the web proxy capture method, then click **Add capture**.
   **Default Xbox console** uses the designated default console without entering its IP address.
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

Choose **Edit > Appearance > Light** or **Dark**. Themes change presentation, not
capture behavior. XMAT remembers the choice the next time it starts.

**Edit > Appearance > Comfort view**, on by default, adds space around tables,
panels and controls. Turn it off for a denser view that fits more rows on screen;
text sizes and colors stay the same. XMAT remembers this setting too.

When a Windows contrast theme (high contrast) is on, XMAT uses its colors in both
looks, and it follows the setting while it runs. The **Edit** menu notes that
Windows is setting the colors. Your Light or Dark choice is kept and applies again
when the contrast theme is turned off.

Text follows the Windows **Text size** setting (**Settings > Accessibility > Text
size**), including changes made while XMAT runs.

To compare the redesign with the original XMAT appearance, turn off **New look**
at the right of the menu row. XMAT remembers this setting too. The original
appearance offers **Edit > Dark Mode** for light and dark themes and keeps its own
spacing, since Comfort view applies to the new look.

The sparkle button beside **New look** opens **What's new**, which shows the
redesign's changes a page at a time, with a picture of each and where to find it.
Use **Back** and **Next**, the arrow keys or the list of topics to move between
pages.

## Keyboard and screen readers

- **Ctrl+F4** or **Ctrl+W** closes the selected capture or analysis tab.
- **Ctrl+Shift+Page Up** and **Ctrl+Shift+Page Down** move the selected capture tab
  one place left or right, and screen readers announce its new position.
- A table is one Tab stop. The arrow keys move between its rows and cells, and
  **Tab** moves on to the next control, stopping first at any buttons in the
  current row. To sort from the keyboard, open the table's menu with **Shift+F10**
  or the Menu key and choose **Sort by**.
- In a filter list, **Down** opens the list and the arrow keys move between its
  check boxes. **Space** checks or clears one; **Enter** closes the list.
- **Esc** closes the process filter and returns focus to **Process**.
- When the traffic pane is too narrow for its filters, they fold into a **Filters**
  button. **Enter**, **Space** or **Alt+Down** opens it on the first filter, **Tab**
  moves through the filters, and **Esc** closes it and returns focus to the button.
  Screen readers hear how many filters are on, such as **Filters (2 active)**.
- Pane dividers are Tab stops; the arrow keys move a focused divider.
- In the script editor, **Tab** indents and **Shift+Tab** outdents, so
  **Ctrl+Tab** and **Ctrl+Shift+Tab** move focus out of the editor. In the script
  output, **Enter** goes to the selected error.
- Screen readers announce status bar updates and entry errors as they appear, and
  read abbreviated column headers, such as **#**, by their full names.
