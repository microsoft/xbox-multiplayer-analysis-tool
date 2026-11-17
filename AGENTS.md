# Agent guidance

Read [CONTRIBUTING.md](CONTRIBUTING.md) for repository-wide architecture,
headless-parity, compatibility, and testing requirements.

For network-shaping work, read
[the maintainer guide](docs/network-shaping-design.md) before changing code and
[the user guide](docs/web-proxy.md#network-shaping) before changing behavior or
claims. Keep both audiences' documentation in sync.

Preserve these invariants:

- Shaping is an explicit-proxy feature. It shapes the client-to-XMAT leg, not
  UDP, the XMAT-to-origin leg, or a device's network link.
- `NetworkShapingController` is the live source of policy and change history.
  GUI and CLI updates must use the shared `SetNetworkShapingOperation`.
- Keep shared directional budgets, bounded buffering, cancellation cleanup, and
  live profile changes for open connections.
- Recorded metadata is anonymous and descriptive only. Reading or importing a
  capture must never apply a recorded profile.
- A new GUI entry point needs a `CapabilityRegistry` classification and the
  matching sorted `docs/cli.md` capability row.

Use the focused checks in `docs/network-shaping-design.md`. Keep the default
in-repository test output path; the headless-parity tests locate the solution
from the test output directory.
