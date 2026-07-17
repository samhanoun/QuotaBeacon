using QuotaBeacon.Core.Models;
using QuotaBeacon.Core.Plugins;
using QuotaBeacon.Core.Providers;
using QuotaBeacon.Core.Settings;

namespace QuotaBeacon.Core.Tests;

public sealed class ProviderCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quotabeacon-plugins-{Guid.NewGuid():N}");

    [Fact]
    public void Catalog_loads_trusted_plugin_assemblies()
    {
        Directory.CreateDirectory(_directory);
        File.Copy(
            typeof(ProviderCatalogTests).Assembly.Location,
            Path.Combine(_directory, "QuotaBeacon.SamplePlugin.dll"));

        var result = ProviderCatalog.Load(_directory, [new CatalogProvider("built-in", "Built in")]);

        Assert.Contains(result.Providers, provider => provider.Id == "built-in");
        Assert.Contains(result.Providers, provider => provider.Id == "sample");
        Assert.Contains(result.Plugins, plugin => plugin.Id == "sample-plugin");
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Catalog_rejects_duplicate_provider_ids_and_invalid_binaries_safely()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "broken.dll"), "not an assembly");
        var duplicatePlugin = new InMemoryPlugin(
            "duplicate-plugin",
            [new CatalogProvider("codex", "Duplicate Codex")]);

        var result = ProviderCatalog.Compose(
            [new CatalogProvider("codex", "Codex")],
            [(duplicatePlugin, "memory")]);
        var loaded = ProviderCatalog.Load(_directory, result.Providers);

        Assert.Single(result.Providers);
        Assert.Contains(result.Issues, issue => issue.Message == "A provider with ID 'codex' is already registered.");
        Assert.Contains(loaded.Issues, issue => issue.FileName == "broken.dll");
        Assert.DoesNotContain("BadImageFormat", loaded.Issues.Single().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_enforces_the_same_provider_identity_domain_as_settings()
    {
        var plugin = new InMemoryPlugin(
            "identity-plugin",
            [
                new CatalogProvider(" codex ", "Canonical collision"),
                new CatalogProvider("../unsafe", "Invalid grammar"),
                new CatalogProvider(" ", "Blank identifier"),
                new CatalogProvider("gemini", "Valid provider")
            ]);

        var result = ProviderCatalog.Compose(
            [new CatalogProvider("codex", "Codex")],
            [(plugin, "identity.dll")]);

        Assert.Collection(
            result.Providers,
            provider => Assert.Equal("codex", provider.Id),
            provider => Assert.Equal("gemini", provider.Id));
        Assert.Equal(3, result.Issues.Count);
        Assert.Equal(3, result.Issues.Count(issue =>
            issue.Message == "A provider declared an invalid ID."));

        var disabled = AppSettings.Default.WithProviderEnabled("gemini", enabled: false);
        Assert.False(disabled.IsProviderEnabled(result.Providers[1].Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public sealed class SampleUsagePlugin : IUsageProviderPlugin
{
    public string Id => "sample-plugin";

    public string DisplayName => "Sample plugin";

    public IReadOnlyList<IUsageProvider> CreateProviders() =>
        [new CatalogProvider("sample", "Sample")];
}

public sealed class CatalogProvider(string id, string displayName) : IUsageProvider
{
    public string Id => id;

    public string DisplayName => displayName;

    public Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderSnapshot(
            id,
            displayName,
            DateTimeOffset.UtcNow,
            SnapshotSource.Live,
            SnapshotStatus.Available,
            []));
}

public sealed class InMemoryPlugin(string id, IReadOnlyList<IUsageProvider> providers) : IUsageProviderPlugin
{
    public string Id => id;

    public string DisplayName => id;

    public IReadOnlyList<IUsageProvider> CreateProviders() => providers;
}
