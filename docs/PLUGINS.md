# Provider plugins

Quota Beacon loads trusted `.dll` files from the folder shown on its Plugins
page at startup. Plugins run inside the Quota Beacon process with the same
permissions as the current user. Install only assemblies you have audited or
received from a publisher you trust.

## Contract

A plugin references the compatibility assembly `QuotaBeacon.Core` and exposes a public, non-abstract
type with a parameterless constructor that implements `IUsageProviderPlugin`:

```csharp
using QuotaBeacon.Core.Plugins;
using QuotaBeacon.Core.Providers;

public sealed class ExamplePlugin : IUsageProviderPlugin
{
    public string Id => "example-plugin";
    public string DisplayName => "Example provider plugin";

    public IReadOnlyList<IUsageProvider> CreateProviders() =>
        [new ExampleUsageProvider()];
}
```

Each `IUsageProvider` has a stable ID, display name, and asynchronous snapshot
method. A snapshot reports its provenance (`Live`, `LocalFallback`, or `Cache`),
status, observed time, optional plan label, and independent quota windows.
Quota Beacon owns reset countdowns, pace calculations, history, alerts, and
dashboard rendering.

Provider diagnostics must be safe for display and persistence boundaries:
never include credentials, response bodies, prompts, source code, or private
paths. Honor cancellation and apply a finite timeout to network/process work.
Return a provider-level unavailable/error snapshot for expected failures;
throw only for cancellation or unexpected failures. The coordinator isolates
one provider failure from all other providers.

## Packaging and loading

- Target .NET 10 and reference the same `QuotaBeacon.Core` contract version.
- Prefer a single plugin assembly. If dependencies are required, keep their
  versions private and test loading from a clean plugin folder.
- Copy the plugin DLL into the in-app plugin folder and restart Quota Beacon.
- Plugin and provider IDs must already be canonical lowercase ASCII identifiers:
  1-64 letters, digits, `.`, `_`, or `-`, with no surrounding whitespace.
  Invalid IDs and case-insensitive conflicts are isolated as load issues;
  built-in providers always win. This is the same identity domain used by
  provider enable/disable settings.
- Removing a DLL also requires an app restart.

## Rename compatibility

The Quota Beacon rename intentionally changes the managed plugin contract from
`SessionWatcher.Core` to `QuotaBeacon.Core`. Settings and sanitized usage
history migrate automatically, but pre-rename plugin binaries are not ABI
compatible. Quota Beacon detects and rejects those assemblies with an
actionable load issue; rebuild and redeploy them against the current
`QuotaBeacon.Core` project. This avoids silently executing an adapter with a
different type identity or snapshot contract.

This first contract is intentionally small. Future hardening can add signed
manifests and out-of-process isolation without changing the neutral snapshot
model used by the dashboard.
