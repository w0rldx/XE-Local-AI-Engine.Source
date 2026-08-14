namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed class BenchmarkJudgeExecutor(
    IBenchmarkStore store,
    IBenchmarkRuntimeSnapshotFactory snapshots,
    IBenchmarkInstalledModelLeaseProvider installedModels,
    ICapacityService capacity,
    ILocalChatRuntimePackageBuilder packageBuilder,
    IWorkerEventDispatcher dispatcher,
    IInvocationRunner runner,
    IBenchmarkEventBuffer events,
    IBenchmarkCancellationRegistry cancellations,
    ILogger<BenchmarkJudgeExecutor> logger) : IBenchmarkJudgeExecutor
{
    private const string FingerprintChangedMessage = "The installed judge model changed after the benchmark was created.";
    private const string CapacityRejectedMessage = "The judge could not reserve enough local model capacity.";
    private const string InvocationFailedMessage = "The benchmark judge invocation failed. See local logs for details.";
    private const string InvalidResultMessage = "The benchmark judge returned an invalid result.";

    public async Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Kind != BenchmarkWorkKind.Judge)
        {
            throw new ArgumentException("Judge executor received non-judge work.", nameof(work));
        }

        using var registration = cancellations.Register(work.RunId, BenchmarkWorkKind.Judge, cancellationToken);
        var token = registration.Token;
        try
        {
            events.BeginActivePhase(work.RunId, work.Run.LastStreamSequence);
            var snapshot = snapshots.Deserialize(work.Run.RuntimeSnapshotJson.Span);
            var judge = snapshot.Judge;
            var judgeModel = judge.Enabled && judge.Model is not null && judge.RequestedContextTokens is > 0
                ? judge.Model
                : throw new BenchmarkExecutionException("The frozen judge configuration is invalid.");
            if (work.Run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded || work.Run.OutputPartsJson is not { } output)
            {
                throw new BenchmarkExecutionException("The primary benchmark result is unavailable for judging.");
            }

            await using var modelLease = await installedModels.AcquireAsync(judgeModel.ModelName, token).ConfigureAwait(false);
            if (!BenchmarkSnapshotModelComparer.Matches(judgeModel, modelLease.Snapshot))
            {
                throw new BenchmarkExecutionException(FingerprintChangedMessage);
            }

            var requiredContext = judge.RequestedContextTokens.Value;
            var decision = await capacity.DecideAsync(new CapacityRequest(judgeModel.ModelName, ModelRole.Chat, requiredContext), token).ConfigureAwait(false);
            if (decision.Verdict == CapacityVerdict.RejectInsufficient)
            {
                throw new BenchmarkExecutionException(CapacityRejectedMessage);
            }

            using var reservation = decision.Reservation;
            var package = BuildJudgePackage(snapshot, judgeModel, BenchmarkExecutionSerialization.DeserializeParts(output.Span));
            var admission = new BenchmarkContextAdmissionPolicy(requiredContext);
            using var capture = new BenchmarkInvocationCapture(work.RunId, package.InvocationId, dispatcher, events);
            events.Append(work.RunId,
                BenchmarkRunStreamEventKind.JudgeState,
                new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Running.ToString()));

            await using var assignment = await dispatcher.ReportInvocationAssignedAsync(package, token).ConfigureAwait(false);
            using var context = InvocationExecutionContext.CreatePlain(package,
                Guid.Empty,
                generationAdmissionPolicy: admission);
            await runner.RunAsync(context, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var terminal = capture.TerminalState;
            if (terminal?.Status != InvocationStatus.Completed)
            {
                throw new BenchmarkExecutionException(InvocationFailedMessage);
            }

            var parsed = ParseResult(terminal.StreamedContent, judgeModel.ModelContentFingerprint, judge.PromptVersion);
            var terminalEvent = events.Reserve(work.RunId,
                BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
                new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Succeeded.ToString(), RunVersion: work.Run.Version + 1));
            var persisted = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(work.RunId,
                    work.Run.Version,
                    BenchmarkExecutionSerialization.SerializeJudge(parsed),
                    terminalEvent.Sequence), CancellationToken.None)
                .ConfigureAwait(false);
            events.PublishReserved(terminalEvent with { Payload = terminalEvent.Payload with { RunVersion = persisted.Version } });
            events.EvictPlaintext(work.RunId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            events.EvictPlaintext(work.RunId);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminalizeCancelledAsync(work.RunId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Benchmark judge work {RunId} failed.", work.RunId);
            await TerminalizeFailedAsync(work.RunId, exception is BenchmarkExecutionException safe ? safe.Message : InvocationFailedMessage).ConfigureAwait(false);
        }
    }

    private RuntimePackage BuildJudgePackage(BenchmarkRuntimeSnapshotV1 snapshot,
        BenchmarkInstalledModelSnapshotV1 judgeModel,
        IReadOnlyList<BenchmarkOutputPart> output)
    {
        var promptPayload = JsonSerializer.Serialize(new { task = snapshot.CoreTask, primaryOutputParts = output });
        return packageBuilder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            BuildJudgeSystemPrompt(snapshot.Judge.PromptVersion, snapshot.Judge.OutputSchemaVersion),
            [new ConversationMessageDto
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = promptPayload,
                SortOrder = 0
            }],
            judgeModel.ModelName,
            AgentDefinitionVersion: 1,
            ClientNodeId: LocalChatLoopbackDefaults.ClientNodeId,
            AllowedTools: [],
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            SamplingOptions: new SamplingOptions
            {
                NumCtx = snapshot.Judge.RequestedContextTokens,
                Temperature = 0,
                Seed = "0"
            },
            IsUnattended: true));
    }

    private static string BuildJudgeSystemPrompt(int promptVersion, int outputSchemaVersion) =>
        $"You are benchmark judge prompt version {promptVersion}. Evaluate only the supplied task and primary output. "
        + $"Return exactly one JSON object for schema version {outputSchemaVersion} with properties schemaVersion, score, and rationale. "
        + "schemaVersion must be 1, score must be an integer from 1 through 5, and rationale must be a non-empty string. Return no markdown or extra properties.";

    internal static BenchmarkJudgeResultV1 ParseResult(string content, string fingerprint, int promptVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 3
                || !properties.Select(static property => property.Name).ToHashSet(StringComparer.Ordinal)
                              .SetEquals(["schemaVersion", "score", "rationale"])
                || !root.TryGetProperty("schemaVersion", out var schemaElement)
                || !schemaElement.TryGetInt32(out var schemaVersion)
                || schemaVersion != 1
                || !root.TryGetProperty("score", out var scoreElement)
                || !scoreElement.TryGetInt32(out var score)
                || score is < 1 or > 5
                || !root.TryGetProperty("rationale", out var rationaleElement)
                || rationaleElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(rationaleElement.GetString()))
            {
                throw new JsonException();
            }

            var rationale = rationaleElement.GetString()!.Trim();
            if (rationale.Length > 8192)
            {
                throw new JsonException();
            }

            return new BenchmarkJudgeResultV1(1, score, rationale, fingerprint, promptVersion);
        }
        catch (JsonException exception)
        {
            throw new BenchmarkExecutionException(InvalidResultMessage) { Source = exception.Source };
        }
    }

    private async Task TerminalizeCancelledAsync(Guid runId)
    {
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null || run.JudgeStatus == BenchmarkJudgeStatus.Cancelled)
        {
            events.EvictPlaintext(runId);
            return;
        }

        if (run.JudgeStatus != BenchmarkJudgeStatus.Running)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Cancelled.ToString(), RunVersion: run.Version + 1));
        var persisted = await store.MarkJudgeCancelledAsync(runId, run.Version, terminal.Sequence, CancellationToken.None).ConfigureAwait(false);
        events.PublishReserved(terminal with { Payload = terminal.Payload with { RunVersion = persisted.Version } });
        events.EvictPlaintext(runId);
    }

    private async Task TerminalizeFailedAsync(Guid runId, string message)
    {
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null || run.JudgeStatus is BenchmarkJudgeStatus.Succeeded or BenchmarkJudgeStatus.Failed or BenchmarkJudgeStatus.Cancelled)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Failed.ToString(), RunVersion: run.Version + 1));
        var persisted = await store.MarkJudgeFailedAsync(runId, run.Version, message, terminal.Sequence, CancellationToken.None).ConfigureAwait(false);
        events.PublishReserved(terminal with { Payload = terminal.Payload with { RunVersion = persisted.Version } });
        events.EvictPlaintext(runId);
    }
}
