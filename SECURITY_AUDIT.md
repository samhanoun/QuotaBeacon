# Security audit — 17–18 July 2026

## Result

The QuotaBeacon working-tree diff review covered all 86 inventoried worklist
entries with no deferred files. Three candidates were reproduced with bounded
runtime harnesses, then evaluated against the repository threat model and the
mandatory attack-path policy. None established a realistic lower-privileged
attacker path or security-relevant impact, so the final report contains zero
reportable Critical, High, Medium, or Low findings.

The three rejected candidates were still treated as engineering hardening
requirements and fixed before release:

| Area | Validated behavior | Resolution |
| --- | --- | --- |
| Provider identities | A trusted plugin could declare an invalid or canonically colliding provider ID, confusing disable state or causing a settings exception. | The catalog and settings now share one canonical lowercase ASCII identity contract; invalid and conflicting providers are isolated before reaching the UI. |
| Google reset parsing | A syntactically valid extreme relative duration could overflow `TimeSpan` before the 31-day cap, while a near-maximum observation time could overflow date addition. | Per-unit and cumulative bounds now run before conversion, non-finite values are rejected, and date addition is exception-safe. |
| Google terminal labels | Unicode bidi format controls survived ordinary control-character stripping and reached a bounded XAML label. | Unicode `Format` characters are replaced with whitespace before parsing and projection. |

The post-scan release review found and fixed a higher-impact functional safety
issue in the new Google CLI integration. Redirected standard streams put Gemini
into headless mode, where the original `/stats model` input could fall through
to a real model request instead of opening an interactive quota view. Repeated
background polling could therefore consume quota without returning quota data.
The integration now uses Windows ConPTY with Gemini's documented `/stats model`
command and Antigravity's documented `/usage` command. It first waits for the
CLI's input prompt, types bounded printable ASCII one keystroke at a time, then
arms a fresh command-menu marker immediately before the final keystroke. Enter
is sent only after that fresh autocomplete marker remains free of trust or
authentication prompts through a settling interval. Stale menu text and a late
modal prompt therefore cannot authorize Enter. This sequencing is necessary
because modern TUIs can treat a whole-line write as a pasted model prompt. The
launcher starts the CLI suspended,
assigns a kill-on-close job before resume, uses a mutable `CreateProcessW`
buffer, limits native DLL resolution to System32, retains bounded output while
continuing to drain the terminal, and terminates the contained process tree
without sending `/quit`, `/exit`, Escape, or any other prompt text. Functional
tests prove real TTY attachment, `.cmd` paths with spaces, typed command
submission, stale-marker rejection, late trust-prompt rejection, bounded noisy
output, timeouts, and descendant cleanup.

Executable discovery keeps strict filename and extension checks, prefers
provider-owned official install directories, then reads the process, Windows
user, and Windows machine PATH values with environment-variable expansion.
Antigravity is launched from an empty stable
QuotaBeacon probe directory. If AGY requests workspace trust, the provider
returns a bounded actionable diagnostic; it never auto-confirms trust or uses
the dangerous permission-bypass flag.

The same final review replaced refresh queuing with a dirty-flag coalescer.
Rapid timer/settings/provider events now produce at most one follow-up poll,
and results captured against an outdated provider set are discarded before UI
projection or alerts. Disabling a provider also evicts its alert baseline, so
re-enabling it starts with a fresh observation instead of reporting a stale
threshold or reset event.

The earlier repository-wide baseline review found four Low/P3 availability
issues, all of which remain remediated:

| ID | Area | Risk | Resolution |
| --- | --- | --- | --- |
| CLD-001 | Claude response ingestion | An unbounded decoded response could consume excessive memory. | Streamed reads now enforce decoded-size and body-read deadlines. |
| CLD-002 | Claude quota projection | Unbounded window/model fanout could consume CPU and memory. | Window counts and provider-controlled text now have explicit limits. |
| CLD-003 | Claude model key normalization | Slug construction was quadratic for long provider-controlled model names. | Normalization is linear and model names are length-bounded. |
| PACE-001 | Pace projection | Extreme reset timestamps could overflow date arithmetic and disrupt refresh. | Timestamp arithmetic is guarded and invalid projections return an unknown pace. |

Additional hardening bounds local Codex file discovery, total input bytes, file
size, and line length; skips reparse points; normalizes non-finite percentages;
isolates provider-internal cancellation; and evicts stale alert state.

## Validation

- Locked NuGet restore with audit mode `all`: passed.
- Release build with analyzers and warnings as errors: passed with zero warnings.
- Automated tests: 112 passed, 0 failed.
- Core line coverage: 84.52% (minimum 80%).
- Formatting verification: passed.
- Self-contained x64 publish: passed.
- Local checksum-verified Gitleaks scans of Git history and the staged patch:
  passed.
- Packaged WinUI smoke test: passed, including all four provider controls on
  the overview and in Settings plus sanitized history persistence.
- Live Gemini ConPTY probe: safely executed `/stats model` without a model
  request and confirmed the current CLI's fresh-session quota limitation.
- Live Antigravity ConPTY probe: parsed four grouped weekly/five-hour quota
  windows from `/usage` without exposing captured account data.

GitHub CI repeats the locked restore and NuGet audit, formatting, analyzer-clean
Release build, full tests, coverage gate, and self-contained x64 publish. The
interactive packaged-app smoke test and live provider probe remain local release
checks. Separate workflows run CodeQL's extended C# queries, dependency review
at Moderate-or-higher severity, and full-history secret scanning. Third-party
actions are pinned to immutable commit SHAs and the scanner binary is
checksum-pinned. Dependabot proposes reviewed action and NuGet updates.

## Residual trust boundaries

- Provider APIs and local CLI formats can change without notice. Failures must
  remain explicit and isolated rather than being interpreted as zero usage.
- Local session metadata is untrusted input even though it is on the same
  machine; resource limits remain part of the security boundary.
- External provider DLLs are trusted native application extensions, not a
  sandbox. Install only plugins whose publisher and source are trusted.
