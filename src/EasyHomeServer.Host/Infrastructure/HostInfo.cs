using System.Reflection;
using EasyHomeServer.Sdk;

namespace EasyHomeServer.Host.Infrastructure;

/// <summary>Identity of this host, shown in the top bar. Fixed for the process lifetime.</summary>
internal sealed class HostInfo
{
    /// <summary>Machine hostname.</summary>
    public string HostName { get; } = Environment.MachineName;

    /// <summary>Version of the host application.</summary>
    public string Version { get; } =
        typeof(HostInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { } informational
            ? informational.Split('+')[0]
            : "0.0.0";

    /// <summary>Major SDK contract version this host provides to modules.</summary>
    public int SdkContractVersion { get; } = typeof(IModule).Assembly.GetName().Version?.Major ?? 0;
}
