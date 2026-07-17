# Quota Beacon for Windows

Quota Beacon is a native Windows usage monitor for AI coding tools. Its built-in
providers are Claude Code, OpenAI Codex, Gemini CLI, and Google Antigravity,
with a plugin contract for adding more providers without changing the app shell.

The app keeps two kinds of numbers deliberately separate:

- **Quota windows** are the provider-reported percentage used and reset time.
- **Local activity** is a private, on-device token and cost estimate derived
  from local CLI metadata. It is never presented as a provider quota.

## Product goals

- Show short-window and weekly usage before a coding session is interrupted.
- Work from the Windows tray and a native Fluent dashboard.
- Auto-detect existing Claude Code and Codex sign-ins.
- Keep credentials local, read-only, short-lived in memory, and isolated in
  provider plugins.
- Store only sanitized usage snapshots; never prompts, responses, or source
  code.
- Remain useful during provider or CLI version skew through explicit fallback
  sources and stale-data labels.

See [docs/PRODUCT_BRIEF.md](docs/PRODUCT_BRIEF.md) for the researched feature
scope, alternatives, and provider contract. See [docs/PLUGINS.md](docs/PLUGINS.md)
for the external provider API and its trust boundary, and
[docs/ANALYTICS.md](docs/ANALYTICS.md) for local metric definitions.

## Status

The first working native prototype includes:

- a responsive WinUI 3 dashboard for Claude Code, OpenAI Codex, Gemini CLI,
  and Google Antigravity;
- provider-reported windows, reset countdowns, remaining quota, and pace;
- privacy-safe daily and 30-day tokens, sessions, cache rate, model share,
  activity charts, and API-equivalent cost estimates from local Codex metadata;
- an explicit Codex local-session fallback when app-server versions drift;
- a dedicated Trends view, sanitized 90-day quota history, threshold alerts,
  and a Windows tray icon;
- provider controls on the dashboard and in Settings, plus configurable
  refresh/startup/minimize-to-tray behavior;
- trusted external provider DLL discovery with duplicate/error isolation.

The July 2026 Quota Beacon change-set security review closed all 86 worklist
rows with no deferred work and no reportable vulnerabilities. Three validated
defense-in-depth issues were still remediated: provider-ID domain mismatches,
relative-reset arithmetic overflow, and Unicode format controls in quota
labels. See [SECURITY_AUDIT.md](SECURITY_AUDIT.md) and [SECURITY.md](SECURITY.md).

The final release review also replaced redirected Google CLI input with a real
contained Windows terminal. Gemini receives only `/model`, Antigravity receives
only `/usage`, terminal output stays bounded and continuously drained, and
shutdown never sends prompt text that could be interpreted as an AI request.

On this development machine, Codex currently uses the clearly labeled local
fallback because the installed CLI cannot parse a newly returned plan name.
Claude reports an actionable sign-in/expiry state until `claude /login`
succeeds. Neither condition prevents the other provider from updating.

## Build and run

Requirements: Windows 10 version 1809 or newer, Developer Mode, the .NET 10
SDK, and the Windows application-development workload from Visual Studio.

```powershell
dotnet restore QuotaBeacon.slnx
dotnet build QuotaBeacon.slnx -c Release
dotnet test QuotaBeacon.Core.Tests -c Release
dotnet run --project QuotaBeacon -c Debug
```

The app is packaged for its development launch, so `dotnet run` registers a
debug identity before opening it. New installs use the current package's
`LocalState\QuotaBeacon` folder. Existing installations continue using
`LocalState\SessionWatcher` automatically so settings and history are
preserved. Use the Plugins page to open the exact folder. Pre-rename plugin
binaries must be rebuilt against `QuotaBeacon.Core`; the loader rejects them
with an upgrade message instead of attempting an ABI-incompatible load.

To create a self-contained x64 file-system publish:

```powershell
dotnet publish QuotaBeacon\QuotaBeacon.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64
```

Run `scripts\smoke-test.ps1` with no Quota Beacon instance open to verify a
real packaged launch, its accessibility surface, and sanitized persistence.

## Security and development workflow

Pull requests run Windows release builds, a self-contained x64 publish, the
complete test suite with an 80% line-coverage gate, formatting checks, NuGet
vulnerability audits, CodeQL, dependency review, and secret scanning. GitHub
Actions and reusable supply-chain steps are pinned to immutable commit SHAs and
updated by Dependabot. The interactive packaged-app smoke test remains a local
release check because hosted CI does not provide Quota Beacon with a supported
interactive desktop session.

## Repository layout

- `QuotaBeacon` — Quota Beacon's WinUI 3 shell, tray integration, and composition root.
- `QuotaBeacon.Core` — provider contracts, Claude/Codex adapters, history,
  alerts, plugins, and presentation projection.
- `QuotaBeacon.Core.Tests` — isolated contract and safety tests.
- `docs` — research, architecture decisions, and plugin authoring.
