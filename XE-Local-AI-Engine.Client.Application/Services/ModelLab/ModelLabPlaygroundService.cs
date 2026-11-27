namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Client.Services.Training.Runtime;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Trains the training stepper's tiny network through <c>nnplayground.py</c> inside the request, then saves the run and its recorded trace.</summary>
/// <remarks>
///     CPU only: the script hides CUDA before torch loads, so the run takes no GPU gate, lease or ledger and never waits on a loaded model. One run
///     at a time, refused when busy, under a two-minute watchdog, registered as a runtime user while its script runs. Only a clean script run whose
///     outputs pass their checks is saved; anything else saves nothing.
/// </remarks>
public sealed class ModelLabPlaygroundService
{
    public const string UnsupportedPlatformMessage =
        "The training stepper runs on Linux x64 only for now, because it needs the Python training runtime, which is available on Linux x64 only.";

    private const string NotInstalledMessage = "The Python training runtime is not installed. Install it on the Training page, then start the run again.";
    private const string TraceUnavailableMessage = "The saved trace of this run is missing or failed its integrity check. Delete the run and train it again.";

    private static readonly TimeSpan MaxJobDuration = TimeSpan.FromMinutes(2);

    private readonly ManagedEncryptedBlobStore _blobs;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ILogger<ModelLabPlaygroundService> _logger;
    private readonly ITrainingRuntimeService _runtime;
    private readonly TrainingRuntimeUsers _runtimeUsers;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModelLabScriptRunner _scripts;

    /// <summary>1 while a run is in flight: single-flight, refused when busy, with no queue.</summary>
    private int _running;

