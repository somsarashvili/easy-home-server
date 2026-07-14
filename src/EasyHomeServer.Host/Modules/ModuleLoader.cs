using System.Reflection;
using System.Text.RegularExpressions;
using EasyHomeServer.Sdk;

namespace EasyHomeServer.Host.Modules;

/// <summary>
/// Scans the modules directory at startup and turns each subdirectory into a
/// <see cref="LoadedModule"/> or a <see cref="ModuleLoadFailure"/>.
/// </summary>
/// <remarks>
/// Every failure mode here is contained: a module that is missing, corrupt, built against
/// the wrong SDK or throws in its constructor produces a failure entry and the scan moves
/// on. Nothing a module does at load time can prevent the host from starting.
/// </remarks>
internal sealed partial class ModuleLoader(ILogger<ModuleLoader> logger)
{
    private static readonly Version HostSdkVersion =
        typeof(IModule).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    private const string SdkAssemblyName = "EasyHomeServer.Sdk";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ModuleIdPattern { get; }

    public ModuleCatalog Load(string modulesRoot)
    {
        var modules = new List<LoadedModule>();
        var failures = new List<ModuleLoadFailure>();

        if (!Directory.Exists(modulesRoot))
        {
            logger.LogInformation(
                "Modules directory {ModulesRoot} does not exist; starting with no modules.",
                modulesRoot);

            return new ModuleCatalog(modules, failures);
        }

        var directories = Directory.GetDirectories(modulesRoot).OrderBy(d => d, StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);

            try
            {
                var result = LoadModule(directory, name);

                if (result.Module is not null)
                {
                    // Ids must be unique: they key the data directory and nav entries.
                    var clash = modules.FirstOrDefault(m =>
                        string.Equals(m.Manifest.Id, result.Module.Manifest.Id, StringComparison.OrdinalIgnoreCase));

                    if (clash is not null)
                    {
                        failures.Add(new ModuleLoadFailure
                        {
                            Name = name,
                            Path = directory,
                            Kind = ModuleLoadFailureKind.InvalidManifest,
                            Reason =
                                $"Module id '{result.Module.Manifest.Id}' is already used by the module loaded "
                                + $"from '{clash.Directory}'. Module ids must be unique.",
                        });

                        continue;
                    }

                    modules.Add(result.Module);
                }
                else if (result.Failure is not null)
                {
                    failures.Add(result.Failure);
                }
            }
            catch (Exception ex)
            {
                // Defensive: LoadModule is expected to convert its own errors into failures.
                failures.Add(new ModuleLoadFailure
                {
                    Name = name,
                    Path = directory,
                    Kind = ModuleLoadFailureKind.LoadError,
                    Reason = $"Unexpected error while loading: {ex.Message}",
                });

                logger.LogError(ex, "Unexpected error loading module from {Directory}.", directory);
            }
        }

        foreach (var module in modules)
        {
            logger.LogInformation(
                "Loaded module {ModuleId} ({DisplayName}) version {Version} from {Directory} (SDK {SdkVersion}).",
                module.Manifest.Id,
                module.Manifest.DisplayName,
                module.Manifest.Version,
                module.Directory,
                module.SdkVersion);
        }

        foreach (var failure in failures)
        {
            logger.LogError(
                "Module {ModuleName} at {Path} was not loaded ({Kind}): {Reason}",
                failure.Name,
                failure.Path,
                failure.Kind,
                failure.Reason);
        }

        logger.LogInformation(
            "Module scan of {ModulesRoot} complete: {LoadedCount} loaded, {FailedCount} failed. Host SDK version {HostSdkVersion}.",
            modulesRoot,
            modules.Count,
            failures.Count,
            HostSdkVersion);

