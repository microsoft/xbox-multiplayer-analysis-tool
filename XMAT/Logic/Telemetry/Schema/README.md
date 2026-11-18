# XMAT C# codegen handoff

These generated C# contracts come from `xtech.data.events.schemaregistry` commit `eb30ed93a844ab8c4e0c214a86472f364380425d`, based on `origin/main` commit `a29c5d4e7af10042e48755fdc36dc389aecf6c15`. The registry project is `xmat`; all 11 event versions are pinned in `codegen.yaml`.

The generated namespace and event names use `Microsoft.Gaming.Tooling.XMAT`. Each event has no event-specific fields and uses the project's common fields. `CommonFields.cs` defines the wire properties; `Events/` contains one generated event class per event; `Types/` contains the fixed enum types; the output also includes the JSON runtime support and `telemetry.manifest.json`.

## Reproduce

Use the repository-pinned Node.js `v24.22.1`, run from the registry checkout root:

```powershell
npm.cmd run codegen -- --config "<handoff-directory>\codegen.yaml"
```

Run the command from the registry checkout root with Node.js `v24.22.1` on `PATH`, substituting this directory's absolute path for `<handoff-directory>`. The config writes to its sibling `generated/` directory. The contracts are draft and do not imply privacy or release approval.