    public ModelLabPlaygroundService(IServiceScopeFactory scopeFactory,
        ITrainingRuntimeService runtime,
        TrainingRuntimeUsers runtimeUsers,
        ITrainingProcessSpawner spawner,
        ITrainingRunEventBuffer events,
        INodeDataDirectory dataDirectory,
        INodeSqliteKeyHolder keyHolder,
        IOptions<ModelLabJobOptions> options,
        TimeProvider timeProvider,
        ILogger<ModelLabPlaygroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(keyHolder);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(runtimeUsers);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(spawner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _blobs = new ManagedEncryptedBlobStore(dataDirectory, keyHolder, "model-lab", "playground", "model_lab_playground_blob",
            ModelLabPlaygroundSpec.MaxTraceBytes, "Model Lab training stepper trace");
        _dataDirectory = dataDirectory;
        _logger = logger;
        _runtime = runtime;
        _runtimeUsers = runtimeUsers;
        _scopeFactory = scopeFactory;
        var shared = options.Value;
        var maxDuration = shared.MaxDuration < MaxJobDuration ? shared.MaxDuration : MaxJobDuration;
        _scripts = new ModelLabScriptRunner(spawner, events, new ModelLabJobOptions
        {
            MaxDuration = maxDuration,
            InactivityTimeout = shared.InactivityTimeout < maxDuration ? shared.InactivityTimeout : maxDuration,
            ExitGracePeriod = shared.ExitGracePeriod
        }, timeProvider);
    }

    /// <summary>The job is Linux x64 only, like the training runtime. A test swaps this to see the other platforms' answer.</summary>
    internal Func<bool> PlatformProbe { get; set; } = static () => TrainingScripts.IsPlatformSupported;

    /// <summary>The script a run spawns. A test points it at a stand-in file.</summary>
    internal string ScriptPath { get; set; } = Path.Combine(TrainingScripts.ResolveDirectory(), TrainingScripts.PlaygroundScriptName);

    public bool IsPlatformSupported => PlatformProbe();

    /// <summary>Trains one run and saves it; every refusal and failure saves nothing. Cancelling <paramref name="ct" /> stops the script.</summary>
    public async Task<ModelLabPlaygroundOutcome> StartAsync(ModelLabPlaygroundConfigV1 config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsPlatformSupported)
        {
            return Refuse(ModelLabPlaygroundRefusal.UnsupportedPlatform, UnsupportedPlatformMessage);
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Refuse(ModelLabPlaygroundRefusal.Busy, "Another training stepper run is in progress. Try again when it has finished.");
        }

        var id = Guid.NewGuid();
        var workDirectory = Path.Combine(ModelLabScriptRunner.WorkRoot(_dataDirectory), "playground-" + id.ToString("N"));
        try
        {
            var job = new ModelLabPlaygroundJobV1
            {
                Config = config,
                TracePath = Path.Combine(workDirectory, "trace.json"),
                ResultPath = Path.Combine(workDirectory, "result.json")
            };
            var jobJson = JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options);
            ModelLabScriptRun run;
            ModelLabRuntimeJob? runtimeJob = null;
            try
            {
                if (!ModelLabRuntimeJob.TryEnter(_runtimeUsers, _runtime, ScriptPath, NotInstalledMessage, out runtimeJob, out var refusal))
                {
                    return Refuse(ModelLabPlaygroundRefusal.RuntimeUnavailable, refusal);
                }

                run = await _scripts.RunAsync(id, workDirectory, jobJson, new ModelLabScriptCommand
                {
                    Interpreter = runtimeJob.Interpreter,
                    ScriptPath = runtimeJob.ScriptPath,
                    ContractVersion = ModelLabPlaygroundSpec.ContractVersion,
                    PublishPhases = false
                }, static _ => Task.CompletedTask, ct);
            }
            finally
            {
                // Held until the script run returns, however it ends: what follows reads only the job's own files.
                runtimeJob?.Dispose();
            }

            if (ModelLabPlaygroundChecks.RunFailureOrNull(run, ct.IsCancellationRequested) is { } failed)
            {
                return Refuse(ModelLabPlaygroundRefusal.RunFailed, failed);
            }

            if ((ModelLabPlaygroundChecks.SizeProblemOrNull(new FileInfo(job.TracePath), job.MaxTraceBytes, "trace")
                 ?? ModelLabPlaygroundChecks.SizeProblemOrNull(new FileInfo(job.ResultPath), job.MaxResultBytes, "summary")) is { } size)
            {
                return Refuse(ModelLabPlaygroundRefusal.RunFailed, size);
            }

            var trace = await File.ReadAllBytesAsync(job.TracePath, CancellationToken.None);
            var result = await File.ReadAllBytesAsync(job.ResultPath, CancellationToken.None);
            if ((ModelLabPlaygroundChecks.TraceProblemOrNull(trace) ?? ModelLabPlaygroundChecks.ResultProblemOrNull(result)) is { } shape)
            {
                return Refuse(ModelLabPlaygroundRefusal.RunFailed, shape);
            }

            if (ReadResultOrNull(result) is not { } summary)
            {
                return Refuse(ModelLabPlaygroundRefusal.RunFailed, "The training stepper script wrote a summary this build cannot read.");
            }

            var record = await SaveAsync(id, trace, result);
            return new ModelLabPlaygroundOutcome
            {
                Run = Summary(record, summary)
            };
        }
        finally
        {
            BestEffortFile.TryDeleteDirectory(workDirectory);
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Newest first.</summary>
    public async Task<IReadOnlyList<ModelLabPlaygroundRunSummary>> ListAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var records = await scope.ServiceProvider.GetRequiredService<IModelLabPlaygroundRunStore>().ListAsync(ct);
        return [.. records.Select(static record => Summary(record, StoredResult(record)))];
    }

