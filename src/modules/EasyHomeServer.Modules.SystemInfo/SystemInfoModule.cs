using EasyHomeServer.Modules.SystemInfo.Metrics;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace EasyHomeServer.Modules.SystemInfo;

/// <summary>
/// Entry point for the System Info module. The host finds this type by scanning the assembly;
/// there is no reference in either direction between the host project and this one.
/// </summary>
public sealed class SystemInfoModule : IModule
{
    /// <inheritdoc />
    public ModuleManifest Manifest { get; } = new()
    {
        Id = "systeminfo",
        DisplayName = "System Info",
        Version = "0.1.0",
        RoutePath = "/system",
        Icon = Icons.Material.Filled.Monitor,
        NavOrder = 10,
        Description = "Live CPU, memory, disk and network metrics for this machine.",
    };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IModuleContext context)
    {
        var options = context.Configuration.Get<SystemInfoOptions>() ?? new SystemInfoOptions();

        services.AddSingleton(options);

        // ProcReader and MetricsHistory are stateful — they hold the previous counter reading
        // and the history ring — so both must be singletons. Scoping either per-request would
        // reset the deltas on every render and report nonsense.
        services.AddSingleton<ProcReader>();
        services.AddSingleton<MetricsHistory>();
        services.AddSingleton<PowerControl>();

        services.AddModuleWorker<SystemSampler>();
    }
}
