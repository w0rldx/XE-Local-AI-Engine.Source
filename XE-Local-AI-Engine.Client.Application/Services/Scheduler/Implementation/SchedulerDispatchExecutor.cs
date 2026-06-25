namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Default <see cref="ISchedulerDispatchExecutor" />. Scoped so the stores (and their DbContext) are resolved per
///     fire. Guards every fire — missing / disabled / soft-deleted definition, or a template with no registered handler —
///     by logging a sanitized skip and returning <em>without</em> writing a run row; raw parameters are never logged.
///     <para>
///         <b>Run history.</b> Once a fire passes the guards it is recorded: an idempotent
///         <see cref="IScheduledJobRunStore.UpsertByFireInstanceAsync" /> (keyed on the Quartz fire-instance id) opens a
///         <see cref="ScheduledRunStatus.Running" /> row, the handler runs with a live progress callback that appends
///         <see cref="ScheduledRunEventLevel.Progress" /> events, and a terminal lifecycle update records the outcome:
///         <see cref="ScheduledRunStatus.Succeeded" />, <see cref="ScheduledRunStatus.Failed" /> (sanitized — no message
///         text or stack trace leaves the process), <see cref="ScheduledRunStatus.Cancelled" /> (operator cancel) or
///         <see cref="ScheduledRunStatus.TimedOut" /> (auto-interrupt). Cancellation is re-thrown so Quartz still
///         observes the interrupt / shutdown; ordinary failures are swallowed (the run row is the record of failure) so a
///         single faulting handler cannot fault the scheduler. Terminal writes use <see cref="CancellationToken.None" />
///         because the run is ending precisely <em>because</em> its own token was cancelled.
///     </para>
///     <para>
///         <b>Realtime events.</b> Each lifecycle transition (started / progress / completed / failed / cancelled)
///         is published through <see cref="ISchedulerEventPublisher" /> as a sanitized DTO. Publishing is best-effort:
///         failures are logged and swallowed so a broken notification never corrupts run handling or masks a cancellation.
///     </para>
/// </summary>
internal sealed class SchedulerDispatchExecutor(
    IScheduledJobDefinitionStore definitionStore,
    IScheduledJobTemplateRegistry templateRegistry,
    IScheduledJobRunStore runStore,
    IScheduledJobRunEventStore runEventStore,
    ISchedulerEventPublisher eventPublisher,
    TimeProvider timeProvider,
    ILogger<SchedulerDispatchExecutor> logger) : ISchedulerDispatchExecutor
{
    /// <summary>
    ///     The whitelisted parameter keys a per-fire override may replace. Compared case-insensitively against the stored
    ///     JSON's property names; the stored key's original casing is preserved when it already exists.
    /// </summary>
    private const string OverridableUseCaseProperty = "useCase";

    private const string OverridableLimitProperty = "limit";

    private const string OverridableQuantProperty = "quantOverride";

    private const string OverridableCtxTargetProperty = "ctxTarget";

    private readonly IScheduledJobDefinitionStore _definitionStore =
        definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));

    private readonly ISchedulerEventPublisher _eventPublisher =
        eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));

    private readonly ILogger<SchedulerDispatchExecutor> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IScheduledJobRunEventStore _runEventStore =
        runEventStore ?? throw new ArgumentNullException(nameof(runEventStore));

    private readonly IScheduledJobRunStore _runStore =
        runStore ?? throw new ArgumentNullException(nameof(runStore));

    private readonly IScheduledJobTemplateRegistry _templateRegistry =
        templateRegistry ?? throw new ArgumentNullException(nameof(templateRegistry));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task DispatchAsync(Guid scheduledJobId,
        string fireInstanceId,
        DateTimeOffset? scheduledFireTimeUtc,
        DateTimeOffset actualFireTimeUtc,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? parameterOverrides = null)
    {
        var definition = await _definitionStore.GetByIdAsync(scheduledJobId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            _logger.LogWarning("Scheduled job dispatch skipped: no definition found for id {ScheduledJobId} (fire {FireInstanceId}).",
                scheduledJobId,
                fireInstanceId);
            return;
        }

        if (!definition.Enabled || definition.DeletedAtUtc is not null)
        {
            _logger.LogInformation("Scheduled job dispatch skipped: definition {ScheduledJobId} is not dispatchable (enabled={Enabled}, deleted={Deleted}, fire {FireInstanceId}).",
                scheduledJobId,
                definition.Enabled,
                definition.DeletedAtUtc is not null,
                fireInstanceId);
            return;
        }

        if (!_templateRegistry.TryGetHandler(definition.TemplateId, out var handler))
        {
            _logger.LogWarning("Scheduled job dispatch skipped: no handler registered for template {TemplateId} (definition {ScheduledJobId}, fire {FireInstanceId}).",
                definition.TemplateId,
                scheduledJobId,
                fireInstanceId);
            return;
        }

        await RecordAndRunAsync(definition, handler, fireInstanceId, scheduledFireTimeUtc, actualFireTimeUtc, parameterOverrides, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RecordAndRunAsync(ScheduledJobDefinitionRecord definition,
        IScheduledJobHandler handler,
        string fireInstanceId,
        DateTimeOffset? scheduledFireTimeUtc,
        DateTimeOffset actualFireTimeUtc,
        IReadOnlyDictionary<string, string>? parameterOverrides,
        CancellationToken cancellationToken)
    {
        var actualFireMs = actualFireTimeUtc.ToUnixTimeMilliseconds();

        // Idempotent open: a refire / recovery callback with the same fire-instance id returns the existing row instead
        // of inserting a duplicate. If that row is already terminal the work has run before — skip re-execution.
        var run = await _runStore.UpsertByFireInstanceAsync(new ScheduledJobRunInput(definition.Id,
                definition.TemplateId,
                fireInstanceId,
                ScheduledRunTrigger.Schedule,
                ScheduledRunStatus.Running,
                scheduledFireTimeUtc?.ToUnixTimeMilliseconds(),
                actualFireMs),
            cancellationToken).ConfigureAwait(false);

        if (IsTerminal(run.Status))
        {
            _logger.LogInformation("Scheduled job dispatch skipped: run {RunId} for fire {FireInstanceId} is already {Status} (idempotent re-fire).",
                run.Id,
                fireInstanceId,
                run.Status);
            return;
        }

        await SafePublishRunAsync(run, SchedulerHubEvents.RunStarted).ConfigureAwait(false);

        // The stored definition is NEVER mutated. A per-fire override (manual refresh) is merged onto a copy of the
        // parameters that the handler sees — only the whitelisted use-case key. A cron/no-override fire passes the stored
        // parameters through unchanged.
        var effectiveParameters = ApplyParameterOverrides(definition.ParameterJson, parameterOverrides);

        var context = new ScheduledJobExecutionContext
        {
            ScheduledJobId = definition.Id,
            TemplateId = definition.TemplateId,
            DisplayName = definition.DisplayName,
            Parameters = effectiveParameters,
            FireInstanceId = fireInstanceId,
            ScheduledFireTimeUtc = scheduledFireTimeUtc,
            ActualFireTimeUtc = actualFireTimeUtc,
            TriggeredBy = ScheduledRunTrigger.Schedule,
            ReportProgressAsync = BuildProgressReporter(run.Id, definition.Id)
        };

        try
        {
            await handler.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);

            var completedMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var updated = await _runStore.UpdateLifecycleAsync(run.Id,
                ScheduledRunStatus.Succeeded,
                completedMs,
                completedMs - actualFireMs,
                "Completed.",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            await SafePublishRunAsync(updated ?? run, SchedulerHubEvents.RunCompleted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var completedMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            // Operator cancel stamps CancellationRequestedAtUtc before interrupting; its absence means the only other
            // token-cancel source — the auto-interrupt max-runtime plugin — fired (graceful shutdown waits for jobs).
            var latest = await _runStore.GetByIdAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
            var wasCancelRequested = latest?.CancellationRequestedAtUtc is not null;
            var status = wasCancelRequested ? ScheduledRunStatus.Cancelled : ScheduledRunStatus.TimedOut;

            var updated = await _runStore.UpdateLifecycleAsync(run.Id,
                status,
                completedMs,
                completedMs - actualFireMs,
                errorMessage: wasCancelRequested
                    ? "Run was cancelled."
                    : "Run exceeded its maximum runtime and was interrupted.",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            await SafePublishRunAsync(updated ?? run,
                wasCancelRequested ? SchedulerHubEvents.RunCancelled : SchedulerHubEvents.RunFailed).ConfigureAwait(false);

            // Re-throw so Quartz observes the interrupt / shutdown.
            throw;
        }
        catch (Exception exception)
        {
            var completedMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            // Full exception (incl. message + stack) goes to the trusted server log only; the persisted run row carries
            // a generic message and the exception type name so no message text or stack trace ever reaches an API/UI.
            _logger.LogWarning(exception,
                "Scheduled job {ScheduledJobId} run {RunId} (fire {FireInstanceId}) failed.",
                definition.Id,
                run.Id,
                fireInstanceId);

            // Only a handler-declared, already-operator-safe ScheduledJobExecutionException widens the UI-visible
            // message; every other exception type keeps the generic constant so no raw message or stack text leaks.
            var errorMessage = exception is ScheduledJobExecutionException safe
                ? safe.Message
                : "The scheduled job failed during execution.";

            var updated = await _runStore.UpdateLifecycleAsync(run.Id,
                ScheduledRunStatus.Failed,
                completedMs,
                completedMs - actualFireMs,
                errorMessage: errorMessage,
                errorDetails: exception.GetType().FullName,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            await SafePublishRunAsync(updated ?? run, SchedulerHubEvents.RunFailed).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Returns <paramref name="storedParametersJson" /> with ONLY the whitelisted <c>useCase</c> and/or <c>limit</c>
    ///     properties replaced by the per-fire override, leaving every other property untouched. The stored definition is
    ///     never mutated — this works on a parsed copy. No override, no whitelisted key in the override, or
    ///     unparseable/empty stored JSON returns the stored JSON verbatim (the handler then validates it exactly as it
    ///     would a normal fire), so an override can never fabricate parameters or override anything other than the two
    ///     whitelisted keys. The <c>limit</c> override is written back as a JSON number (the stored shape), so the
    ///     handler's numeric parse is unchanged.
    /// </summary>
    private static string? ApplyParameterOverrides(string? storedParametersJson,
        IReadOnlyDictionary<string, string>? parameterOverrides)
    {
        if (parameterOverrides is null || string.IsNullOrWhiteSpace(storedParametersJson))
        {
            return storedParametersJson;
        }

        string? useCaseOverride = null;
        if (parameterOverrides.TryGetValue(SchedulerJobKeys.ModelFitUseCaseOverrideKey, out var useCaseValue)
            && !string.IsNullOrWhiteSpace(useCaseValue))
        {
            useCaseOverride = useCaseValue;
        }

        int? limitOverride = null;
        if (parameterOverrides.TryGetValue(SchedulerJobKeys.ModelFitLimitOverrideKey, out var limitValue)
            && int.TryParse(limitValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit))
        {
            limitOverride = parsedLimit;
        }

        string? quantOverride = null;
        if (parameterOverrides.TryGetValue(SchedulerJobKeys.ModelFitQuantOverrideKey, out var quantValue)
            && !string.IsNullOrWhiteSpace(quantValue))
        {
            quantOverride = quantValue;
        }

        int? ctxTargetOverride = null;
        if (parameterOverrides.TryGetValue(SchedulerJobKeys.ModelFitCtxTargetOverrideKey, out var ctxValue)
            && int.TryParse(ctxValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCtx))
        {
            ctxTargetOverride = parsedCtx;
        }

        if (useCaseOverride is null && limitOverride is null && quantOverride is null && ctxTargetOverride is null)
        {
            return storedParametersJson;
        }

        JsonObject? parametersObject;
        try
        {
            parametersObject = JsonNode.Parse(storedParametersJson) as JsonObject;
        }
        catch (JsonException)
        {
            // Leave malformed stored JSON untouched; the handler raises the same validation error it does today.
            return storedParametersJson;
        }

        if (parametersObject is null)
        {
            return storedParametersJson;
        }

        if (useCaseOverride is not null)
        {
            SetWhitelistedProperty(parametersObject, OverridableUseCaseProperty, JsonValue.Create(useCaseOverride));
        }

        if (limitOverride is { } limit)
        {
            // The stored `limit` is a JSON number; write the override as a number so the handler's int parse still works.
            SetWhitelistedProperty(parametersObject, OverridableLimitProperty, JsonValue.Create(limit));
        }

        if (quantOverride is not null)
        {
            SetWhitelistedProperty(parametersObject, OverridableQuantProperty, JsonValue.Create(quantOverride));
        }

        if (ctxTargetOverride is { } ctxTarget)
        {
            // The stored `ctxTarget` is a JSON number; write the override as a number so the handler's int parse works.
            SetWhitelistedProperty(parametersObject, OverridableCtxTargetProperty, JsonValue.Create(ctxTarget));
        }

        return parametersObject.ToJsonString();
    }

    /// <summary>
    ///     Sets <paramref name="property" /> on <paramref name="parametersObject" /> to <paramref name="value" />,
    ///     replacing an existing property in place (preserving its original casing) or adding a camelCase one when absent.
    ///     ONLY the named whitelisted property is touched; every other stored parameter is left untouched.
    /// </summary>
    private static void SetWhitelistedProperty(JsonObject parametersObject, string property, JsonNode? value)
    {
        var existingKey = parametersObject
                          .Select(pair => pair.Key)
                          .FirstOrDefault(key => string.Equals(key, property, StringComparison.OrdinalIgnoreCase));

        parametersObject[existingKey ?? property] = value;
    }

    private Func<string, int?, CancellationToken, Task> BuildProgressReporter(Guid runId, Guid scheduledJobId)
    {
        // Box the sequence so the closure can Interlocked.Increment a shared cell even if a handler reports progress
        // from parallel tasks. data_json carries the optional percent (encrypted at rest by the interceptors).
        var sequence = new int[1];

        // The third delegate parameter is the handler's own (possibly-cancelled) progress token — intentionally NOT
        // forwarded to the event write below (see comment there), so it is unused.
        return async (message, percent, progressCancellationToken) =>
        {
            var nextSequence = Interlocked.Increment(ref sequence[0]);
            var dataJson = percent is { } value
                ? string.Create(CultureInfo.InvariantCulture, $"{{\"percent\":{value}}}")
                : null;

            // Persist the progress event with CancellationToken.None — same policy as the terminal writes. A handler
            // reporting progress on its way out of a cancelled run forwards its (already-cancelled) token; honoring it
            // here would throw a second OperationCanceledException from SaveChanges that masks the real cancellation.
            _ = await _runEventStore.AddAsync(new ScheduledJobRunEventInput(runId, nextSequence, ScheduledRunEventLevel.Progress, message, dataJson),
                CancellationToken.None).ConfigureAwait(false);

            await SafePublishProgressAsync(runId, scheduledJobId, message, percent).ConfigureAwait(false);
        };
    }

    private async Task SafePublishRunAsync(ScheduledJobRunRecord record, string eventType)
    {
        try
        {
            var occurredAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var runEvent = new SchedulerRunHubEvent(eventType,
                record.Id,
                record.ScheduledJobId,
                record.TemplateId,
                record.Status,
                record.TriggeredBy,
                record.ScheduledFireTimeUtc,
                record.ActualFireTimeUtc,
                record.CompletedAtUtc,
                record.DurationMs,
                record.Summary,
                record.ErrorMessage,
                occurredAt);

            await _eventPublisher.PublishRunAsync(runEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish scheduler event {EventType} for run {RunId}.", eventType, record.Id);
        }
    }

    private async Task SafePublishProgressAsync(Guid runId, Guid scheduledJobId, string? message, int? percent)
    {
        try
        {
            var occurredAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await _eventPublisher.PublishRunProgressAsync(new SchedulerRunProgressHubEvent(SchedulerHubEvents.RunProgress, runId, scheduledJobId, message, percent, occurredAt),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish scheduler progress event for run {RunId}.", runId);
        }
    }

    private static bool IsTerminal(ScheduledRunStatus status)
    {
        return status is ScheduledRunStatus.Succeeded
            or ScheduledRunStatus.Failed
            or ScheduledRunStatus.Cancelled
            or ScheduledRunStatus.TimedOut
            or ScheduledRunStatus.Skipped;
    }
}
