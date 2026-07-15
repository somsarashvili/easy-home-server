namespace EasyHomeServer.Modules.Avahi;

/// <summary>Module settings, bound from the host's <c>Modules:avahi</c> configuration section.</summary>
public sealed class AvahiOptions
{
    /// <summary>
    /// Directory avahi reads service files from. It watches this directory and republishes on
    /// change, so writing a file here is the whole publishing mechanism.
    /// </summary>
    public string ServicesPath { get; set; } = "/etc/avahi/services";

    /// <summary>
    /// File avahi reads static host records from. Unlike the services directory, avahi does not
    /// watch this — it is read at startup and on reload.
    /// </summary>
    public string HostsPath { get; set; } = "/etc/avahi/hosts";

    /// <summary>
    /// Whether to advertise Docker containers that opt in with the
    /// <c>easyhomeserver.avahi.enable</c> label. Turning this off withdraws everything this
    /// module published.
    /// </summary>
    public bool AdvertiseContainers { get; set; } = true;

    /// <summary>
    /// Whether to advertise the management tool itself, so the server is findable at
    /// <c>&lt;hostname&gt;.local</c> without knowing its port.
    /// </summary>
    public bool AdvertiseSelf { get; set; } = true;

    /// <summary>Port the management tool listens on, used by <see cref="AdvertiseSelf"/>.</summary>
    public int SelfPort { get; set; } = 5000;
}
