# Architecture

OpenRevit Tools has three runtime layers:

1. **Revit add-in (`JarviTools.dll`)** — ribbon commands, model reads/writes and a loopback HTTP MCP endpoint.
2. **stdio MCP bridge (`bridge/RevitMcpBridge`)** — translates MCP JSON-RPC `tools/list` and `tools/call` to
   the authenticated Revit loopback endpoint.
3. **AI/MCP client** — plans model-specific work, calls narrowly described tools, checks evidence and reports
   assumptions. The AI is not embedded in the DLL.

## Revit threading

HTTP requests are accepted off the Revit UI thread and queued through one `ExternalEvent`. Tools execute on the
Revit main thread. A queued request records the active document/view identity; if the user switches context before
execution, it is cancelled instead of writing to a similarly numbered element in another model.

The loopback endpoint is intentionally bounded: request bodies are capped at 1 MiB, at most 8 HTTP requests are
handled concurrently, and no more than 64 Revit requests can wait in the `ExternalEvent` queue. Each handler pass
starts at most 4 requests or spends 100 ms between request boundaries before yielding and scheduling another pass.
A single Revit API tool call remains non-preemptible once started; stopping MCP therefore cancels queued work while
allowing an already-running Revit transaction to finish safely. These limits prevent an authenticated local client
from exhausting memory or indefinitely draining the Revit UI thread.

## Analysis modules

- **Clearance (positive space):** element lowest-point screening and dedicated result views.
- **Plenum (negative space):** adaptive XY cells, vertical probe profiles, host/link solid checks and conservative
  Unknown/Mixed handling.
- **Maintenance reachability:** pure grid/pathfinding primitives plus Revit geometry validation. The ribbon item is
  intentionally an AI collaboration entry: the final proposal depends on project semantics, negative-space evidence,
  maintenance-side inference and expert confirmation. Candidate auditing keeps the production selection path separate
  from the report-only representative set; see `docs/maintenance-candidate-audit.md`.
- **Maintenance HandReach:** independent 400×400 ceiling-hatch reach-through analysis (service-face proxy, nearest-edge
  distance, circular corridor solids, A-frame ladder, 40 mm grid region merging). Data-only by default; views are
  generated on demand after approval. Kept in `MaintenanceHandReach*` files so the proven wall-door pipeline is
  untouched; see `docs/maintenance-hand-reach.md`.

## Ownership and deletion

Generated assets must carry machine-readable ownership (`ApplicationId`, Extensible Storage or fixed shared-parameter
GUIDs). Names are for people, not deletion authority. User annotations, sheet placement and unrelated groups are
preserved.

## Maintenance ledger

Revit instance parameters are the source of truth. `sync_maintenance_ledger_bridge` exports a user-facing CSV, a
Codex evidence CSV and a hash manifest. The formatted XLSX is a reviewed presentation artifact, not the authoritative
database. See `docs/maintenance-ledger-sync.md`.
