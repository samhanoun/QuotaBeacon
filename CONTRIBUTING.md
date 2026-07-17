# Contributing to Quota Beacon

Quota Beacon accepts focused changes that preserve its core guarantees: usage
data stays local, provider failures remain isolated, and quota data is never
confused with local estimates.

## Development loop

Use Windows, the SDK pinned by `global.json`, and the Windows application
development workload. Create a short-lived branch and run:

```powershell
dotnet restore QuotaBeacon.slnx --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all
dotnet format QuotaBeacon.slnx --no-restore --verify-no-changes
$env:CI = 'true'
dotnet build QuotaBeacon.slnx -c Release --no-restore
dotnet test QuotaBeacon.Core.Tests\QuotaBeacon.Core.Tests.csproj `
  -c Release --no-build --no-restore `
  --collect:"XPlat Code Coverage" `
  --settings coverlet.runsettings
```

Add or update tests before changing observable behavior. Core line coverage
must stay at or above 80%. Commit `packages.lock.json` changes whenever a
package changes.

## Security expectations

- Treat provider responses, session logs, plugin files, and settings as
  untrusted input.
- Bound network bodies, file traversal, line length, collections, and time.
- Never log credentials, prompts, responses, source code, or raw provider
  payloads.
- Keep authentication material in memory only as long as a request requires.
- Preserve cancellation and isolate one provider's failure from all others.
- Document new network endpoints, permissions, persistence, and trust-boundary
  changes in the pull request.

GitHub Actions run locked restore plus NuGet audit, formatting, a warning-free
release build, tests and coverage, CodeQL, dependency review, and Gitleaks.
Resolve every failed gate; do not bypass or weaken a check to merge a change.

Report vulnerabilities through the private process in `SECURITY.md`, not an
issue or pull request.
