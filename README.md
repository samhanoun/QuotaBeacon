# SessionWatcher for Windows

SessionWatcher is a native Windows usage monitor for AI coding tools. Its first
providers are Claude Code and OpenAI Codex, with a plugin contract for adding
more providers without changing the app shell.

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

- a responsive WinUI 3 dashboard for Claude Code and OpenAI Codex;
- provider-reported windows, reset countdowns, remaining quota, and pace;
- privacy-safe daily and 30-day tokens, sessions, cache rate, model share,
  activity charts, and API-equivalent cost estimates from local Codex metadata;
- an explicit Codex local-session fallback when app-server versions drift;
- a dedicated Trends view, sanitized 90-day quota history, threshold alerts,
  and a Windows tray icon;
- configurable refresh/startup/minimize-to-tray behavior;
- trusted external provider DLL discovery with duplicate/error isolation.

On this development machine, Codex currently uses the clearly labeled local
fallback because the installed CLI cannot parse a newly returned plan name.
Claude reports an actionable sign-in/expiry state until `claude /login`
succeeds. Neither condition prevents the other provider from updating.

## Build and run

Requirements: Windows 10 version 1809 or newer, Developer Mode, the .NET 10
SDK, and the Windows application-development workload from Visual Studio.

```powershell
dotnet restore SessionWatcher.slnx
dotnet build SessionWatcher.slnx -c Release
dotnet test SessionWatcher.Core.Tests -c Release
dotnet run --project SessionWatcher -c Debug
```

The app is packaged for its development launch, so `dotnet run` registers a
debug identity before opening it. Application data is under the current
package's `LocalState\SessionWatcher` folder. Use the Plugins page to open the
exact folder.

To create a self-contained x64 file-system publish:

```powershell
dotnet publish SessionWatcher\SessionWatcher.csproj -c Release -p:Platform=x64
```

Run `scripts\smoke-test.ps1` with no SessionWatcher instance open to verify a
real packaged launch, its accessibility surface, and sanitized persistence.

## Repository layout

- `SessionWatcher` — WinUI 3 shell, tray integration, and composition root.
- `SessionWatcher.Core` — provider contracts, Claude/Codex adapters, history,
  alerts, plugins, and presentation projection.
- `SessionWatcher.Core.Tests` — isolated contract and safety tests.
- `docs` — research, architecture decisions, and plugin authoring.
