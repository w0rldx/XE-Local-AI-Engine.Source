namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;

internal sealed partial class NodePatchApplyService
{
    private async Task LogAppliedAsync(string runId, IReadOnlyList<PatchApplyFileEntry> files, CancellationToken cancellationToken)
    {
        var detail = string.Join(separator: ';', files.Select(file => Describe(file.Alias, file.RelativePath)));
        PatchApplied(_logger, runId, files.Count);
        await AppendEventSafelyAsync(runId, "patch_applied", detail, cancellationToken);
    }

    private async Task LogRejectionAsync(string runId, IReadOnlyList<PatchApplyRejection> rejections, CancellationToken cancellationToken)
    {
        // The entry name joins the reason folder-relative, the same form the applied-files line already writes.
        var detail = string.Join(separator: ';',
            rejections.Select(rejection => rejection.Path is null
                ? rejection.Reason
                : string.Create(CultureInfo.InvariantCulture, $"{rejection.Path}: {rejection.Reason}")));
        PatchApplyRejected(_logger, runId, rejections.Count);
        await AppendEventSafelyAsync(runId, "patch_apply_rejected", detail, cancellationToken);
    }

    // Outcome only, so an apply is visible without opening the run directory: run id and a count, never a path.
    // The run's own log holds the per-file detail and the rejection strings.
    [LoggerMessage(EventId = 4801, Level = LogLevel.Information, Message = "AgentHome patch applied for run {RunId}: {FileCount} file(s) written to the host.")]
    private static partial void PatchApplied(ILogger logger, string runId, int fileCount);

    // No "the host was not modified" claim: the partially-applied path reports through here too, and the result's
    // own PartiallyApplied flag is what says which of the two happened.
    [LoggerMessage(EventId = 4802, Level = LogLevel.Information, Message = "AgentHome patch apply rejected for run {RunId}: {RejectionCount} rejection(s).")]
    private static partial void PatchApplyRejected(ILogger logger, string runId, int rejectionCount);

    private async Task AppendEventSafelyAsync(string runId, string eventName, string? detail, CancellationToken cancellationToken)
    {
        // Observability guard: best-effort logging catches ANY exception from identity or logger, so a failed log can never
        // surface after a successful host mutation. A caller-token cancellation propagates from the caller's own await.
        try
        {
            var logDirectory = Path.Combine(ResolveRunDirectory(runId), "logs");
            if (!Directory.Exists(logDirectory))
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var runLogger = scope.ServiceProvider.GetRequiredService<IAgentHomeRunLogger>();
            var identity = await _identityProvider.GetAsync(cancellationToken);
            await runLogger.OpenAsync(new AgentHomeRunLogContext
                {
                    RunId = runId,
                    HostLogDirectory = logDirectory,
                    NodeId = identity.NodeId,
                    OwnerUserId = identity.OwnerUserId,
                    ProviderName = ProviderName
                },
                cancellationToken);
            await runLogger.AppendEventAsync(eventName, detail, cancellationToken: cancellationToken);
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