    /// <exception cref="ModelLabPlaygroundRunNotFoundException">No run has that id.</exception>
    public async Task<ModelLabPlaygroundRunRead> GetAsync(Guid id, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var record = await scope.ServiceProvider.GetRequiredService<IModelLabPlaygroundRunStore>().GetAsync(id, ct) ?? throw new ModelLabPlaygroundRunNotFoundException(id);
        var trace = await _blobs.ReadAsync(record.Id, record.TraceBlobId, record.TraceSha256, record.TraceByteCount, ct);
        var run = Summary(record, StoredResult(record));
        return trace.Status == ManagedBlobReadStatus.Found
            ? new ModelLabPlaygroundRunRead
            {
                Run = run,
                Trace = trace.Content
            }
            : new ModelLabPlaygroundRunRead
            {
                Run = run,
                UnavailableMessage = TraceUnavailableMessage
            };
    }

    /// <exception cref="ModelLabPlaygroundRunNotFoundException">No run has that id.</exception>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        if (!await scope.ServiceProvider.GetRequiredService<IModelLabPlaygroundRunStore>().DeleteAsync(id, ct))
        {
            throw new ModelLabPlaygroundRunNotFoundException(id);
        }

        _blobs.DeleteScope(id);
    }

    /// <summary>Blob first, then the row, neither cancellable. A failed save drops the blob scope only once a fresh read shows no row holds it.</summary>
    private async Task<ModelLabPlaygroundRunRecord> SaveAsync(Guid id, byte[] trace, byte[] result)
    {
        ModelLabPlaygroundRunRecord record;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IModelLabPlaygroundRunStore>();
        try
        {
            var blobId = Guid.NewGuid();
            var blob = await _blobs.WriteAsync(id, blobId, trace, CancellationToken.None);
            record = await store.CreateAsync(new ModelLabPlaygroundRunCreate
            {
                Id = id,
                TemplateId = ModelLabPlaygroundSpec.TemplateId,
                TraceBlobId = blobId,
                TraceSha256 = blob.ContentHash,
                TraceByteCount = blob.ByteCount,
                ResultJson = result
            }, CancellationToken.None);
        }
        catch (Exception)
        {
            await DropScopeIfNoRowAsync(id);
            throw;
        }

        try
        {
            foreach (var removed in await store.TrimToNewestAsync(ModelLabPlaygroundSpec.MaxRetainedRuns, CancellationToken.None))
            {
                _blobs.DeleteScope(removed);
            }
        }
        catch (Exception exception)
        {
            // The run is saved; the next save trims again.
            _logger.LogWarning(exception, "Trimming old training stepper runs failed after saving {RunId}.", id);
        }

        return record;
    }

    /// <summary>A row that may have landed, or one that cannot be checked, keeps its scope: deleting it would leave a row naming a missing trace.</summary>
    private async Task DropScopeIfNoRowAsync(Guid id)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<IModelLabPlaygroundRunStore>().GetAsync(id, CancellationToken.None) is null)
            {
                _blobs.DeleteScope(id);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Training stepper run {RunId} could not be checked after a failed save, so its trace scope is kept.", id);
        }
    }

    private static ModelLabPlaygroundResultV1? ReadResultOrNull(byte[] result)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelLabPlaygroundResultV1>(result, TrainingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A stored result passed the checks when it was saved, so one that no longer reads is corrupt, not absent.</summary>
    private static ModelLabPlaygroundResultV1 StoredResult(ModelLabPlaygroundRunRecord record) =>
        JsonSerializer.Deserialize<ModelLabPlaygroundResultV1>(record.ResultJson.Span, TrainingJson.Options)
        ?? throw new InvalidDataException($"Training stepper run '{record.Id}' has an empty summary.");

    private static ModelLabPlaygroundRunSummary Summary(ModelLabPlaygroundRunRecord record, ModelLabPlaygroundResultV1 result) =>
        new()
        {
            Id = record.Id,
            TemplateId = record.TemplateId,
            CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAtUtc),
            TraceByteCount = record.TraceByteCount,
            Result = result
        };

    private static ModelLabPlaygroundOutcome Refuse(ModelLabPlaygroundRefusal refusal, string message) =>
        new()
        {
            Refusal = refusal,
            RefusalMessage = message
        };
}
