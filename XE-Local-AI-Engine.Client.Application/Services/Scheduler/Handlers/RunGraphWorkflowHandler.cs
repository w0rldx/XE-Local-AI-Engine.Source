namespace XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Quartz template handler for <c>run-graph</c>: starts a saved graph workflow with origin <c>Schedule</c> and waits
///     for the run to end. See docs/wiki/06-scheduler.md ("Shipped templates").
/// </summary>
/// <remarks>
///     Every fire mints its own request id, so each fire is a new run. The wait polls the run service: the run hub's
///     publisher has no service-side subscription seam, and the dispatcher is the only writer of run status.
/// </remarks>
public sealed class RunGraphWorkflowHandler : IScheduledJobHandler
{
    /// <summary>The reserved scheduler template id this handler claims.</summary>
    public const string TemplateIdValue = "run-graph";

    /// <summary>The bound on <c>timeoutSeconds</c>, the same ceiling a graph node timeout has.</summary>
    public const int MaxTimeoutSeconds = 86_400;

    /// <summary>The longest outcome label the summary quotes.</summary>
    public const int MaxOutcomeLength = 200;

    private const string ParameterSchemaJson =
        """
        {
          "$schema": "http://json-schema.org/draft-07/schema#",
          "type": "object",
          "additionalProperties": false,
          "required": ["definitionId"],
          "properties": {
            "definitionId": { "type": "string", "format": "uuid", "minLength": 1 },
            "inputJson": { "type": ["string", "null"] },
            "timeoutSeconds": { "type": ["integer", "null"], "minimum": 1, "maximum": 86400 }
          }
        }
        """;

    // Fixed poll interval; subscribe to run events instead if a service-side seam ever exists.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private static readonly GraphWorkflowRunOrigin ScheduleOrigin = new()
    {
        Source = GraphWorkflowTriggerSource.Schedule
    };

    private static readonly JsonSerializerOptions ParameterSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IOptions<GraphWorkflowOptions> _options;

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly TimeProvider _timeProvider;

