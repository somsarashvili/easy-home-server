using EasyHomeServer.Modules.Disks.Disks;
using EasyHomeServer.Modules.Disks.MergerFs;
using EasyHomeServer.Modules.Disks.SnapRaid;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace EasyHomeServer.Modules.Disks;

/// <summary>Entry point for the Disks module: the machine's storage, and what is on it.</summary>
public sealed class DisksModule : IModule
{
    /// <inheritdoc />
    public ModuleManifest Manifest { get; } = new()
    {
        Id = "disks",
        DisplayName = "Disks",
        Version = "0.1.0",
        RoutePath = "/disks",
        Icon = Icons.Material.Filled.Storage,
        NavOrder = 15,
        Description = "Disks, partitions, mounts and drive health.",
    };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IModuleContext context)
    {
        var options = context.Configuration.Get<DisksOptions>() ?? new DisksOptions();

        services.AddSingleton(options);
        services.AddSingleton<BlockDeviceReader>();
        services.AddSingleton<SmartReader>();
        services.AddSingleton<MountManager>();
        services.AddSingleton<DiskFormatter>();
        services.AddSingleton<MergerFsReader>();
        services.AddSingleton<SnapRaidCli>();

        services.AddModuleWorker<DiskPoller>();
        services.AddModuleWorker<SnapRaidPoller>();
    }
}
