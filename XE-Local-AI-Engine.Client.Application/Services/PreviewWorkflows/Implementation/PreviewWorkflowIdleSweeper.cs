namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;

using Microsoft.Extensions.Options;

/// <summary>
///     Periodically sweeps the in-memory preview run registry, cancelling runs that have been idle past the TTL or have
///     exceeded the hard wall-clock cap. A Paused run is exempt (its idle clock is suspended and it is excluded from the
///     wall-clock cap) so a run waiting on a human Continue is never swept (findings item 6). The sweep itself lives on
///     the singleton execution service; this hosted service only drives the cadence.
/// </summary>
public sealed class PreviewWorkflowIdleSweeper : BackgroundService
{
    private readonly PreviewWorkflowExecutionService _executionService;
    private readonly ILogger<PreviewWorkflowIdleSweeper> _logger;
    private readonly PreviewWorkflowExecutionOptions _options;
    private readonly TimeProvider _timeProvider;

    // The execution service is registered against its interface and (separately) as the concrete type for this sweeper
    // and the hub-disconnect path; both resolve the SAME singleton instance.
    internal PreviewWorkflowIdleSweeper(PreviewWorkflowExecutionService executionService,
        IOptions<PreviewWorkflowExecutionOptions> options,
        TimeProvider timeProvider,
        ILogger<PreviewWorkflowIdleSweeper> logger)
    {
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.SweepInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : _options.SweepInterval;
        using var timer = new PeriodicTimer(interval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                await _executionService.SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Preview workflow idle sweep iteration failed; continuing.");
            }
        }
    }
}