    public RunGraphWorkflowHandler(IServiceScopeFactory scopeFactory, IOptions<GraphWorkflowOptions> options, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string TemplateId => TemplateIdValue;

    public ScheduledJobTemplateDescriptor Descriptor { get; } = new()
    {
        TemplateId = TemplateIdValue,
        DisplayName = "Run a graph workflow",
        Description = "Starts a saved graph workflow on a schedule and waits for it to finish. The workflow must accept Schedule as a trigger source.",
        ParameterSchema = ParameterSchemaJson,
        DefaultParameters = null,
        SupportedScheduleKinds = [ScheduleKind.Cron, ScheduleKind.OneShot, ScheduleKind.SimpleInterval, ScheduleKind.Manual],
        DefaultScheduleKind = ScheduleKind.Cron,
        DefaultMisfirePolicy = SchedulerMisfirePolicy.SkipMissed,
        // The default run timeout (600 s) plus a minute to cancel, so the Quartz ceiling does not pre-empt the handler's own.
        DefaultMaxRuntimeSeconds = 660,
        AllowManualTrigger = true,
        AllowAgentCreation = false,
        HistoryDetailLevel = HistoryDetailLevel.Detailed
    };

    public async Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var parameters = ParseAndValidate(context.Parameters);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        if (!await services.GetRequiredService<INodeRuntimeSettings>().GetGraphWorkflowsEnabledAsync(cancellationToken))
        {
            throw new ScheduledJobExecutionException("Graph workflows are turned off on this node, so the scheduled workflow did not run. Turn them on in Node Settings to run it.");
        }

        var runService = services.GetRequiredService<IGraphWorkflowRunService>();
        var requestId = Guid.NewGuid();
        GraphWorkflowRunDetail detail;
        try
        {
            detail = await StartAsync(runService, parameters, requestId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The start may have committed the run before the cancellation surfaced; the request id finds it, since
            // the run must not outlive the job that started it, actions included.
            if (await services.GetRequiredService<IGraphWorkflowStore>().FindRunByRequestAsync(requestId, CancellationToken.None) is { } committed)
            {
                await CancelQuietlyAsync(runService, committed.Id);
            }

            throw;
        }

        var runId = detail.Run.Id;
        var deadline = _timeProvider.GetUtcNow().AddSeconds(parameters.TimeoutSeconds);
        GraphWorkflowRunStatus? reported = null;

        try
        {
            while (!GraphWorkflowStateMachine.IsTerminal(detail.Run.Status))
            {
                if (reported != detail.Run.Status)
                {
                    reported = detail.Run.Status;
                    await ReportAsync(context, $"Graph workflow run {runId} is {reported}.", cancellationToken);
                }

                if (_timeProvider.GetUtcNow() >= deadline)
                {
                    await CancelQuietlyAsync(runService, runId);
                    throw new ScheduledJobExecutionException(string.Create(CultureInfo.InvariantCulture,
                        $"Graph workflow run {runId} did not finish within {parameters.TimeoutSeconds} s and was cancelled."));
                }

                await Task.Delay(PollInterval, _timeProvider, cancellationToken);
                detail = await runService.GetRunAsync(runId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The job was cancelled or hit its max runtime: the run must not outlive the job that started it.
            await CancelQuietlyAsync(runService, runId);
            throw;
        }

        Settle(context, detail, requestId);
    }

    /// <summary>Rejects at create/update time what <see cref="ExecuteAsync" /> would reject on every fire.</summary>
    public void ValidateParameters(string? parametersJson) =>
        _ = ParseAndValidate(parametersJson);

    /// <summary>Refuses a definition that does not exist or does not list Schedule among its trigger sources.</summary>
    public async Task ValidateReferencesAsync(string? parametersJson, CancellationToken cancellationToken)
    {
        var parameters = ParseAndValidate(parametersJson);
        await using var scope = _scopeFactory.CreateAsyncScope();
        GraphWorkflowDefinitionSnapshot definition;
        try
        {
            definition = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().GetDefinitionAsync(parameters.DefinitionId, cancellationToken);
        }
        catch (GraphWorkflowNotFoundException exception)
        {
            throw new ScheduledJobValidationException("definitionId: no graph workflow with this id exists. Select an existing workflow for the scheduled run.", exception);
        }

        if (!definition.TriggerSources.HasFlag(GraphWorkflowTriggerSource.Schedule))
        {
            throw new ScheduledJobValidationException(
                $"definitionId: graph workflow '{definition.Name}' does not accept runs started by Schedule. Add Schedule to the workflow's trigger sources in its settings, then save this job.");
        }
    }

    private static async Task<GraphWorkflowRunDetail> StartAsync(IGraphWorkflowRunService runService,
        RunGraphWorkflowParameters parameters,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runService.StartAsync(parameters.DefinitionId, requestId, parameters.InputJson, definitionVersion: null, ScheduleOrigin, cancellationToken);
        }
        catch (GraphWorkflowNotFoundException exception)
        {
            throw new ScheduledJobExecutionException("The scheduled graph workflow could not be found. It may have been deleted.", exception);
        }
        catch (GraphWorkflowRunConflictException exception)
        {
            // With no version pinned, the only start conflict is the definition refusing the Schedule source; its message names the definition.
            throw new ScheduledJobExecutionException($"{exception.Message} Add Schedule to the workflow's trigger sources in its settings to let this job run it.", exception);
        }
        catch (GraphWorkflowValidationException exception)
        {
            // The same structural refusal the Graph Workflows page shows the operator (input cap, unrunnable tool node).
            throw new ScheduledJobExecutionException(exception.Message, exception);
        }
    }

    /// <summary>Records the End outcome on success; a failed or cancelled run throws its structural reason, never node output.</summary>
    private static void Settle(ScheduledJobExecutionContext context, GraphWorkflowRunDetail detail, Guid requestId)
    {
        var run = detail.Run;
        switch (run.Status)
        {
            case GraphWorkflowRunStatus.Failed:
                var failedNode = detail.NodeRuns.FirstOrDefault(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Failed);
                var nodeSuffix = failedNode is null ? string.Empty : $" at node '{failedNode.NodeKey}' ({failedNode.FailureClass})";
                throw new ScheduledJobExecutionException($"Graph workflow run {run.Id} failed: {run.FailureClass}{nodeSuffix}.");
            case GraphWorkflowRunStatus.Cancelled:
                throw new ScheduledJobExecutionException($"Graph workflow run {run.Id} was cancelled before it completed ({run.FailureClass}).");
            default:
                var outcome = EndOutcome(detail.NodeRuns);
                var outcomeText = outcome is null ? string.Empty : $" with outcome '{outcome}'";
                context.Summary = $"Graph workflow run {run.Id} completed{outcomeText} (request {requestId}).";
                break;
        }
    }

    /// <summary>The operator-declared outcome label of the End node that succeeded; its result is model text and stays out of the summary.</summary>
    private static string? EndOutcome(IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns)
    {
        var end = nodeRuns.FirstOrDefault(static nodeRun => nodeRun is { Kind: GraphWorkflowNodeKind.End, Status: GraphWorkflowNodeRunStatus.Succeeded });
        if (end?.OutputJson is not { } outputJson)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Object
                || !output.TryGetProperty("outcome", out var outcome)
                || outcome.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = outcome.GetString() ?? string.Empty;
            return text.Length <= MaxOutcomeLength ? text : text[..MaxOutcomeLength];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Best-effort cancel on a token of its own, so a cancelled job still reaches the run; an already-ended run is fine.</summary>
    private static async Task CancelQuietlyAsync(IGraphWorkflowRunService runService, Guid runId)
    {
        try
        {
            await runService.CancelAsync(runId, CancellationToken.None);
        }
        catch (GraphWorkflowRunConflictException)
        {
            // It ended between the last read and the cancel.
        }
    }

    private static Task ReportAsync(ScheduledJobExecutionContext context, string message, CancellationToken cancellationToken) =>
        context.ReportProgressAsync is { } report ? report(message, null, cancellationToken) : Task.CompletedTask;

    /// <summary>Parses the decrypted parameters; never echoes raw values into the error text.</summary>
    private RunGraphWorkflowParameters ParseAndValidate(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            throw new ScheduledJobValidationException("Run-graph parameters are required.");
        }

        RunGraphWorkflowParametersDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RunGraphWorkflowParametersDto>(parametersJson, ParameterSerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new ScheduledJobValidationException("Run-graph parameters are not valid JSON.", exception);
        }

        if (dto is null)
        {
            throw new ScheduledJobValidationException("Run-graph parameters are required.");
        }

        if (string.IsNullOrWhiteSpace(dto.DefinitionId) || !Guid.TryParse(dto.DefinitionId, out var definitionId) || definitionId == Guid.Empty)
        {
            throw new ScheduledJobValidationException("A valid graph workflow must be selected for the scheduled run.");
        }

        var options = _options.Value;
        var inputJson = string.IsNullOrWhiteSpace(dto.InputJson) ? null : dto.InputJson;
        if (inputJson is not null)
        {
            ValidateInput(inputJson, options.MaxRunInputBytes);
        }

        if (dto.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            throw new ScheduledJobValidationException(string.Create(CultureInfo.InvariantCulture,
                $"The run timeout must be between 1 and {MaxTimeoutSeconds} seconds."));
        }

        return new RunGraphWorkflowParameters
        {
            DefinitionId = definitionId,
            InputJson = inputJson,
            TimeoutSeconds = dto.TimeoutSeconds ?? options.DefaultNodeTimeoutSeconds
        };
    }

    private static void ValidateInput(string inputJson, int maxRunInputBytes)
    {
        if (Encoding.UTF8.GetByteCount(inputJson) > maxRunInputBytes)
        {
            throw new ScheduledJobValidationException(string.Create(CultureInfo.InvariantCulture,
                $"The run input is larger than the {maxRunInputBytes} bytes one run may carry."));
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ScheduledJobValidationException("The run input must be a JSON object.");
            }
        }
        catch (JsonException exception)
        {
            throw new ScheduledJobValidationException("The run input must be a JSON object.", exception);
        }
    }

    /// <summary>Validated, code-facing parameters for one <c>run-graph</c> fire.</summary>
    private sealed class RunGraphWorkflowParameters
    {
        public required Guid DefinitionId { get; init; }

        public required string? InputJson { get; init; }

        public required int TimeoutSeconds { get; init; }
    }

    /// <summary>Decrypted-parameter wire shape for the <c>run-graph</c> template.</summary>
    private sealed class RunGraphWorkflowParametersDto
    {
        [JsonPropertyName("definitionId")]
        public string? DefinitionId { get; init; }

        [JsonPropertyName("inputJson")]
        public string? InputJson { get; init; }

        [JsonPropertyName("timeoutSeconds")]
        public int? TimeoutSeconds { get; init; }
    }
}
