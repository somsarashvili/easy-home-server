using System.Runtime.InteropServices;
using EasyHomeServer.Sdk;

namespace EasyHomeServer.Host.Modules;

/// <inheritdoc />
internal sealed class ModuleContext : IModuleContext
{
    /// <inheritdoc />
    public IConfiguration Configuration { get; }

    /// <inheritdoc />
    public string DataDirectory { get; }

    /// <inheritdoc />
    public bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private ModuleContext(IConfiguration configuration, string dataDirectory)
    {
        Configuration = configuration;
        DataDirectory = dataDirectory;
    }

    /// <summary>
    /// Builds the context for one module: a configuration section scoped to
    /// <c>Modules:{id}</c> and a data directory created on demand.
    /// </summary>
    public static ModuleContext Create(string moduleId, IConfiguration hostConfiguration, string dataRoot)
    {
        var dataDirectory = Path.Combine(dataRoot, "modules", moduleId);
        Directory.CreateDirectory(dataDirectory);

        return new ModuleContext(hostConfiguration.GetSection($"Modules:{moduleId}"), dataDirectory);
    }
}
