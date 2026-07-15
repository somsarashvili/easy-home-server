using EasyHomeServer.Modules.Docker.Docker;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace EasyHomeServer.Modules.Docker;

/// <summary>
/// Entry point for the Docker module. Found by the host's assembly scan; no reference exists in
/// either direction between this project and the host.
/// </summary>
public sealed class DockerModule : IModule
{
    /// <inheritdoc />
    public ModuleManifest Manifest { get; } = new()
    {
        Id = "docker",
        DisplayName = "Docker",
        Version = "0.1.0",
        RoutePath = "/docker",
        // MudBlazor 9.7 ships no Docker brand icon; a shipping container is the nearest thing
        // in the Material set and reads correctly at nav size.
        Icon = Icons.Material.Filled.ViewInAr,
        NavOrder = 20,
        Description = "Manage containers, images, volumes and networks.",
    };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IModuleContext context)
    {
        var options = context.Configuration.Get<DockerOptions>() ?? new DockerOptions();

        services.AddSingleton(options);
        services.AddSingleton<MacvlanShim>();
        services.AddSingleton<DockerCli>();
        services.AddSingleton<ComposeCli>();
        services.AddSingleton<ComposeDiscovery>();

        // Singleton: the poller holds the previous poll's containers to diff against, and the
        // latest snapshot that pages render on first load.
        services.AddModuleWorker<DockerPoller>();
    }
}