        return new ModuleCatalog(modules, failures);
    }

    private (LoadedModule? Module, ModuleLoadFailure? Failure) LoadModule(string directory, string name)
    {
        ModuleLoadFailure Fail(ModuleLoadFailureKind kind, string reason) =>
            new() { Name = name, Path = directory, Kind = kind, Reason = reason };

        if (!TryResolveAssemblyPath(directory, out var assemblyPath, out var resolveError))
        {
            return (null, Fail(ModuleLoadFailureKind.NoModuleType, resolveError));
        }

        Assembly assembly;
        try
        {
            var context = new PluginLoadContext(assemblyPath, name);
            assembly = context.LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception ex)
        {
            return (null, Fail(
                ModuleLoadFailureKind.LoadError,
                $"Could not load '{Path.GetFileName(assemblyPath)}': {ex.Message}"));
        }

        // Compatibility is checked against the SDK version recorded in the module's assembly
        // references at compile time. This is authoritative and needs no cooperation from the
        // module: it cannot be spoofed by the manifest, and it is checked before any module
        // code runs.
        var sdkReference = assembly
            .GetReferencedAssemblies()
            .FirstOrDefault(a => string.Equals(a.Name, SdkAssemblyName, StringComparison.OrdinalIgnoreCase));

        if (sdkReference?.Version is null)
        {
            return (null, Fail(
                ModuleLoadFailureKind.NoModuleType,
                $"'{Path.GetFileName(assemblyPath)}' does not reference {SdkAssemblyName} and is not a module."));
        }

        if (sdkReference.Version.Major != HostSdkVersion.Major)
        {
            return (null, Fail(
                ModuleLoadFailureKind.SdkVersionMismatch,
                $"Built against SDK contract version {sdkReference.Version.Major} "
                    + $"(={sdkReference.Version}), but this host provides version {HostSdkVersion.Major} "
                    + $"(={HostSdkVersion}). Install a build of this module for SDK "
                    + $"{HostSdkVersion.Major}, or upgrade the host."));
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Typically a missing dependency that was not published alongside the module.
            var detail = ex.LoaderExceptions
                .Where(e => e is not null)
                .Select(e => e!.Message)
                .Distinct()
                .Take(3);

            return (null, Fail(
                ModuleLoadFailureKind.LoadError,
                $"Could not inspect types in '{Path.GetFileName(assemblyPath)}'. "
                    + $"A dependency is probably missing: {string.Join("; ", detail)}"));
        }

        var moduleTypes = types
            .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false, IsPublic: true })
            .ToList();

        if (moduleTypes.Count == 0)
        {
            return (null, Fail(
                ModuleLoadFailureKind.NoModuleType,
                $"'{Path.GetFileName(assemblyPath)}' contains no public non-abstract implementation of IModule."));
        }

        if (moduleTypes.Count > 1)
        {
            return (null, Fail(
                ModuleLoadFailureKind.InvalidManifest,
                $"'{Path.GetFileName(assemblyPath)}' contains {moduleTypes.Count} IModule implementations "
                    + $"({string.Join(", ", moduleTypes.Select(t => t.Name))}); exactly one is required."));
        }

        var moduleType = moduleTypes[0];

        if (moduleType.GetConstructor(Type.EmptyTypes) is null)
        {
            return (null, Fail(
                ModuleLoadFailureKind.InitializationError,
                $"'{moduleType.FullName}' must have a public parameterless constructor. "
                    + "Modules are created before the DI container exists; take dependencies in ConfigureServices instead."));
        }

        IModule instance;
        try
        {
            instance = (IModule)Activator.CreateInstance(moduleType)!;
        }
        catch (Exception ex)
        {
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

            return (null, Fail(
                ModuleLoadFailureKind.InitializationError,
                $"'{moduleType.FullName}' threw while being constructed: {cause.Message}"));
        }

        ModuleManifest manifest;
        try
        {
            manifest = instance.Manifest;
        }
        catch (Exception ex)
        {
            return (null, Fail(
                ModuleLoadFailureKind.InvalidManifest,
                $"'{moduleType.FullName}' threw while returning its manifest: {ex.Message}"));
        }

        if (ValidateManifest(manifest) is { } manifestError)
        {
            return (null, Fail(ModuleLoadFailureKind.InvalidManifest, manifestError));
        }

        return (new LoadedModule
        {
            Instance = instance,
            Assembly = assembly,
            Directory = directory,
            SdkVersion = sdkReference.Version,
        }, null);
    }

    /// <summary>
    /// Finds the module's own assembly. A published module directory contains exactly one
    /// deps.json, named after the module assembly, which is a more reliable marker than
    /// guessing among the dlls.
    /// </summary>
    private static bool TryResolveAssemblyPath(string directory, out string assemblyPath, out string error)
    {
        assemblyPath = string.Empty;
        error = string.Empty;

        var depsFiles = Directory.GetFiles(directory, "*.deps.json", SearchOption.TopDirectoryOnly);

        if (depsFiles.Length == 1)
        {
            var candidate = depsFiles[0][..^".deps.json".Length] + ".dll";

            if (File.Exists(candidate))
            {
                assemblyPath = candidate;

                return true;
            }

            error = $"'{Path.GetFileName(depsFiles[0])}' has no matching '{Path.GetFileName(candidate)}'.";

            return false;
        }

        if (depsFiles.Length > 1)
        {
            error =
                $"Found {depsFiles.Length} .deps.json files; a module directory must contain exactly one "
                + "published module.";

            return false;
        }

        // No deps.json (e.g. hand-assembled directory): fall back to the naming convention.
        var byConvention = Directory.GetFiles(directory, "EasyHomeServer.Modules.*.dll", SearchOption.TopDirectoryOnly);

        if (byConvention.Length == 1)
        {
            assemblyPath = byConvention[0];

            return true;
        }

        error = byConvention.Length == 0
            ? "No .deps.json and no EasyHomeServer.Modules.*.dll found; this is not a published module directory."
            : $"No .deps.json and {byConvention.Length} candidate module assemblies found; cannot pick one.";

        return false;
    }

    private static string? ValidateManifest(ModuleManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || !ModuleIdPattern.IsMatch(manifest.Id))
        {
            return $"Manifest id '{manifest.Id}' is invalid: use lowercase letters, digits and hyphens, "
                + "starting with a letter or digit (for example 'systeminfo').";
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            return "Manifest DisplayName is required.";
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return "Manifest Version is required.";
        }

        if (string.IsNullOrWhiteSpace(manifest.Icon))
        {
            return "Manifest Icon is required.";
        }

        if (string.IsNullOrWhiteSpace(manifest.RoutePath) || !manifest.RoutePath.StartsWith('/'))
        {
            return $"Manifest RoutePath '{manifest.RoutePath}' must be an absolute route beginning with '/'.";
        }

        return null;
    }
}
