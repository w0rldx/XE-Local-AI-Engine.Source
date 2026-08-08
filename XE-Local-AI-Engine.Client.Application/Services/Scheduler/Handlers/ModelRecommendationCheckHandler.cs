namespace XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;

/// <summary>
///     Quartz template handler for the reserved <c>model-recommendation-check</c> template. On each fire
///     it validates the decrypted parameters, then invokes <see cref="IModelFitRefreshService" /> to run the local model
///     advisor (box-aware GGUF recommendation) and replace the cached recommendation snapshot.
///     <para>
///         <b>Singleton.</b> The registry captures every handler in a <c>FrozenDictionary</c> at construction, so this
///         handler is effectively a singleton and CANNOT inject scoped services. It injects
///         <see cref="IServiceScopeFactory" /> and creates a scope per <see cref="ExecuteAsync" />, resolving the scoped
///         refresh service inside.
///     </para>
///     <para>
///         <b>Owns no scheduler state.</b> It never creates/updates scheduler run rows and never publishes SignalR — the
///         dispatcher owns those. It forwards <see cref="ScheduledJobExecutionContext.ReportProgressAsync" /> to the
///         refresh service, lets <see cref="OperationCanceledException" /> propagate (so the dispatcher records a
///         Cancelled run), and throws a <see cref="ScheduledJobExecutionException" /> carrying the refresh result's
///         contractually-sanitized <see cref="ModelFitRefreshResult.SanitizedError" /> on a non-success refresh so the
///         dispatcher records a Failed run with an actionable reason. The refresh service is invoked ONLY here — there is no bypass path.
///     </para>
/// </summary>
public sealed class ModelRecommendationCheckHandler : IScheduledJobHandler
{
    /// <summary>The reserved scheduler template id this handler claims.</summary>
    public const string TemplateIdValue = "model-recommendation-check";

    /// <summary>The advisor's fixed provider sentinel — passed to the validator (use-case/limit bounds only, no caller provider).</summary>
    private const string AdvisorProviderName = "llama.cpp";

    /// <summary>
    ///     JSON-Schema (draft-07) for the decrypted <c>model-recommendation-check</c> parameters. The approved-image and
    ///     provider-name params are gone (the advisor runs box-aware GGUF recommendation in-process); the optional
    ///     <c>quantOverride</c> replaces the default <c>Q4_K_M</c> and <c>ctxTarget</c> overrides the fit context window.
    /// </summary>
    private const string ParameterSchemaJson =
        """
        {
          "$schema": "http://json-schema.org/draft-07/schema#",
          "type": "object",
          "additionalProperties": false,
          "required": ["operation", "limit"],
          "properties": {
            "operation": { "type": "string", "enum": ["Recommend"] },
            "useCase": { "type": "string", "enum": ["general", "coding", "reasoning", "chat", "multimodal", "embedding"] },
            "limit": { "type": "integer", "minimum": 1, "maximum": 50 },
            "quantOverride": { "type": ["string", "null"] },
            "ctxTarget": { "type": ["integer", "null"], "minimum": 256 }
          }
        }
        """;

    private static readonly JsonSerializerOptions ParameterSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private readonly ILogger<ModelRecommendationCheckHandler> _logger;

    private readonly IServiceScopeFactory _scopeFactory;

