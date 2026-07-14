using System.Reflection;
using System.Runtime.Loader;

namespace EasyHomeServer.Host.Modules;

/// <summary>
/// Load context for a single module, giving each module its own resolution scope so two
/// modules can depend on different versions of the same third-party library.
/// </summary>
/// <remarks>
/// <para>
/// Resolution deliberately prefers the host: anything the default context can supply — the
/// framework, the SDK, MudBlazor — is shared rather than loaded privately. This matters
/// because type identity in .NET includes the load context. If a module loaded its own copy
/// of the SDK, the <c>IModule</c> it implements would be a different type from the
/// <c>IModule</c> the host looks for, and discovery would silently find nothing. The same
/// applies to MudBlazor: a private copy would render components the host's
/// <c>MudThemeProvider</c> knows nothing about.
/// </para>
/// <para>
/// Only genuinely module-private dependencies — those the host has never heard of — are
/// resolved from the module directory via its deps.json.
/// </para>
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string moduleAssemblyPath, string name)
        : base(name, isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(moduleAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share with the host wherever possible; see the note on type identity above.
        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch (FileNotFoundException)
        {
            // Not a host/framework assembly — fall through to the module's own dependencies.
        }
        catch (FileLoadException)
        {
            // Present but unloadable from the default context; try the module's copy.
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);

        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);

        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
