using System.Collections.Immutable;
using System.Reflection;
using EasyHomeServer.Sdk;

namespace EasyHomeServer.Host.Modules;

/// <summary>
/// The result of the startup module scan: everything that loaded, and everything that did
/// not and why. Registered as a singleton so the shell can build navigation from it and
/// surface failures in the UI.
/// </summary>
public sealed class ModuleCatalog
{
    /// <summary>Modules that loaded successfully, in navigation order.</summary>
    public ImmutableArray<LoadedModule> Modules { get; }

    /// <summary>
    /// Modules that were found on disk but could not be loaded. Surfaced in the UI rather
    /// than being fatal — one broken module must not take the server down.
    /// </summary>
    public ImmutableArray<ModuleLoadFailure> Failures { get; }

    /// <summary>
    /// Module assemblies containing routable components, for <c>Router.AdditionalAssemblies</c>
    /// and <c>AddAdditionalAssemblies</c>.
    /// </summary>
    public ImmutableArray<Assembly> ComponentAssemblies { get; }

    public ModuleCatalog(IEnumerable<LoadedModule> modules, IEnumerable<ModuleLoadFailure> failures)
    {
        Modules = modules
            .OrderBy(m => m.Manifest.NavOrder)
            .ThenBy(m => m.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        Failures = failures.ToImmutableArray();
        ComponentAssemblies = Modules.Select(m => m.Assembly).ToImmutableArray();
    }
}

/// <summary>A module that loaded and is live in the host.</summary>
public sealed class LoadedModule
{
    /// <summary>The module's entry point instance.</summary>
    public required IModule Instance { get; init; }

    /// <summary>The module assembly, loaded in its own <see cref="PluginLoadContext"/>.</summary>
    public required Assembly Assembly { get; init; }

    /// <summary>Directory the module was loaded from.</summary>
    public required string Directory { get; init; }

    /// <summary>The SDK assembly version the module was compiled against.</summary>
    public required Version SdkVersion { get; init; }

    /// <summary>Convenience accessor for <see cref="IModule.Manifest"/>.</summary>
    public ModuleManifest Manifest => Instance.Manifest;
}

/// <summary>A module directory that could not be turned into a running module.</summary>
public sealed record ModuleLoadFailure
{
    /// <summary>Directory name of the module that failed, used as a display name.</summary>
    public required string Name { get; init; }

    /// <summary>Path that was scanned.</summary>
    public required string Path { get; init; }

    /// <summary>What went wrong, phrased for a human reading the UI.</summary>
    public required string Reason { get; init; }

    /// <summary>Category of failure, so the UI can hint at the fix.</summary>
    public required ModuleLoadFailureKind Kind { get; init; }
}

/// <summary>Why a module failed to load.</summary>
public enum ModuleLoadFailureKind
{
    /// <summary>The module was built against an incompatible SDK contract version.</summary>
    SdkVersionMismatch,

    /// <summary>No assembly in the directory contained a usable <see cref="IModule"/> implementation.</summary>
    NoModuleType,

    /// <summary>The assembly could not be loaded or its types could not be inspected.</summary>
    LoadError,

    /// <summary>The module type was found but threw while being constructed or configured.</summary>
    InitializationError,

    /// <summary>The manifest was missing required values or collided with another module.</summary>
    InvalidManifest,
}
