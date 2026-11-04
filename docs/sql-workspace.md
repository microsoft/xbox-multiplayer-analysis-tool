# SQL workspace

[Back to README](../README.md)

Choose **Analysis > New SQL Query** to open a document in **SQL Queries**; the **Captures** workspace retains its device tabs, analysis views and splitter state. SQL always targets the explicitly selected active/current-loaded database, not the capture grid's device or visual filters. Stop all captures before running or refreshing schema; editing remains available during capture.

The local AvalonEdit editor provides highlighting, line numbers, schema search, table/column insertion, generated SELECTs, examples, basic alias-aware completion, and session-only history. **F5 / Ctrl+Enter** runs the selection when present, otherwise the document; **Ctrl+Space** opens completion. Open/save `.sql` files explicitly—opened files are never executed automatically, and history is not written to application settings. Unsaved documents use Save/Discard/Cancel on close. AvalonEdit 6.3.1.120 is MIT-licensed; its notice is included with the application.

Queries use the same read-only DAL snapshots as agents: exactly one result-producing read-only statement, no database writes/ATTACH/PRAGMA/extension escape hatch. Defaults are 60 seconds and a 512 MiB result store, with explicit document options. Capture can resume after snapshot preparation without changing that run. Cancellation drains private work; errors/cancellation/limits retain the preceding successful result under its original SQL/source identity.

Results use a virtualized ordinal grid with **1,000-row pages**, exact totals, duplicate/empty labels, typed NULL/INTEGER/REAL/TEXT/BLOB previews, and a bounded cell inspector. Paging is not a hidden SQL LIMIT. Selected row identities survive page changes; export **all rows** or **selected rows** as exact CSV/JSON from the completed immutable result, never by rerunning SQL. CSV quoting does not neutralize spreadsheet formulas, and unsanitized output may contain credentials/payloads. Human export uses its own file picker and overwrite/privacy consent.

Temporary snapshots/results contain unencrypted capture data and add disk overhead beyond the result-store quota; successful replacement/document close/application shutdown releases only owned stores after work drains. The editor does not infer a source record for arbitrary joins or aggregates. SQLite errors are shown as supplied by the existing query contract; an exact editor error location is unavailable when the provider supplies no offset.
