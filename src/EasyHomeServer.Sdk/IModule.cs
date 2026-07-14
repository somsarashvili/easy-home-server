using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EasyHomeServer.Sdk;

/// <summary>
/// Entry point of a module. Exactly one public, non-abstract implementation with a
/// parameterless constructor must exist per module assembly; the host discovers it by
/// scanning the assembly at startup.
/// </summary>
/// <remarks>
/// Implementations are instantiated before the DI container is built, so the constructor
/// must not depend on services. Do work in <see cref="ConfigureServices"/> or in a
/// <see cref="ModuleBackgroundService"/>.
/// </remarks>
public interface IModule
{
    /// <summary>Identity and presentation metadata for this module.</summary>
    ModuleManifest Manifest { get; }

    /// <summary>
    /// Registers the module's services into the host container. Called once at startup,
    /// before the application is built. Register background work with
    /// <see cref="ModuleServiceCollectionExtensions.AddModuleWorker{TWorker}"/>.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IModuleContext context);

    /// <summary>
    /// Maps any minimal-API endpoints the module needs. Most modules are pure Blazor and
    /// do not need this. Endpoints should be routed under <c>/api/{Manifest.Id}/</c>.
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
