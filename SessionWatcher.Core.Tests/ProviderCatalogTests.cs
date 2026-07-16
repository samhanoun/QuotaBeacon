using SessionWatcher.Core.Models;
using SessionWatcher.Core.Plugins;
using SessionWatcher.Core.Providers;

namespace SessionWatcher.Core.Tests;

public sealed class ProviderCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sessionwatcher-plugins-{Guid.NewGuid():N}");

    [Fact]
    public void Catalog_loads_trusted_plugin_assemblies()
    {
        Directory.CreateDirectory(_directory);
        File.Copy(
            typeof(ProviderCatalogTests).Assembly.Location,
            Path.Combine(_directory, "SessionWatcher.SamplePlugin.dll"));

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
