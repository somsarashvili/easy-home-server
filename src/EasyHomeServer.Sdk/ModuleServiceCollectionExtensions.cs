using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace EasyHomeServer.Sdk;

/// <summary>Registration helpers for module authors, used from <see cref="IModule.ConfigureServices"/>.</summary>
public static class ModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="ModuleBackgroundService"/> as a singleton and as a hosted
    /// service, so the worker instance can also be injected into the module's components
    /// (for example to read the latest sample without waiting for the next event).
    /// </summary>
    public static IServiceCollection AddModuleWorker<TWorker>(this IServiceCollection services)
        where TWorker : ModuleBackgroundService
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TWorker>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TWorker>());

        return services;
    }
}
