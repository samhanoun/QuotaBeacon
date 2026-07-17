# Product brief

## User journeys

1. As a Claude Code and Codex user, I want to see every active rate-limit
   window and its reset countdown, so I can decide which tool to use next.
2. As a heavy user, I want usage compared with elapsed time in the current
   window, so I am warned before my current pace exhausts the quota.
3. As a privacy-conscious developer, I want the app to reuse existing local
   sign-ins without copying or exporting credentials, prompts, responses, or
   code.
4. As a user with changing CLI versions, I want the app to show its source,
   freshness, and fallback state, so a stale or estimated number is never
   mistaken for live provider data.
5. As a plugin author, I want to return a provider-neutral usage snapshot, so a
   new tool can use the same dashboard, history, alerts, and tray surfaces.

## QuotaBeacon reference scope

The macOS reference product advertises:

- five-hour and weekly limits, including model-scoped limits;
- usage-rate charts and 7- to 90-day history;
- token and cost breakdowns;
- menu-bar modes, reset countdowns, and notifications;
- tool auto-detection and multiple accounts;
- one view spanning Claude, Codex, Cursor, and other coding tools.

This Windows project will first deliver the behavior that protects a coding
session: live provider windows, reset time, pace, freshness, history, alerts,
and tray visibility. Multi-account switching and broader cost catalogs follow
after the provider and persistence contracts are stable.

## Existing Windows options found during scouting

| Product | Windows | Claude | Codex | Form | Notes |
| --- | --- | --- | --- | --- | --- |
| Win-CodexBar | Yes | Yes | Yes | Tray/dashboard | Closest direct equivalent; installable through WinGet and supports many providers. |
| Wburn | Yes | Yes | Yes | Windows Widgets | Focused native widgets; also supports Gemini CLI. |
| Tokus | Yes | Yes | Yes | Cross-platform tray | Free, local-first multi-provider tracker. |
| Usage Monitor for Claude | Yes | Yes | No | Portable tray app | Mature, auditable Claude-only monitor. |
| Claude Usage Tracker | Yes | Yes | No | Dashboard/widgets | Claude quota, local sessions, history, and overlays. |
| TokenPeep | Prerelease | No | Yes | Tray/OSD | Codex-focused, Windows-first prerelease. |

The project remains useful as a native WinUI, narrowly auditable,
plugin-oriented implementation whose fallback behavior and data provenance are
first-class UI concepts.

## Provider contracts

### Codex

Preferred source: launch the installed `codex app-server` over stdio, perform
the documented initialization handshake, and call
`account/rateLimits/read`. The RPC returns provider-backed usage percentages,
window durations, and Unix reset timestamps without this app reading Codex
tokens directly.

Fallback source: read only `token_count.rate_limits` metadata from recent local
Codex session JSONL files. Do not read, retain, or expose message content. The
fallback must be labeled local and may be stale.

### Claude

Read the Claude Code OAuth access token from its existing credentials file,
honoring `CLAUDE_CONFIG_DIR`, and send it only to
`https://api.anthropic.com/api/oauth/usage`. The token is never persisted by
QuotaBeacon or logged. Parse quota objects dynamically so newly introduced
model-scoped windows are visible without a release.

Claude's official status-line and `/usage` surfaces remain the user-verifiable
reference. Authentication errors should instruct the user to run Claude's
normal login flow; QuotaBeacon does not alter credentials.

### Gemini CLI and Antigravity

Launch only the installed official CLIs in a real interactive terminal, using
Gemini's documented `/stats model` quota view and Antigravity's documented
`/usage` view. Prefer the official install locations, then discover other
Windows shims from expanded process, user, and machine PATH values. Wait for the
input prompt and a fresh exact command-menu marker before Enter; continue
watching for late trust or authentication prompts until submission. Capture
bounded terminal output with a deadline, terminate the contained process tree
after capture or timeout, strip terminal controls, and project the remaining or
used percentages into the neutral quota model. Antigravity runs in a stable
empty probe folder. If AGY requires workspace trust, the user approves that
folder once and
QuotaBeacon never bypasses the prompt. QuotaBeacon never reads or reuses either
tool's OAuth or keyring material.

If a fresh Gemini process exposes only per-process model statistics until it has
made an API call, report that limitation rather than making a billable model
request solely to unlock the quota panel.

## Neutral usage model

Each plugin returns a snapshot containing:

- provider ID and display name;
- observed time and source (`live`, `localFallback`, or `cache`);
- availability, freshness, and a safe diagnostic message;
- zero or more quota windows with stable key, label, percentage used, duration,
  and reset time;
- optional plan label and sanitized credits metadata.

The shell computes presentation-only values such as remaining percentage,
elapsed-window percentage, pace delta, and reset countdown.

## Privacy and security boundary

- No prompt, response, tool-call, file-content, or source-code collection.
- Local analytics parse only Codex model names and per-turn token counters;
  unrelated session records are skipped before JSON parsing.
- No telemetry and no QuotaBeacon service.
- Network allowlist in built-in providers: Anthropic usage endpoint only;
  Codex networking is delegated to the installed official app-server.
- OAuth values are never included in exceptions, logs, history, or UI.
- History contains sanitized snapshots only and uses the current user's local
  application-data directory.
- External plugins execute local code and must be treated as trusted software;
  the UI must make that boundary clear.

## Source links

- SessionWatcher: https://sessionwatcher.com/
- OpenAI Codex app-server protocol: https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md
- Claude Code usage errors and official usage surfaces: https://code.claude.com/docs/en/errors
- Gemini CLI quota and pricing: https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/quota-and-pricing.md
- Gemini CLI command reference: https://github.com/google-gemini/gemini-cli/blob/main/docs/reference/commands.md
- Antigravity CLI: https://github.com/google-antigravity/antigravity-cli
- Google Antigravity CLI `/usage` codelab: https://codelabs.developers.google.com/sdd-agy-cli
- Win-CodexBar: https://github.com/nesszer/Win-CodexBar
- Wburn: https://xakpc.dev/apps/wburn/
- Tokus: https://www.tokus.io/
- Usage Monitor for Claude: https://github.com/jens-duttke/usage-monitor-for-claude
- Claude Usage Tracker: https://pypi.org/project/claude-usage-tracker/
- TokenPeep: https://www.tokenpeep.com/
