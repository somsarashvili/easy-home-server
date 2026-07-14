using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Sdk;

/// <summary>
/// Base class for module background work: periodic sampling, event listeners, watchers.
/// Register with <see cref="ModuleServiceCollectionExtensions.AddModuleWorker{TWorker}"/>.
/// </summary>
/// <remarks>
/// This exists instead of plain <see cref="BackgroundService"/> for one important reason:
/// since .NET 6 an unhandled exception in a <see cref="BackgroundService"/> stops the whole
/// host by default. A misbehaving module must never take the server down, so this class
/// catches and logs whatever escapes <see cref="ExecuteAsync"/> and lets the rest of the
/// host carry on. Overriding <see cref="StartAsync"/>/<see cref="StopAsync"/> is not the
/// intended extension point.
/// </remarks>
public abstract class ModuleBackgroundService : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task? _executeTask;
    private bool _disposed;

    /// <summary>Logger for this worker, named after the concrete worker type.</summary>
    protected ILogger Logger { get; }

    /// <summary>Creates the worker.</summary>
    protected ModuleBackgroundService(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        Logger = loggerFactory.CreateLogger(GetType());
    }

    /// <summary>
    /// The worker body. Runs until <paramref name="stoppingToken"/> is signalled at host
    /// shutdown. Long loops must observe the token; work that ignores it will delay shutdown
    /// until the host's stop timeout elapses.
    /// </summary>
    protected abstract Task ExecuteAsync(CancellationToken stoppingToken);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _executeTask = RunGuardedAsync(_stoppingCts.Token);

        // A worker that completes synchronously (or fails immediately) surfaces here;
        // otherwise let startup continue while it runs.
        if (_executeTask.IsCompleted)
        {
            return _executeTask;
        }

        return Task.CompletedTask;
    }

    private async Task RunGuardedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Background worker {Worker} faulted and has stopped. The host continues running; "
                    + "restart the service to retry.",
                GetType().Name);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executeTask is null)
        {
            return;
        }

        try
        {
            await _stoppingCts.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            var cancelledTask = Task.Delay(Timeout.Infinite, cancellationToken);
            await Task.WhenAny(_executeTask, cancelledTask).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases resources held by the worker.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _stoppingCts.Cancel();
            _stoppingCts.Dispose();
        }

        _disposed = true;
    }
}
