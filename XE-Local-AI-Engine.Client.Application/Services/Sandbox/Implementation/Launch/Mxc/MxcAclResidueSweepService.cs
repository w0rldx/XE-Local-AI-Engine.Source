namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;

/// <summary>
///     Windows startup sweep of AppContainer ACEs a crashed MXC run left behind: every engine-owned path the grant journal recorded, plus
///     the compute venv directory, top-level only (<see cref="MxcAclResidueSweeper" />). Completes inside <see cref="StartAsync" /> and
///     never throws.
/// </summary>
/// <remarks>
///     It must finish before this node's first MXC launch, or it strips that launch's live grant: <c>AddNodeApplication</c> registers it
///     as the FIRST hosted service, and hosted services start sequentially in registration order, before Kestrel serves a request. A
///     second engine process on the box can still strip a live grant; that command fails closed. One node per data directory is supported.
/// </remarks>
public sealed class MxcAclResidueSweepService : IHostedService
{
    private readonly ILogger<MxcAclResidueSweepService> _logger;
    private readonly Action _sweep;

    [SupportedOSPlatform("windows")]
    public MxcAclResidueSweepService(ILogger<MxcAclResidueSweepService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sweep = Sweep;
    }

    /// <summary>Test seam: runs <paramref name="sweep" /> in place of the Windows ACL walk.</summary>
    internal MxcAclResidueSweepService(ILogger<MxcAclResidueSweepService> logger, Action sweep)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sweep = sweep ?? throw new ArgumentNullException(nameof(sweep));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Awaited, not fire-and-forget: top-level only, so the walk is cheap, and a launch overlapping it would lose its grant.
        try
        {
            await Task.Run(_sweep, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The MXC ACL residue sweep failed; continuing.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [SupportedOSPlatform("windows")]
    private void Sweep()
    {
        var roots = new List<string>(MxcGrantJournal.Drain(MxcGrantJournal.DefaultPath, MxcGrantJournal.EngineOwnedRoots, _logger))
        {
            // The venv's parent, so VenvRoot itself is a direct child: covers a grant the journal could not record.
            Path.Combine(ComputeRuntimeDirectory.DefaultCacheRoot(), "venv")
        };
        _ = MxcAclResidueSweeper.Sweep(roots, _logger);
    }
}
