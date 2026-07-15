namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// The labels the Avahi module reads, mirrored here so the compose builder can offer them as
/// fields.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberate, and deliberately small, duplication. The Avahi module owns these names;
/// this module cannot reference it — separate plugins, separate load contexts — and giving them a
/// shared contracts assembly would mean a third package for four strings and a naming rule.
/// </para>
/// <para>
/// What makes that acceptable is the failure mode. These are labels: unknown ones are ignored by
/// Docker and by Avahi alike. If the two ever drift, the effect is a builder checkbox that
/// silently does nothing — visible the moment anyone tries it, and fixed by editing a string. It
/// is not a type contract, so it cannot fail at load time or corrupt anything.
/// </para>
/// <para>
/// The Avahi module works perfectly well without this: the labels can be typed by hand on the
/// YAML tab, or set with <c>docker run --label</c>. This only saves the operator from having to
/// know them.
/// </para>
/// </remarks>
internal static class AvahiLabels
{
    /// <summary>Set to <c>true</c> to advertise the container.</summary>
    public const string Enable = "easyhomeserver.avahi.enable";

    /// <summary>The name shown when browsing the network.</summary>
    public const string Name = "easyhomeserver.avahi.name";

    /// <summary>The <c>.local</c> hostname, for a container with its own LAN address.</summary>
    public const string HostName = "easyhomeserver.avahi.hostname";

    /// <summary>Which port to advertise.</summary>
    public const string Port = "easyhomeserver.avahi.port";

    /// <summary>Path published as a TXT record.</summary>
    public const string Path = "easyhomeserver.avahi.path";

    /// <summary>
    /// Checks a hostname against the DNS label rules, returning why it is unusable or null when
    /// it is fine.
    /// </summary>
    /// <remarks>
    /// Mirrors the Avahi module's own check so the form can reject a bad name while it is being
    /// typed. Avahi validates again at the point of use and is the authority — this is a
    /// courtesy, not a gate.
    /// </remarks>
    public static string? ValidateHostName(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return null;
        }

        if (hostName.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return "Leave off the .local — it is added automatically.";
        }

        if (hostName.Contains('.', StringComparison.Ordinal))
        {
            return "No dots: an mDNS name is a single label under .local.";
        }

        if (hostName.Length > 63)
        {
            return "Too long — 63 characters at most.";
        }

        if (!hostName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return "Use only letters, digits and hyphens.";
        }

        if (hostName.StartsWith('-') || hostName.EndsWith('-'))
        {
            return "Cannot start or end with a hyphen.";
        }

        return null;
    }

    /// <summary>
    /// The container name Compose will generate for a service, which is what the hostname
    /// defaults to.
    /// </summary>
    /// <remarks>
    /// Compose names containers <c>project-service-N</c>. Predicted here so the form can show
    /// what will happen if the field is left empty — the whole reason the field exists is that
    /// <c>mynginx-web-1.local</c> is a surprise.
    /// </remarks>
    public static string PredictContainerName(string projectName, string serviceName) =>
        $"{projectName}-{serviceName}-1";
}
