namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;

internal sealed partial class NodePatchApplyService
{
    private async Task LogAppliedAsync(string runId, IReadOnlyList<PatchApplyFileEntry> files, CancellationToken cancellationToken)
    {
        var detail = string.Join(';', files.Select(file => string.Create(CultureInfo.InvariantCulture, $"{file.Alias}/{file.RelativePath}")));
        await AppendEventSafelyAsync(runId, "patch_applied", detail, cancellationToken).ConfigureAwait(false);
    }

    private async Task LogRejectionAsync(string runId, IReadOnlyList<string> rejections, CancellationToken cancellationToken)
    {
        await AppendEventSafelyAsync(runId, "patch_apply_rejected", string.Join(';', rejections), cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendEventSafelyAsync(string runId, string eventName, string? detail, CancellationToken cancellationToken)
    {
        // observability guard: Best-effort logging — broadened to catch ANY exception from identity/logger so a failed log can
        // never surface after a successful host mutation. OperationCanceledException from the caller's token is NOT
        // caught here; it will propagate only from the caller's own await, not from this helper.
        try
        {
            var logDirectory = Path.Combine(ResolveAgentHomeRoot(), RunsDirectoryName, runId, "logs");
            if (!Directory.Exists(logDirectory))
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var runLogger = scope.ServiceProvider.GetRequiredService<IAgentHomeRunLogger>();
            var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            await runLogger.OpenAsync(new AgentHomeRunLogContext
                {
                    RunId = runId,
                    HostLogDirectory = logDirectory,
                    NodeId = identity.NodeId,
                    OwnerUserId = identity.OwnerUserId,
                    ProviderName = ProviderName
                },
                cancellationToken).ConfigureAwait(false);
            await runLogger.AppendEventAsync(eventName, detail, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller-cancel: do not swallow — propagate so the caller knows the operation was cancelled.
            throw;
        }
        catch (Exception exception)
        {
            // Any other failure (identity error, I/O, DI, logger) is swallowed. A log write must never throw past
            // a successful host mutation because run-log writes are best-effort.
            _logger.LogDebug(exception, "AgentHome patch apply log append for {EventName} failed.", eventName);
        }
    }
}
