namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.Net;
using Microsoft.Extensions.Options;

/// <summary>
///     Holds the set of addresses that mean "this computer", so the bridge's peer guard can answer in constant time
///     what would otherwise be an interface enumeration per request.
///     <para>
///         It refreshes on a timer rather than caching once, because the answer genuinely changes while the node
///         runs: a laptop moves between networks, a VPN comes up, and — the case that matters here — a container
///         daemon creates and destroys bridge interfaces as applications start and stop. A set frozen at boot would
///         refuse a container that came up on an interface created after it.
///     </para>
///     <para>
///         An <see cref="IHostedService" /> with its own loop driven by an injected <see cref="TimeProvider" />,
///         mirroring <c>ExternalAppStateObserver</c>, so a test advances the clock instead of sleeping.
///     </para>
/// </summary>
public sealed class ContainerBridgeAddressWatcher : IHostedService, IDisposable
{
    /// <summary>How often the host's own addresses are re-read. Not operator-configurable: nothing about a local interface enumeration needs tuning.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly Func<IReadOnlyList<IPAddress>> _addressSource;
    private readonly ILogger<ContainerBridgeAddressWatcher> _logger;
    private readonly ContainerBridgeOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeProvider _timeProvider;

    private volatile HashSet<IPAddress> _addresses = [];
    private Task? _loop;

    public ContainerBridgeAddressWatcher(IOptions<ContainerBridgeOptions> options,
        TimeProvider timeProvider,
        ILogger<ContainerBridgeAddressWatcher> logger,
        Func<IReadOnlyList<IPAddress>>? addressSource = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _addressSource = addressSource ?? DefaultAddressSource;
    }

    public void Dispose()
    {
        _stopping.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            // The registration stays so the composition root has one shape whether or not the bridge is on; the
            // behaviour is what the flag gates.
            //
            // The flag is this type's only gate, so a node with the bridge on and External Apps off still runs the
            // loop although no listener was opened. That is one interface enumeration a minute and is left alone
            // deliberately: the second flag lives in the composition root, and reaching it from here would make the
            // containers layer read a feature's configuration to decide its own behaviour.
            return Task.CompletedTask;
        }

        // Synchronously, before the first tick: Kestrel's own hosted service starts ahead of this one, so the
        // listener can already be accepting when this runs and an empty set would refuse a legitimate container.
        Refresh();

        // CancellationToken.None on purpose: the loop's lifetime is the host's, not this start call's.
        _loop = Task.Run(() => RefreshLoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();

        if (_loop is not { } loop)
        {
            return;
        }

        try
        {
            await loop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Either the loop's own cancellation or a shutdown deadline that ran out. The loop reads interfaces and
            // holds nothing, so neither is worth failing shutdown over.
        }
    }

    /// <summary>
    ///     Whether <paramref name="remoteAddress" /> is one of this computer's own addresses. A null peer is refused:
    ///     the bridge is only ever reached over a real socket, so "no peer" is not a peer this guard can vouch for.
    /// </summary>
    public bool IsSameHost(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        if (_addresses.Count == 0)
        {
            // The listener beat the hosted service to the first connection, or the bridge is running without one.
            Refresh();
        }

        return _addresses.Contains(Normalize(remoteAddress));
    }

    /// <summary>Re-reads the host's own addresses. <c>internal</c> so a test drives one refresh directly.</summary>
    internal void Refresh()
    {
        // Both loopbacks unconditionally: a connection the engine makes to its own bridge, and a rootful daemon's
        // host-gateway path, both present as loopback and neither is reported as an interface address everywhere.
        var snapshot = new HashSet<IPAddress>
        {
            IPAddress.Loopback,
            IPAddress.IPv6Loopback
        };

        foreach (var address in _addressSource())
        {
            snapshot.Add(Normalize(address));
        }

        _addresses = snapshot;
    }

    private static IReadOnlyList<IPAddress> DefaultAddressSource()
    {
        return ContainerBridgeEndpointResolver.CollectUnicastAddresses(ContainerBridgeEndpointResolver.SnapshotInterfaces());
    }

    // A dual-stack listener reports an IPv4 peer as ::ffff:a.b.c.d, which no interface enumeration ever returns.
    // Folding both sides to IPv4 is what keeps the comparison from silently refusing every container.
    private static IPAddress Normalize(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval, _timeProvider);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                Refresh();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A tick that failed must not end the watcher; the last good set stands and the next tick asks again.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _logger.LogWarning(exception, "Re-reading this computer's own network addresses failed; the container bridge keeps the previous set.");
            }
        }
    }
}
