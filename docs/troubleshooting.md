# Troubleshooting

[Back to README](../README.md) | [Support policy](../SUPPORT.md)

Use these recovery steps for proxy and capture problems. For setup instructions,
see [Web proxy](web-proxy.md) or [Network capture](network-capture.md).

## Xbox proxy recovery

### Endpoint cannot be verified

Endpoint verification depends on the console recovery and installed SDK. If the
console cannot report a complete endpoint, XMAT reports it as unverified rather
than claiming success.

1. Use **Check Proxy Status** to inspect the console's reported endpoint.
2. Explicitly disable proxying and wait for the console to return.
3. Check status before re-enabling or retrying a pending change.

For port changes, stop capture and use **Actions > Change Proxy Port**, rather than
trying to change an active tab's port indirectly through Preferences.

**A console reboot or SDK write already requested may not be interruptible.**
Canceling a port apply prevents later steps but waits for an in-flight mutation to
settle. Check status before retrying; a saved new port may remain assigned even if
activation could not be confirmed.

### Reported port has an invalid suffix

The Xbox proxy-address file must be NUL-terminated UTF-8. An EOF-only or CRLF-only
file can leave garbage after the port in the reported setting. If an older XMAT
version configured it, explicitly disable and re-enable Xbox proxying with the
current version to rewrite the file. Invalid suffixes are reported as unverified,
not silently stripped.

### Console loses access to Xbox services

Stopping capture does not disable Xbox proxy settings. If they remain enabled while
the local listener is stopped, console service checks and Xbox services calls can fail.
Restart capture or use **Disable Xbox Proxy** to restore normal routing; disabling
reboots the console. See [Stop proxying safely](web-proxy.md#stop-proxying-safely).

## If XMAT closes unexpectedly

### Local PC proxy restoration

Capturing **Local PC** points the Windows proxy setting at XMAT. If XMAT ends without
restoring it, apps using the Windows proxy can lose internet access. XMAT records the
previous settings and attempts to restore them:

- Before changing Windows proxy settings, XMAT records them and starts a helper.
  If the helper cannot start, the proxy is not changed and capture does not start.
  If XMAT crashes, is ended in Task Manager, or is stopped from a debugger, the helper
  tries to restore those settings, usually a direct connection.
- Stopping capture, exiting normally, or an error closing XMAT also attempts restoration.
- Restoration occurs only while Windows still has XMAT's applied settings. If it
  cannot complete, the record is retained and restoration is retried later, including
  when capture stops and at the next start.
- If the helper stops unexpectedly during capture, XMAT tries to restore settings
  immediately and reports the result, unless a newer capture has started. Stop and
  start capture to resume.
- At startup, if the Windows proxy points at this PC (for example `127.0.0.1:8880`)
  with no listener, XMAT asks before disabling it when there is no record of setting
  it (possible with an older version) or settings changed during capture.
- If you change Windows proxy settings during capture, XMAT leaves your changes
  alone. Stopping turns off only XMAT's own proxy; if Windows now uses a different
  proxy, XMAT leaves it enabled and tells you. A later capture records the settings
  in effect then instead of restoring older ones.

### Restoration limits

- A paused debugger or unresponsive XMAT process has not exited, so the proxy stays on.
- After a PC restart or ending XMAT and its helper together (for example the entire
  process tree), restoration is attempted the next time XMAT starts.
- If XMAT cannot verify that the instance which changed settings has ended, such as
  one running as administrator, it leaves settings alone and does not capture this PC.
- A partly failed proxy change can leave settings indistinguishable from yours and
  require manual correction. A saved proxy address that is not in use may not be restored.
- Local PC capture is refused inside a process group that could end the helper
  together with XMAT, as some terminals and tools do. Start XMAT directly instead.

### Xbox consoles

Consoles are never reset automatically. At the next start after an unexpected exit
while capturing a console, XMAT offers to disable that console's proxy, which restarts
it. The offer is repeated at later starts until answered.

## Network capture fails

Reboot the console used for the failed trace before retrying. The trace may still
be running and the capture service can keep failing if not restarted.

## Known limitations

| Limitation | Workaround or details |
|---|---|
| A header spanning multiple lines does not proxy correctly | No current workaround; apps such as Outlook may fail while proxying the PC |
| WebSocket capture has retention and persistence limits | Only recent messages are retained, and messages are not saved to or loaded from `.XMATCAP`; see [WebSockets](web-proxy.md#view-websocket-messages) |
| Script editor has no highlighting or completion, and not all .NET objects are available | See [Scripting](scripting.md) for supported workflow and execution limits |
| `pktmon` traces cannot be imported | Use `netsh trace`; see [Capture files](capture-files.md#import-network-traces-from-another-pc) |

## Collect debug logs

1. Choose **Help > Collect Debug Logs** to create a ZIP of application logs on your Desktop.
2. **Review the logs before sharing:** they may contain web request and response data.
3. Send the reviewed logs to xmat_oss@microsoft.com with a description of the problem.

An unknown application error also places logs on the Desktop automatically. For
bugs and feature requests, search existing GitHub issues before filing a new one.
See [Support](../SUPPORT.md) for the support policy.

## Potential improvements, not commitments

These are areas identified for improvement, separate from the recovery steps above:

- Improve keep-alive handling.
- Save and load WebSocket messages in `.XMATCAP` files.
- Allow user-selected storage for root certificates and working analysis output.
  Analysis reports can already be exported to a selected folder; working copies stay in AppData.
- Add localization.

See the [Roadmap](../ROADMAP.md) for project plans; this list does not promise new features.
