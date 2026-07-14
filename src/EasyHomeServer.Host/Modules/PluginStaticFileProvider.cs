using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace EasyHomeServer.Host.Modules;

/// <summary>
/// Serves static web assets that live inside dynamically loaded module assemblies, under the
/// conventional <c>/_content/{AssemblyName}/...</c> path.
/// </summary>
/// <remarks>
/// <para>
/// This is the sharp edge of the plugin model. Razor class libraries normally get their
/// <c>wwwroot</c> published to <c>_content/{AssemblyName}</c> by the static web assets build
/// pipeline, but that pipeline resolves everything at the host's <em>build</em> time. A module
/// discovered at runtime was never part of the host's build, so its assets are invisible to
/// it and every request 404s.
/// </para>
/// <para>
/// The fix: modules embed their <c>wwwroot</c> into the assembly (with a file manifest, so the
/// original directory structure and file names survive verbatim — plain
/// <c>EmbeddedFileProvider</c> mangles paths containing dots or dashes). At startup the host
/// opens a <see cref="ManifestEmbeddedFileProvider"/> over each module assembly and this
/// provider routes <c>/_content/{AssemblyName}/*</c> to the right one.
/// </para>
/// <para>
/// A module with no embedded <c>wwwroot</c> is simply absent from the map and its lookups miss
/// cleanly, so having no assets is not an error.
/// </para>
/// </remarks>
internal sealed class PluginStaticFileProvider : IFileProvider
{
    private const string ContentPrefix = "/_content/";

    private readonly IReadOnlyDictionary<string, IFileProvider> _providersByAssembly;

    private PluginStaticFileProvider(IReadOnlyDictionary<string, IFileProvider> providersByAssembly)
    {
        _providersByAssembly = providersByAssembly;
    }

    /// <summary>
    /// Builds a provider covering every module in the catalog that embeds a <c>wwwroot</c>.
    /// </summary>
    public static PluginStaticFileProvider Create(ModuleCatalog catalog, ILogger logger)
    {
        var providers = new Dictionary<string, IFileProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in catalog.Modules)
        {
            var assemblyName = module.Assembly.GetName().Name;

            if (string.IsNullOrEmpty(assemblyName))
            {
                continue;
            }

            if (!TryCreateProvider(module.Assembly, out var provider))
            {
                logger.LogDebug(
                    "Module {ModuleId} embeds no static web assets; nothing mapped under {Path}.",
                    module.Manifest.Id,
                    $"{ContentPrefix}{assemblyName}/");

                continue;
            }

            providers[assemblyName] = provider;

            var assetCount = CountAssets(provider, "/");

            logger.LogInformation(
                "Mapped {AssetCount} embedded static asset(s) for module {ModuleId} at {Path}.",
                assetCount,
                module.Manifest.Id,
                $"{ContentPrefix}{assemblyName}/");
        }

        return new PluginStaticFileProvider(providers);
    }

    /// <summary>Counts assets recursively, so the startup log reports files rather than top-level folders.</summary>
    private static int CountAssets(IFileProvider provider, string path)
    {
        var count = 0;

        foreach (var entry in provider.GetDirectoryContents(path))
        {
            count += entry.IsDirectory
                ? CountAssets(provider, $"{path.TrimEnd('/')}/{entry.Name}")
                : 1;
        }

        return count;
    }

    private static bool TryCreateProvider(Assembly assembly, out IFileProvider provider)
    {
        provider = new NullFileProvider();

        try
        {
            var embedded = new ManifestEmbeddedFileProvider(assembly, "wwwroot");

            // The manifest exists but may not contain a wwwroot root; probing confirms it does.
            // The probe path must be "/", not "": ManifestEmbeddedFileProvider reports the empty
            // path as non-existent even when the root is populated.
            if (!embedded.GetDirectoryContents("/").Exists)
            {
                return false;
            }

            provider = embedded;

            return true;
        }
        catch (InvalidOperationException)
        {
            // No embedded file manifest in this assembly — the module ships no assets.
            return false;
        }
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        if (!TryResolve(subpath, out var provider, out var relativePath))
        {
            return new NotFoundFileInfo(subpath);
        }

        return provider.GetFileInfo(relativePath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        if (!TryResolve(subpath, out var provider, out var relativePath))
        {
            return NotFoundDirectoryContents.Singleton;
        }

        return provider.GetDirectoryContents(relativePath);
    }

    public IChangeToken Watch(string filter)
    {
        // Assets are compiled into the module assembly and cannot change without a restart.
        return NullChangeToken.Singleton;
    }

    private bool TryResolve(string subpath, out IFileProvider provider, out string relativePath)
    {
        provider = new NullFileProvider();
        relativePath = string.Empty;

        if (string.IsNullOrEmpty(subpath))
        {
            return false;
        }

        var path = subpath.Replace('\\', '/');

        if (!path.StartsWith('/'))
        {
            path = '/' + path;
        }

        if (!path.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = path[ContentPrefix.Length..];
        var slash = remainder.IndexOf('/');

        if (slash <= 0 || slash == remainder.Length - 1)
        {
            return false;
        }

        var assemblyName = remainder[..slash];

        if (!_providersByAssembly.TryGetValue(assemblyName, out var found))
        {
            return false;
        }

        provider = found;
        relativePath = remainder[(slash + 1)..];

        return true;
    }
}
