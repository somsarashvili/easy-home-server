using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;

namespace EasyHomeServer.Host.Modules;

/// <summary>
/// Loads shared contract assemblies into the default load context before any module is loaded,
/// so that a type published by one module and consumed by another is the <em>same</em> type.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of how <see cref="IEventBus"/> and .NET type identity interact. A type's
/// identity includes its load context. Each module gets its own <see cref="PluginLoadContext"/>,
/// so if the Docker module and the Avahi module each loaded their own copy of
/// <c>EasyHomeServer.Contracts.Docker</c>, <c>ContainerInventory</c> would be two distinct types.
/// The bus keys subscriptions by <see cref="Type"/>, so the publisher's events would never reach
/// the subscriber — and nothing would throw. It would simply never work.
/// </para>
/// <para>
/// Loading them here, into <see cref="AssemblyLoadContext.Default"/>, means
/// <see cref="PluginLoadContext"/>'s "prefer the host" rule finds them for every module and they
/// all share one instance. It has to happen before the module scan: an assembly already loaded
/// privately by a module cannot be retrofitted into Default.
/// </para>
/// <para>
/// Contract assemblies live in their own directory, and their own package, rather than inside a
/// module's: the Avahi module depends on the Docker <em>contract</em>, not on the Docker module
/// being installed. With the contract present it starts cleanly and simply never receives an
/// event.
/// </para>
/// </remarks>
internal sealed class SharedAssemblyLoader(ILogger<SharedAssemblyLoader> logger)
{
    /// <summary>
    /// Loads every assembly in <paramref name="sharedRoot"/> into the default context.
    /// Returns the simple names of what was loaded, for diagnostics.
    /// </summary>
    public ImmutableArray<string> Load(string sharedRoot)
    {
        if (!Directory.Exists(sharedRoot))
        {
            logger.LogInformation(
                "Shared contracts directory {SharedRoot} does not exist; no cross-module contracts are available.",
                sharedRoot);

            return [];
        }

        var loaded = ImmutableArray.CreateBuilder<string>();

        foreach (var path in Directory.GetFiles(sharedRoot, "*.dll", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                var name = assembly.GetName();

                loaded.Add(name.Name ?? Path.GetFileNameWithoutExtension(path));

                logger.LogInformation(
                    "Shared contract {AssemblyName} {Version} loaded from {Path}.",
                    name.Name,
                    name.Version,
                    path);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
            {
                // A broken contract assembly is not fatal to the host, but every module that
                // needs it will fail to load — so this is an error, not a warning.
                logger.LogError(
                    ex,
                    "Could not load shared contract from {Path}. Modules depending on it will fail to load.",
                    path);
            }
        }

        return loaded.ToImmutable();
    }
}
