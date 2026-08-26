namespace XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Quartz template handler for the <c>run-benchmark-batch</c> template: freezes a whole model × KV-cache matrix
///     against one benchmark project on a schedule, so an eight-hour overnight matrix is a schedule rather than a
///     foreground wait. It <b>enqueues and returns</b> — the runs themselves are drained by the existing single-consumer
///     <c>BenchmarkQueueHostedService</c>, so the job holds neither the scheduler thread nor the GPU for the duration.
///     <para>
///         <b>Singleton.</b> The registry captures every handler in a <c>FrozenDictionary</c> at construction, so this
///         handler is effectively a singleton and CANNOT inject scoped services. It injects
///         <see cref="IServiceScopeFactory" /> and creates a scope per <see cref="ExecuteAsync" /> (mirrors
///         <see cref="RunSavedAgentHandler" />).
///     </para>
///     <para>
///         <b>Refuses to pile up.</b> A nightly matrix that fires while the previous night's is still draining would
///         queue a second matrix behind the first and measure the same project twice. A fire that finds queued or
///         running WORK of any kind on the project — primary, judge, fidelity or pairwise comparison, all of which
///         outlive the runs they belong to — is recorded as a SKIPPED fire naming what is still busy — not a failure, because
///         nothing is wrong: the node is simply still busy. <see cref="SchedulerMisfirePolicy.SkipMissed" /> covers the
///         other half, a node that was off when the trigger was due.
///     </para>
///     <para>
///         <b>Owns no scheduler state.</b> It records a content-safe summary (project id, cells requested, runs created,
///         per-cell failure reasons — never a prompt, never a model answer) through
///         <see cref="ScheduledJobExecutionContext.ReportProgressAsync" /> and on
///         <see cref="ScheduledJobExecutionContext.Summary" /> (which the dispatcher persists onto the run row), and
///         throws
///         <see cref="ScheduledJobExecutionException" /> with an operator-safe reason only when EVERY cell failed.
///     </para>
/// </summary>
public sealed class RunBenchmarkBatchHandler : IScheduledJobHandler
{
    /// <summary>The reserved scheduler template id this handler claims.</summary>
    public const string TemplateIdValue = "run-benchmark-batch";

    /// <summary>Matrix ceiling, the same one the interactive batch endpoint enforces. Beyond this is a mistake.</summary>
    private const int MaxCells = 50;

    /// <summary>Per-axis ceilings. Ten models over four KV types is already a very long night.</summary>
    private const int MaxModels = 10;

    private const int MaxKvCacheTypes = 4;
    private const int MaxRepeatCount = 10;

    /// <summary>
    ///     JSON-Schema (draft-07) for the decrypted <c>run-benchmark-batch</c> parameters: the project to measure and the
    ///     matrix to freeze against it. Values are validated again in code before use — the descriptor schema is
    ///     documentation for the management UI, never the enforcement point.
    /// </summary>
    private const string ParameterSchemaJson =
        """
        {
          "$schema": "http://json-schema.org/draft-07/schema#",
          "type": "object",
          "additionalProperties": false,
          "required": ["projectId", "models"],
          "properties": {
            "projectId": { "type": "string", "format": "uuid", "minLength": 1 },
            "models": {
              "type": "array",
              "minItems": 1,
              "maxItems": 10,
              "items": { "type": "string", "minLength": 1 }
            },
            "kvCacheTypes": {
              "type": "array",
              "minItems": 1,
              "maxItems": 4,
              "items": { "type": ["string", "null"] }
            },
            "repeatCount": { "type": "integer", "minimum": 1, "maximum": 10 },
            "warmup": { "type": "boolean" }
          }
        }
        """;

    /// <summary>
    ///     Pre-filled into a new schedule's parameter box so an operator edits a shape rather than inventing one. The
    ///     ids are placeholders; the form is a plain JSON textarea.
    /// </summary>
    private const string DefaultParametersJson =
        """
        {
          "projectId": "00000000-0000-0000-0000-000000000000",
          "models": [],
          "kvCacheTypes": [null],
          "repeatCount": 1,
          "warmup": false
        }
        """;

