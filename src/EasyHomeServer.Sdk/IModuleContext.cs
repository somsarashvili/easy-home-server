using Microsoft.Extensions.Configuration;

namespace EasyHomeServer.Sdk;

/// <summary>
/// Host-provided context handed to a module during <see cref="IModule.ConfigureServices"/>.
/// </summary>
public interface IModuleContext
{
    /// <summary>
    /// Configuration section scoped to this module (<c>Modules:{Id}</c> in host config), so
    /// modules cannot read or collide with each other's settings.
    /// </summary>
    IConfiguration Configuration { get; }

    /// <summary>
    /// Writable directory reserved for this module's own state, already created.
    /// Production: <c>/var/lib/easyhomeserver/modules/{Id}</c>.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>True when the host runs on Linux. Modules that read /proc should degrade gracefully when false.</summary>
    bool IsLinux { get; }
}
