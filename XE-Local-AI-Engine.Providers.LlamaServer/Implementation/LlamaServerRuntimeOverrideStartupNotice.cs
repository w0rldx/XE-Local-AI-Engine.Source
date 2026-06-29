namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;

/// <summary>
///     Startup <see cref="IHostedService" /> that emits a single prominent <see cref="LogLevel.Warning" /> when the
///     operator bring-your-own llama-server override is active. The path is operator-owned and therefore safe to log (no
///     secret-hygiene violation). Nothing is logged when the override is unset, so a normal (pinned-acquisition) deploy is
///     byte-behavior-unchanged.
/// </summary>
internal sealed class LlamaServerRuntimeOverrideStartupNotice : IHostedService
{
    private readonly ILogger<LlamaServerRuntimeOverrideStartupNotice> _logger;
    private readonly LlamaServerRuntimeOverrideOptions _options;

    public LlamaServerRuntimeOverrideStartupNotice(LlamaServerRuntimeOverrideOptions options,
        ILogger<LlamaServerRuntimeOverrideStartupNotice> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.IsActive)
        {
            _logger.LogWarning(
                "Using operator-supplied llama-server at {ServerPath} (variant {Variant}); integrity hash verification is skipped.",
                _options.ServerPath,
                _options.Variant);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