    /// <summary>
    ///     How long a fire may spend freezing EACH cell before it stops and reports what it started. The whole fire's
    ///     budget is this times the number of cells, so the ceiling grows with the matrix the operator asked for.
    ///     <para>
    ///         The interactive batch endpoint spends 45 s on the WHOLE request, because a connection is held open; a
    ///         Quartz fire holds nothing, and a flat 45 s truncates the overnight matrix this template exists to run —
    ///         measured live on this node, a cold cell costs ~18 s (the freeze verifies each model's GGUF by digest),
    ///         so a flat budget enqueued 3 of 4 cells. Per-cell keeps the guard that matters — a pathological host
    ///         cannot hang the fire indefinitely — while still admitting the matrix.
    ///     </para>
    ///     <para>
    ///         Checked BETWEEN cells, never inside one, so the budget can be overrun by one cell and no cell is ever
    ///         half-frozen. The scheduler's own max-runtime ceiling remains the outer bound.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan PerCellFreezeBudget = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions ParameterSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<RunBenchmarkBatchHandler> _logger;

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly TimeProvider _timeProvider;

    public RunBenchmarkBatchHandler(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<RunBenchmarkBatchHandler> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string TemplateId => TemplateIdValue;

    public ScheduledJobTemplateDescriptor Descriptor { get; } = new(TemplateIdValue,
        "Run a benchmark matrix",
        "Enqueues a model × KV-cache matrix against one benchmark project on a schedule. Skips the fire when the project already has work queued.",
        ParameterSchemaJson,
        DefaultParametersJson,
        [ScheduleKind.Cron, ScheduleKind.OneShot, ScheduleKind.SimpleInterval, ScheduleKind.Manual],
        // An overnight matrix is the point, so Cron is pre-selected; OneShot covers "tonight only".
        ScheduleKind.Cron,
        // A matrix missed while the node was off must not fire the moment it comes back — it would land in the middle of
        // whatever the operator is doing on the GPU. The next scheduled slot is soon enough.
        SchedulerMisfirePolicy.SkipMissed,
        // No template default: the fire only ENQUEUES, so it is bounded by its own per-cell freeze budget rather than by
        // the length of the runs it queues. Leaving this blank keeps the node-level ceiling in charge.
        DefaultMaxRuntimeSeconds: null,
        AllowManualTrigger: true,
        // Locked decision: an AI agent may schedule a saved-agent run; it may not schedule GPU-hours.
        AllowAgentCreation: false,
        HistoryDetailLevel.Detailed);

    public async Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var parameters = ParseAndValidate(context.Parameters);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<IBenchmarkStore>();
        var freezeService = services.GetRequiredService<IBenchmarkRunFreezeService>();

        var project = await store.GetProjectAsync(parameters.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            throw new ScheduledJobExecutionException("The scheduled benchmark project could not be found. It may have been deleted.");
        }

        // Refuse to pile up (R-7). Reported as a skipped fire, not a failure: the schedule is fine, the node is busy.
        // Counted over WORK ITEMS of every kind, not over run statuses: judging, fidelity and pairwise work outlives
        // the run it belongs to, so the previous matrix can hold the single-consumer queue and the GPU for hours while
        // every one of its runs already reads Succeeded — and the next fire piled a second matrix on top of it.
        var active = await store.CountActiveWorkAsync(parameters.ProjectId, cancellationToken).ConfigureAwait(false);
        var activeCount = active.Values.Sum();
        if (activeCount > 0)
        {
            // Named per kind, because "still busy" and "still busy JUDGING" are different operator actions.
            var breakdown = string.Join(", ", active.OrderBy(static entry => entry.Key).Select(static entry => $"{entry.Key} {entry.Value}"));
            _logger.LogInformation(
                "Scheduled benchmark batch for project {ProjectId} was skipped: {ActiveCount} work item(s) of that project are still queued or running ({Breakdown}).",
                parameters.ProjectId,
                activeCount,
                breakdown);
            await ReportAsync(context,
                    $"Skipped: benchmark project {parameters.ProjectId} still has {activeCount} work item(s) queued or running ({breakdown}), so no cells were enqueued.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await EnqueueMatrixAsync(context, freezeService, parameters, project.Version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Freezes each cell of the matrix in turn against one shared <see cref="BenchmarkFreezeScope" />, so the
    ///     llama-server capability probe runs once and each distinct model is verified once and then held — the variable
    ///     a matrix exists to hold still. Every group insert is all-or-nothing, so the version the NEXT cell must present
    ///     is the running total of runs created so far; re-reading the project between cells would be the same number
    ///     with a wider race window.
    /// </summary>
    private async Task EnqueueMatrixAsync(ScheduledJobExecutionContext context,
        IBenchmarkRunFreezeService freezeService,
        RunBenchmarkBatchParameters parameters,
        long projectVersion,
        CancellationToken cancellationToken)
    {
        var cells = parameters.Cells;
        var runsCreated = 0;
        var cellsStarted = 0;
        var failures = new List<string>();

        await using var freezeScope = new BenchmarkFreezeScope();
        var startedAt = _timeProvider.GetTimestamp();
        var fireBudget = PerCellFreezeBudget * cells.Count;
        var expectedVersion = projectVersion;

        for (var index = 0; index < cells.Count; index++)
        {
            var (modelName, kvCacheType) = cells[index];

            if (_timeProvider.GetElapsedTime(startedAt) >= fireBudget)
            {
                failures.Add($"{cells.Count - index} cell(s) not attempted: the fire reached its {fireBudget.TotalSeconds:0} second budget");
                break;
            }

            try
            {
                var created = await freezeService.StartAsync(new BenchmarkRunStartRequest(parameters.ProjectId,
                                                       modelName,
                                                       expectedVersion,
                                                       kvCacheType,
                                                       parameters.RepeatCount,
                                                       parameters.Warmup), freezeScope, cancellationToken)
                                                 .ConfigureAwait(false);
                expectedVersion += created.Count;
                runsCreated += created.Count;
                cellsStarted++;
            }
            catch (Exception exception) when (IsWholeMatrixFailure(exception))
            {
                // A vanished project or a project version that moved under the fire is a fact about the MATRIX: every
                // remaining cell would fail identically, and N identical reasons would bury the one thing to fix.
                failures.Add($"{Describe(modelName, kvCacheType)}: {Reason(exception)}");
                if (index + 1 < cells.Count)
                {
                    failures.Add($"{cells.Count - index - 1} cell(s) not attempted after that");
                }

                break;
            }
            catch (Exception exception) when (IsPerCellFailure(exception))
            {
                // One ineligible or uninstalled model must not cost the operator the other nine cells.
                failures.Add($"{Describe(modelName, kvCacheType)}: {Reason(exception)}");
            }
        }

        var summary = $"Benchmark project {parameters.ProjectId}: {cellsStarted}/{cells.Count} cell(s) enqueued, {runsCreated} run(s) created"
                      + (failures.Count == 0 ? "." : $". Not enqueued — {string.Join("; ", failures)}.");
        await ReportAsync(context, summary, cancellationToken).ConfigureAwait(false);

        if (cellsStarted == 0)
        {
            _logger.LogWarning("Scheduled benchmark batch for project {ProjectId} enqueued nothing: {Failures}",
                parameters.ProjectId,
                string.Join("; ", failures));
            throw new ScheduledJobExecutionException($"No cell of the scheduled benchmark matrix could be enqueued. {string.Join("; ", failures)}");
        }
    }

    /// <summary>
    ///     Facts about the MATRIX rather than about one cell: a vanished project, or a project version that moved under
    ///     the fire. A <see cref="KeyNotFoundException" /> is deliberately NOT here — that is one model not being
    ///     installed, which is exactly a per-cell verdict.
    /// </summary>
    private static bool IsWholeMatrixFailure(Exception exception) =>
        exception is BenchmarkNotFoundException
        || (exception is BenchmarkConflictException conflict && string.Equals(conflict.Code, "VersionConflict", StringComparison.Ordinal));

    private static bool IsPerCellFailure(Exception exception) =>
        exception is BenchmarkStoreException or BenchmarkEligibilityException or KeyNotFoundException or NotSupportedException;

    /// <summary>
    ///     An operator-safe reason for one refused cell. Deliberately NOT the raw exception message for the store
    ///     family: a conflict carries a machine code, and everything else here is already an operator-facing sentence.
    /// </summary>
    private static string Reason(Exception exception) =>
        exception switch
        {
            BenchmarkConflictException conflict => conflict.Code,
            BenchmarkNotFoundException => "the project was not found",
            KeyNotFoundException => "the model is not installed",
            NotSupportedException => "the runtime cannot be frozen for this model",
            _ => exception.Message
        };

    private static string Describe(string modelName, string? kvCacheType) =>
        kvCacheType is null ? modelName : $"{modelName} ({kvCacheType})";

    /// <summary>
    ///     Records the fire's content-safe outcome in both places it belongs: the live progress event stream, and
    ///     <see cref="ScheduledJobExecutionContext.Summary" /> so the run row carries the same sentence rather than a
    ///     generic "Completed." that reads identically for an enqueued matrix and a busy-skip.
    /// </summary>
    private static Task ReportAsync(ScheduledJobExecutionContext context, string summary, CancellationToken cancellationToken)
    {
        context.Summary = summary;
        var reportProgress = context.ReportProgressAsync;
        return reportProgress is null ? Task.CompletedTask : reportProgress(summary, 100, cancellationToken);
    }

    /// <summary>
    ///     Parses and validates the decrypted parameter JSON and expands it into the ordered cell list. A malformed or
    ///     out-of-range payload throws <see cref="ScheduledJobValidationException" /> (the dispatcher records the failure
    ///     without freezing anything). Never echoes raw parameter values beyond the model names the operator typed.
    /// </summary>
    private static RunBenchmarkBatchParameters ParseAndValidate(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            throw new ScheduledJobValidationException("Run-benchmark-batch parameters are required.");
        }

        RunBenchmarkBatchParametersDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RunBenchmarkBatchParametersDto>(parametersJson, ParameterSerializerOptions);
        }
        catch (JsonException)
        {
            throw new ScheduledJobValidationException("Run-benchmark-batch parameters are not valid JSON.");
        }

        if (dto is null)
        {
            throw new ScheduledJobValidationException("Run-benchmark-batch parameters are required.");
        }

        if (string.IsNullOrWhiteSpace(dto.ProjectId) || !Guid.TryParse(dto.ProjectId, out var projectId) || projectId == Guid.Empty)
        {
            throw new ScheduledJobValidationException("A valid benchmark project must be selected for the scheduled matrix.");
        }

        var models = (dto.Models ?? [])
                     .Where(static model => !string.IsNullOrWhiteSpace(model))
                     .Select(static model => model!.Trim())
                     .ToArray();
        if (models.Length == 0 || models.Length > MaxModels)
        {
            throw new ScheduledJobValidationException($"The scheduled matrix must name between 1 and {MaxModels} models.");
        }

        // Absent means "one cell per model at the project's own KV setting", which is what a null KV type is everywhere
        // else in the module. An explicitly empty array is an operator mistake, not that.
        var kvCacheTypes = dto.KvCacheTypes is null
            ? new string?[] { null }
            : dto.KvCacheTypes.Select(static type => string.IsNullOrWhiteSpace(type) ? null : type.Trim()).ToArray();
        if (kvCacheTypes.Length == 0 || kvCacheTypes.Length > MaxKvCacheTypes)
        {
            throw new ScheduledJobValidationException($"The scheduled matrix must name between 1 and {MaxKvCacheTypes} KV-cache types, or none at all.");
        }

        // Rejected here rather than at the freeze so the schedule fails with the operator's typo named, once, instead of
        // once per cell of the matrix.
        foreach (var kvCacheType in kvCacheTypes)
        {
            if (!BenchmarkKvCacheType.TryNormalize(kvCacheType, out _))
            {
                throw new ScheduledJobValidationException("The scheduled matrix names a KV-cache type that is not supported.");
            }
        }

        var repeatCount = dto.RepeatCount ?? 1;
        if (repeatCount is < 1 or > MaxRepeatCount)
        {
            throw new ScheduledJobValidationException($"The scheduled matrix repeat count must be between 1 and {MaxRepeatCount}.");
        }

        var cells = models.SelectMany(model => kvCacheTypes.Select(kvCacheType =>
                          {
                              BenchmarkKvCacheType.TryNormalize(kvCacheType, out var normalized);
                              return (Model: model, KvCacheType: normalized);
                          }))
                          .ToArray();
        if (cells.Length > MaxCells)
        {
            throw new ScheduledJobValidationException(
                $"The scheduled matrix expands to {cells.Length.ToString(CultureInfo.InvariantCulture)} cells, past the {MaxCells}-cell ceiling.");
        }

        return new RunBenchmarkBatchParameters(projectId, cells, repeatCount, dto.Warmup ?? false);
    }

    /// <summary>Validated, code-facing parameters for one <c>run-benchmark-batch</c> fire, matrix already expanded.</summary>
    private sealed record RunBenchmarkBatchParameters(
        Guid ProjectId,
        IReadOnlyList<(string Model, string? KvCacheType)> Cells,
        int RepeatCount,
        bool Warmup);

    /// <summary>Decrypted-parameter wire shape for the <c>run-benchmark-batch</c> template.</summary>
    private sealed record RunBenchmarkBatchParametersDto
    {
        [JsonPropertyName("projectId")]
        public string? ProjectId { get; init; }

        [JsonPropertyName("models")]
        public IReadOnlyList<string?>? Models { get; init; }

        [JsonPropertyName("kvCacheTypes")]
        public IReadOnlyList<string?>? KvCacheTypes { get; init; }

        [JsonPropertyName("repeatCount")]
        public int? RepeatCount { get; init; }

        [JsonPropertyName("warmup")]
        public bool? Warmup { get; init; }
    }
}
