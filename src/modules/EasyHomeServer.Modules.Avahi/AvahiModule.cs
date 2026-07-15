using EasyHomeServer.Modules.Avahi.Avahi;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace EasyHomeServer.Modules.Avahi;

/// <summary>
/// Entry point for the Avahi module: advertises this machine's services on the LAN over mDNS.
/// </summary>
public sealed class AvahiModule : IModule
{
    /// <inheritdoc />
    public ModuleManifest Manifest { get; } = new()
    {
        Id = "avahi",
        DisplayName = "Network Discovery",
        Version = "0.1.0",
        RoutePath = "/avahi",
        Icon = Icons.Material.Filled.Sensors,
        NavOrder = 30,
        Description = "Advertise this server and its containers on the local network.",
    };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IModuleContext context)
    {
        var options = context.Configuration.Get<AvahiOptions>() ?? new AvahiOptions();

        services.AddSingleton(options);
        services.AddSingleton<AvahiServiceStore>();
        services.AddSingleton<AvahiHostsFile>();
        services.AddSingleton<AvahiBrowser>();

        // One reconciler owns every advertisement this module writes. See the note in
        // AdvertisementReconciler on why splitting it in two would make them fight.
        services.AddModuleWorker<AdvertisementReconciler>();
    }
}
