# Security audit — 17 July 2026

## Result

The repository-wide review covered all 60 inventoried source worklist entries
with no deferred files. It found four Low/P3 availability issues and no
Critical, High, or Medium findings. All four findings were remediated and
protected by regression tests before release hardening was committed.

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
- Automated tests: 73 passed, 0 failed.
- Core line coverage: 82.59% (minimum 80%).
- Formatting verification: passed.

GitHub CI repeats those checks. Separate workflows run CodeQL's extended C#
queries, dependency review at Moderate-or-higher severity, and full-history
secret scanning. Third-party actions are pinned to immutable commit SHAs and
Dependabot proposes reviewed updates.

## Residual trust boundaries

- Provider APIs and local CLI formats can change without notice. Failures must
  remain explicit and isolated rather than being interpreted as zero usage.
- Local session metadata is untrusted input even though it is on the same
  machine; resource limits remain part of the security boundary.
- External provider DLLs are trusted native application extensions, not a
  sandbox. Install only plugins whose publisher and source are trusted.
