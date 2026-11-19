# XMAT C# codegen handoff

These draft C# contracts come from `xtech.data.events.schemaregistry` commit `5a513bf78c8241934c55227103c3e4a542cea8dd`, not yet merged to `main`. The registry project is `xmat`; `codegen.yaml` records the commit and pins all 11 event versions. Regenerate after the schema changes are merged.

The generated namespace and event names use `Microsoft.Gaming.Tooling.XMAT`. Each event has no event-specific fields and uses the project's common fields. `CommonFields.cs` defines the wire properties; `Events/` contains one generated event class per event; `Types/` contains the fixed enum types; the output also includes the JSON runtime support and `telemetry.manifest.json`.

## Reproduce

Use the repository-pinned Node.js `v24.22.1`, run from the registry checkout root:

```powershell
npm.cmd run codegen -- --config "<handoff-directory>\codegen.yaml"
```

Run the command from the registry checkout root with Node.js `v24.22.1` on `PATH`, substituting this directory's absolute path for `<handoff-directory>`. The config writes to its sibling `generated/` directory. The contracts are draft and do not imply privacy or release approval.
