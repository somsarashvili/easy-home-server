using System.Collections.Immutable;
using System.Text;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Avahi.Avahi;

/// <summary>
/// Manages this module's block of static host records in <c>/etc/avahi/hosts</c>.
/// </summary>
/// <remarks>
/// <para>
/// avahi publishes an A record for every entry here, which is the only way to advertise a name
/// pointing at an address that is not the host's own. That is exactly what a macvlan container
/// needs: it holds its own address on the LAN, so a service advertised against the host's name
/// would send browsers to a machine that cannot even reach it.
/// </para>
/// <para>
/// Unlike the services directory, this is a single file shared with whatever the admin has put in
/// it, so entries cannot be owned by filename. They are owned by a marked block instead:
/// everything between the markers belongs to this module, everything outside is copied through
/// untouched.
/// </para>
/// <para>
/// And unlike service files, avahi does <em>not</em> watch this file — it is read at startup and
/// on reload. Writing it is therefore only half the job; see
/// <see cref="AvahiServiceStore.ReloadAsync"/>.
/// </para>
/// </remarks>
public sealed class AvahiHostsFile(ISystemRunner systemRunner, AvahiOptions options, ILogger<AvahiHostsFile> logger)
{
    private const string BeginMarker = "# BEGIN EasyHomeServer — managed automatically, do not edit";
    private const string EndMarker = "# END EasyHomeServer";

    /// <summary>A name to advertise at a given address.</summary>
    public sealed record HostEntry
    {
        /// <summary>IPv4 address the name resolves to.</summary>
        public required string Address { get; init; }

        /// <summary>Fully-qualified name, for example <c>jellyfin.local</c>.</summary>
        public required string HostName { get; init; }
    }

    /// <summary>
    /// Rewrites this module's block to match <paramref name="desired"/>, preserving everything
    /// outside it. Returns true when the file changed.
    /// </summary>
    /// <remarks>
    /// The return value matters: the caller reloads avahi only when it is true. A reload makes
    /// avahi withdraw and re-announce everything it publishes, so reloading on every poll would
    /// leave every service on the network flickering a few times a minute.
    /// </remarks>
    public async Task<bool> WriteAsync(
        IReadOnlyCollection<HostEntry> desired,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desired);

        string existing;

        try
        {
            existing = File.Exists(options.HostsPath)
                ? await File.ReadAllTextAsync(options.HostsPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not read {HostsPath}; leaving it alone.", options.HostsPath);

            return false;
        }

        var updated = Compose(existing, desired);

        if (updated == existing)
        {
            return false;
        }

        try
        {
            await systemRunner.WriteFileAsync(options.HostsPath, updated, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Wrote {Count} static host record(s) to {HostsPath}.",
                desired.Count,
                options.HostsPath);

            return true;
        }
        catch (SystemOperationException ex)
        {
            logger.LogError(ex, "Could not write {HostsPath}.", options.HostsPath);

            return false;
        }
    }

    /// <summary>
    /// Produces the new file: foreign content unchanged, this module's block replaced.
    /// </summary>
    internal static string Compose(string existing, IReadOnlyCollection<HostEntry> desired)
    {
        var foreign = StripManagedBlock(existing);
        var builder = new StringBuilder();

        builder.Append(foreign.TrimEnd('\n', '\r'));

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        // No entries means no block at all, rather than an empty one: the file should look
        // untouched when this module has nothing to say.
        if (desired.Count == 0)
        {
            return builder.Length == 0 ? string.Empty : builder.ToString();
        }

        builder.Append('\n').Append(BeginMarker).Append('\n');

        foreach (var entry in desired.OrderBy(e => e.HostName, StringComparer.Ordinal))
        {
            builder.Append(entry.Address).Append(' ').Append(entry.HostName).Append('\n');
        }

        builder.Append(EndMarker).Append('\n');

        return builder.ToString();
    }

    /// <summary>
    /// Removes this module's block, keeping everything else.
    /// </summary>
    /// <remarks>
    /// A file with a begin marker and no end marker — a half-written file from a crash, or a
    /// hand-edit — would otherwise swallow the rest of the file. Treated as "everything from the
    /// marker on is ours", which is the safe reading: this module rewrites its own block anyway,
    /// and losing an admin line below it is the one outcome to avoid.
    /// </remarks>
    private static string StripManagedBlock(string content)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        var lines = content.Split('\n');
        var kept = new List<string>(lines.Length);
        var inBlock = false;
        var sawEnd = false;
        var afterBlock = new List<string>();

        foreach (var line in lines)
        {
            if (!inBlock && line.StartsWith(BeginMarker, StringComparison.Ordinal))
            {
                inBlock = true;

                continue;
            }

            if (inBlock)
            {
                if (line.StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    inBlock = false;
                    sawEnd = true;

                    continue;
                }

                // Inside the block: ours, so dropped. Anything after a *missing* end marker is
                // collected separately rather than discarded.
                afterBlock.Add(line);

                continue;
            }

            kept.Add(line);
        }

        // Unterminated block: keep any non-record lines that followed it rather than eat them.
        if (!sawEnd && afterBlock.Count > 0)
        {
            kept.AddRange(afterBlock.Where(l => l.TrimStart().StartsWith('#') || l.Trim().Length == 0));
        }

        return string.Join('\n', kept);
    }

    /// <summary>Reads this module's current entries, for display.</summary>
    public ImmutableArray<HostEntry> Read()
    {
        try
        {
            if (!File.Exists(options.HostsPath))
            {
                return [];
            }

            var entries = ImmutableArray.CreateBuilder<HostEntry>();
            var inBlock = false;

            foreach (var line in File.ReadLines(options.HostsPath))
            {
                if (line.StartsWith(BeginMarker, StringComparison.Ordinal))
                {
                    inBlock = true;

                    continue;
                }

                if (line.StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    break;
                }

                if (!inBlock || line.TrimStart().StartsWith('#') || line.Trim().Length == 0)
                {
                    continue;
                }

                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (fields.Length >= 2)
                {
                    entries.Add(new HostEntry { Address = fields[0], HostName = fields[1] });
                }
            }

            return entries.ToImmutable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read {HostsPath}.", options.HostsPath);

            return [];
        }
    }
}
