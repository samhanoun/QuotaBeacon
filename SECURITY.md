# Security policy

## Supported versions

Until the first stable release, security updates are applied to the latest
commit on the default branch only.

## Reporting a vulnerability

Use **Report a vulnerability** from the repository's Security tab. Repository
maintainers must keep GitHub private vulnerability reporting enabled.

Do not open a public issue, discussion, or pull request. Do not include real
access tokens, prompts, responses, account data, or private source code in a
report. A useful report includes the affected version, impact, reproduction
steps using synthetic data, and any suggested mitigation.

The project aims to acknowledge a report within three business days, validate
it within seven, and coordinate a fix and disclosure according to severity.

## Security model

Quota Beacon reads local Claude Code and Codex state, invokes the official
Gemini and Antigravity CLIs for their usage screens, and makes read-only usage
requests. It stores sanitized usage snapshots, never prompts or responses.
External provider DLLs execute with the user's full application privileges and
must therefore come only from trusted publishers.
