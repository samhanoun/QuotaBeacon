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
scope, alternatives, and implementation contract.

## Status

Initial native Windows implementation is in progress.
