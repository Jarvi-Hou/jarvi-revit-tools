# Security policy

## Supported version

Security fixes are applied to the latest `main` branch and the latest published release.

## MCP trust boundary

OpenRevit MCP controls the active Revit session and can modify the open model. Treat an MCP client as a
trusted operator:

- The HTTP listener binds only to `127.0.0.1`.
- Every server start creates a random bearer token encrypted for the current Windows user.
- The bundled stdio bridge reads that token automatically. Direct unauthenticated HTTP calls are rejected.
- `execute_csharp` is disabled by default. It is registered only when Revit is started with
  `OPENREVIT_ENABLE_FULL_TRUST_CSHARP=1`.
- Full-trust C# can access Revit, files, processes and the network. A Revit transaction cannot roll back those
  external side effects. Enable it only for a supervised, trusted AI/developer session.
- `run_command` is also disabled by default because it leaves Revit waiting for human clicks. Enable it only
  in a supervised foreground session with `OPENREVIT_ENABLE_INTERACTIVE_COMMANDS=1`.
- Before running an AI-generated write, save or synchronize the model and review the requested scope.

Model and link paths are hidden by default. `get_model_info`, `analyze_plenum_space` and
`get_plenum_analysis_summary` expose the full model path only when `includePath=true`; `get_link_status`
uses `includePaths=true`. Otherwise the path is null and the response reports `pathIncluded=false` (or
`pathsIncluded=false`). Explicitly requested paths may contain customer or workstation information and are
sent to the connected MCP client. Use only a trusted client and redact paths before sharing logs or transcripts.

OpenRevit writes local diagnostic logs for troubleshooting and does not upload them. The logger removes files
older than 14 days when a new logging session starts; operators with stricter project rules should remove them
sooner.

The loopback/token boundary protects against casual local access; it is not a sandbox. A malicious process
already running as the same Windows user may still be able to act with that user's authority.

## Reporting a vulnerability

Do not publish an exploit or project data in a public issue. Use GitHub's private security advisory workflow:
**Security → Advisories → New draft security advisory**. Include affected version, reproduction steps and
impact. Remove model names, file paths, credentials and customer data from attachments.
