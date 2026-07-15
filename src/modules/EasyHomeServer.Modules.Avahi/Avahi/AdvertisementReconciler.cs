using System.Collections.Immutable;
using EasyHomeServer.Contracts.Docker;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Avahi.Avahi;

/// <summary>
/// Keeps this machine's mDNS advertisements in step with what it is running.
/// </summary>
/// <remarks>
/// <para>
/// This is the module that proves the plugin model's central claim. It reacts to Docker
/// containers without referencing the Docker module, without knowing whether one is installed,
/// and without either module knowing the other exists. All they share is
/// <see cref="EasyHomeServer.Contracts.Docker"/> — a types-only assembly the host loads once into
/// its default context so both see the same type. Remove the Docker module and this keeps
/// running, advertising the host and nothing else.
/// </para>
/// <para>
/// One reconciler owns every advertisement — the host's own and every container's. That is not
/// incidental: reconciling means "delete what is not wanted", so two reconcilers sharing a
/// directory would each treat the other's files as unwanted and delete them, every few seconds,
/// forever.
/// </para>
/// <para>
/// It subscribes to <see cref="ContainerInventory"/> — full state — rather than accumulating
/// <see cref="ContainerChanged"/> deltas, and treats each one as a reconcile. That is
/// self-correcting: no catch-up needed at startup, a missed event costs nothing, and a hand-edited
/// service file is put back within seconds.
/// </para>
/// </remarks>
public sealed class AdvertisementReconciler : ModuleBackgroundService
{
    /// <summary>Key of the host's own advertisement. Not a legal container name, so it cannot collide with one.</summary>
    public const string SelfKey = "self";

    private readonly IEventBus _eventBus;
    private readonly AvahiServiceStore _store;
    private readonly AvahiOptions _options;

    // Inventories arrive every few seconds; two overlapping passes over the same directory would
    // race each other's writes and deletes.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The most recent mapping of containers to advertisements, for the page.</summary>
    public ImmutableArray<ContainerMapping> Mappings { get; private set; } = [];

    /// <summary>When the last container inventory arrived, or null if none ever has.</summary>
    public DateTimeOffset? LastInventoryAtUtc { get; private set; }

    /// <summary>
    /// True once a container inventory has been seen. False means nothing is publishing
    /// containers — a normal, supported configuration, not a fault.
    /// </summary>
    public bool HasDockerSource => LastInventoryAtUtc is not null;

    /// <summary>Everything currently advertised, host and containers alike.</summary>
    public ImmutableArray<ServiceDefinition> Advertised { get; private set; } = [];

    public AdvertisementReconciler(
        IEventBus eventBus,
        AvahiServiceStore store,
        AvahiOptions options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        _eventBus = eventBus;
        _store = store;
        _options = options;
    }

    /// <summary>The host's own advertisement, or null when self-advertising is off.</summary>
    private ServiceDefinition? SelfDefinition => _options.AdvertiseSelf
        ? new ServiceDefinition
        {
            Key = SelfKey,
            DisplayName = "EasyHomeServer on %h",
            ServiceType = "_http._tcp",
            Port = _options.SelfPort,
            TxtRecords = ImmutableDictionary<string, string>.Empty.Add("path", "/"),
            Origin = ServiceOrigin.Host,
        }
        : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reconcile once up front. Without this, a machine with no Docker module would never
        // advertise itself: no inventory would ever arrive to trigger a pass. It also withdraws
        // anything a previous configuration left behind.
        await ReconcileAsync(stoppingToken).ConfigureAwait(false);

        if (!_options.AdvertiseContainers)
        {
            Logger.LogInformation("Container advertising is disabled.");

            return;
        }

        using var subscription = _eventBus.Subscribe<ContainerInventory>(OnInventoryAsync);

        Logger.LogInformation(
            "Watching Docker containers for the {EnableLabel} label.",
            ContainerServiceMapper.EnableLabel);

        try
        {
            // The work is event-driven; hold the subscription open until shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown; the using disposes the subscription.
        }
    }

    private async Task OnInventoryAsync(ContainerInventory inventory, CancellationToken cancellationToken)
    {
        var wasFirst = LastInventoryAtUtc is null;

        LastInventoryAtUtc = inventory.ObservedAtUtc;
        Mappings = ContainerServiceMapper.Map(inventory);

        if (wasFirst)
        {
            Logger.LogInformation(
                "Receiving container inventories from a Docker module ({Count} container(s)).",
                inventory.Containers.Length);
        }

        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var desired = ImmutableArray.CreateBuilder<ServiceDefinition>();

            if (SelfDefinition is { } self)
            {
                desired.Add(self);
            }

            if (_options.AdvertiseContainers)
            {
                desired.AddRange(Mappings.Select(m => m.Service).Where(s => s is not null).Select(s => s!));
            }

            Advertised = desired.ToImmutable();

            var result = await _store.ReconcileAsync(Advertised, cancellationToken).ConfigureAwait(false);

            // Logged only when something changed: this runs every few seconds, and a line per
            // pass would bury the journal in "nothing happened".
            if (!result.NoChanges)
            {
                Logger.LogInformation(
                    "Advertisements reconciled: {Written} written, {Removed} withdrawn, {Unchanged} unchanged.",
                    result.Written,
                    result.Removed,
                    result.Unchanged);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }
}