    public ModelRecommendationCheckHandler(IServiceScopeFactory scopeFactory,
        ILogger<ModelRecommendationCheckHandler> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string TemplateId => TemplateIdValue;

    public ScheduledJobTemplateDescriptor Descriptor { get; } = new(TemplateIdValue,
        "Model recommendation check",
        "Runs the local model advisor and refreshes the cached model recommendation snapshot.",
        ParameterSchemaJson,
        BuildDefaultParameters(),
        [ScheduleKind.Manual, ScheduleKind.OneShot, ScheduleKind.Cron, ScheduleKind.SimpleInterval],
        // Manual is the recommended kind for this on-demand template (the React "Refresh now" button fires it via
        // TriggerNowAsync). Cron/OneShot/SimpleInterval stay supported for operators who want a recurring refresh.
        ScheduleKind.Manual,
        SchedulerMisfirePolicy.SkipMissed,
        DefaultMaxRuntimeSeconds: 600,
        AllowManualTrigger: true,
        AllowAgentCreation: false,
        HistoryDetailLevel.Detailed);

    public async Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = ParseAndValidate(context.Parameters);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var refreshService = scope.ServiceProvider.GetRequiredService<IModelFitRefreshService>();

        // OperationCanceledException propagates untouched (dispatcher records Cancelled). The progress callback may be
        // null (Summary-level dispatch) — the refresh service null-checks it.
        var result = await refreshService.RefreshAsync(request, context.ReportProgressAsync, cancellationToken)
                                         .ConfigureAwait(false);

        if (result.Status != ModelFitRunStatus.Succeeded)
        {
            // SanitizedError is operator-safe by the IModelFitRefreshService contract (never secrets / raw output), so
            // it is surfaced via ScheduledJobExecutionException — the dispatcher records a Failed run carrying this exact
            // reason. Throwing (rather than returning) prevents a spurious success record.
            _logger.LogWarning("Model recommendation check did not succeed (template {TemplateId}, status {Status}).",
                TemplateIdValue,
                result.Status);

            throw new ScheduledJobExecutionException(result.SanitizedError ?? "The model recommendation refresh did not succeed.");
        }
    }

    /// <summary>
    ///     Parses the decrypted parameter JSON and validates it against the same model-fit request validator the runner
    ///     uses. The operation must be <see cref="ModelFitOperation.Recommend" /> for this template. Any validation
    ///     failure throws <see cref="ScheduledJobValidationException" /> so the dispatcher records the failure without
    ///     invoking the runner. Never echoes raw parameter values.
    /// </summary>
    private ModelFitRefreshRequest ParseAndValidate(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            throw new ScheduledJobValidationException("Model recommendation check parameters are required.");
        }

        ModelRecommendationCheckParameters? parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<ModelRecommendationCheckParameters>(parametersJson, ParameterSerializerOptions);
        }
        catch (JsonException)
        {
            throw new ScheduledJobValidationException("Model recommendation check parameters are not valid JSON.");
        }

        if (parameters is null)
        {
            throw new ScheduledJobValidationException("Model recommendation check parameters are required.");
        }

        if (parameters.Operation != ModelFitOperation.Recommend)
        {
            throw new ScheduledJobValidationException("Only the Recommend operation is supported by this template.");
        }

        // The advisor targets llama.cpp in-process; the request validator's provider arg is fixed to that sentinel so it
        // still enforces the use-case allowlist + limit bounds without a caller-supplied provider name.
        using var scope = _scopeFactory.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<ModelFitRequestValidator>();
        var validationError = validator.GetValidationError(parameters.Operation,
            parameters.UseCase,
            parameters.Limit,
            AdvisorProviderName,
            modelName: null);
        if (validationError is not null)
        {
            throw new ScheduledJobValidationException(validationError);
        }

        return new ModelFitRefreshRequest(parameters.Operation,
            parameters.UseCase,
            parameters.Limit,
            parameters.QuantOverride,
            parameters.CtxTarget);
    }

    private static string BuildDefaultParameters()
    {
        return JsonSerializer.Serialize(new ModelRecommendationCheckParameters
        {
            Operation = ModelFitOperation.Recommend,
            UseCase = "coding",
            Limit = 5
        }, ParameterSerializerOptions);
    }

    /// <summary>Decrypted-parameter shape for the <c>model-recommendation-check</c> template.</summary>
    private sealed record ModelRecommendationCheckParameters
    {
        [JsonPropertyName("operation")]
        public ModelFitOperation Operation { get; init; } = ModelFitOperation.Recommend;

        [JsonPropertyName("useCase")]
        public string? UseCase { get; init; }

        [JsonPropertyName("limit")]
        public int Limit { get; init; }

        [JsonPropertyName("quantOverride")]
        public string? QuantOverride { get; init; }

        [JsonPropertyName("ctxTarget")]
        public int? CtxTarget { get; init; }
    }
}
