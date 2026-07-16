using System.Reflection;
using SessionWatcher.Core.Providers;

namespace SessionWatcher.Core.Plugins;

public sealed record PluginDescriptor(string Id, string DisplayName, string FileName);

public sealed record PluginLoadIssue(string FileName, string Message);

public sealed record ProviderCatalogResult(
    IReadOnlyList<IUsageProvider> Providers,
    IReadOnlyList<PluginDescriptor> Plugins,
    IReadOnlyList<PluginLoadIssue> Issues);

public static class ProviderCatalog
{
    public static ProviderCatalogResult Load(
        string directory,
        IEnumerable<IUsageProvider> builtInProviders)
    {
        var builtIns = builtInProviders.ToArray();
        if (!Directory.Exists(directory))
        {
            return new ProviderCatalogResult(builtIns, [], []);
        }

        var plugins = new List<(IUsageProviderPlugin Plugin, string FileName)>();
        var loadIssues = new List<PluginLoadIssue>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var assembly = Assembly.LoadFrom(Path.GetFullPath(path));
                var pluginTypes = assembly
                    .GetTypes()
                    .Where(type =>
                        type is { IsAbstract: false, IsInterface: false } &&
                        typeof(IUsageProviderPlugin).IsAssignableFrom(type) &&
                        type.GetConstructor(Type.EmptyTypes) is not null)
                    .ToArray();

                if (pluginTypes.Length == 0)
                {
                    loadIssues.Add(new PluginLoadIssue(
                        Path.GetFileName(path),
                        "The assembly does not contain a SessionWatcher provider plugin."));
                    continue;
                }

                foreach (var type in pluginTypes)
                {
                    if (Activator.CreateInstance(type) is IUsageProviderPlugin plugin)
                    {
                        plugins.Add((plugin, Path.GetFileName(path)));
                    }
                }
            }
            catch (Exception exception) when (exception is
                BadImageFormatException or
                FileLoadException or
                FileNotFoundException or
                ReflectionTypeLoadException or
                TargetInvocationException or
                MemberAccessException)
            {
                loadIssues.Add(new PluginLoadIssue(
                    Path.GetFileName(path),
                    "The plugin could not be loaded."));
            }
        }

        var composed = Compose(builtIns, plugins);
        return composed with { Issues = loadIssues.Concat(composed.Issues).ToArray() };
    }

    public static ProviderCatalogResult Compose(
        IEnumerable<IUsageProvider> builtInProviders,
        IEnumerable<(IUsageProviderPlugin Plugin, string FileName)> plugins)
    {
        var providers = new List<IUsageProvider>();
        var descriptors = new List<PluginDescriptor>();
        var issues = new List<PluginLoadIssue>();
        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in builtInProviders)
        {
            if (providerIds.Add(provider.Id))
            {
                providers.Add(provider);
            }
            else
            {
                issues.Add(new PluginLoadIssue(
                    "built-in",
                    $"A provider with ID '{provider.Id}' is already registered."));
            }
        }

        foreach (var (plugin, fileName) in plugins)
        {
            if (!pluginIds.Add(plugin.Id))
            {
                issues.Add(new PluginLoadIssue(fileName, $"A plugin with ID '{plugin.Id}' is already registered."));
                continue;
            }

            IReadOnlyList<IUsageProvider> pluginProviders;
            try
            {
                pluginProviders = plugin.CreateProviders();
            }
            catch
            {
                issues.Add(new PluginLoadIssue(fileName, "The plugin failed while creating its providers."));
                continue;
            }

            descriptors.Add(new PluginDescriptor(plugin.Id, plugin.DisplayName, fileName));
            foreach (var provider in pluginProviders)
            {
                if (providerIds.Add(provider.Id))
                {
                    providers.Add(provider);
                }
                else
                {
                    issues.Add(new PluginLoadIssue(
                        fileName,
                        $"A provider with ID '{provider.Id}' is already registered."));
                }
            }
        }

        return new ProviderCatalogResult(providers, descriptors, issues);
    }
}
