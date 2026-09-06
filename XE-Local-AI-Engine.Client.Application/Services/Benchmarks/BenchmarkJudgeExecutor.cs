namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Runs one judge attempt: the rubric policy the attempt was enqueued under, the judge runtime frozen onto that
///     attempt, and the durable evidence — receipt, environment facts and the rank-cohort key — recorded against the
///     attempt rather than the run, because a run is judged many times.
/// </summary>
public sealed class BenchmarkJudgeExecutor(
    IBenchmarkStore store,
    IBenchmarkRuntimeSnapshotFactory snapshots,
    IBenchmarkInstalledModelLeaseProvider installedModels,
    ICapacityService capacity,
    ILocalChatRuntimePackageBuilder packageBuilder,
    IWorkerEventDispatcher dispatcher,
    IInvocationRunner runner,
    ILlamaServerProcessSupervisor supervisor,
    IGpuVariantSelector variantSelector,
    ILlamaServerEndpointBinding endpointBinding,
    IBenchmarkEventBuffer events,
    IBenchmarkCancellationRegistry cancellations,
    IRuntimeEnvironmentFactsProvider environmentFacts,
    BenchmarkAdmissionRetry admissionRetry,
    IBenchmarkPythonTestsVerifier pythonTests,
    ILogger<BenchmarkJudgeExecutor> logger) : IBenchmarkJudgeExecutor
{
    private const string FingerprintChangedMessage = "The installed judge model changed after the benchmark was created.";
    private const string CapacityRejectedMessage = "The judge could not reserve enough local model capacity.";
    private const string InvocationFailedMessage = "The benchmark judge invocation failed. See local logs for details.";

    /// <summary>
    ///     Refusal for a revision this build no longer judges under. Names the fix, because the operator's only route
    ///     out is re-saving the judge — which mints a new revision under the current versions and re-judges.
    /// </summary>
    internal const string OutdatedPolicyVersionMessage =
        "The judge policy was stored under an older judge version. Re-save the judge to upgrade it (this forces a re-judge).";

    /// <summary>
    ///     The judge turn's constrained-decoding schema, parsed once. Cloned out of its document because a
    ///     <see cref="JsonElement" /> does not outlive the <see cref="JsonDocument" /> it was read from.
    /// </summary>
    private static readonly JsonElement JudgeResponseFormatSchema = ParseJudgeResponseFormatSchema();

    private static JsonElement ParseJudgeResponseFormatSchema()
    {
        using var document = JsonDocument.Parse(BenchmarkJudgeOutputSchemaV2.ResponseFormatJson);
        return document.RootElement.Clone();
    }

    public async Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Kind != BenchmarkWorkKind.Judge)
        {
            throw new ArgumentException("Judge executor received non-judge work.", nameof(work));
        }

        if (work.JudgeAttemptId is not { } attemptId)
        {
            throw new ArgumentException("Judge work must name the attempt it judges.", nameof(work));
        }

        using var registration = cancellations.Register(work.RunId, BenchmarkWorkKind.Judge, cancellationToken);
        var token = registration.Token;
        RuntimeEnvironmentFactsV1? environment = null;
        BenchmarkJudgeAttemptRecord? attempt = null;
        string? policyHash = null;
        try
        {
            events.BeginActivePhase(work.RunId, work.Run.LastStreamSequence);
            attempt = await store.GetJudgeAttemptAsync(attemptId, token).ConfigureAwait(false)
                      ?? throw new BenchmarkExecutionException("The judge attempt is no longer available.");
            // D14: an attempt frozen under a different launch-identity scheme is failed before it launches.
            BenchmarkLaunchIdentityScheme.RequireCurrent(attempt.LaunchIntent);
            var revision = await store.GetJudgePolicyRevisionAsync(attempt.PolicyRevisionId, token).ConfigureAwait(false)
                           ?? throw new BenchmarkExecutionException("The judge policy revision is no longer available.");
            policyHash = revision.PolicyHash;
            var policy = BenchmarkJudgeSerialization.DeserializePolicy(revision.PolicyJson!.Value.Span);

            // Fail closed on a revision this build no longer judges under. READS are deliberately tolerant of an
            // outdated version so the project stays open and re-savable; EXECUTION is not. Judging under the current
            // prompt while the revision promises an older one would file the verdict in the same cohort as verdicts
            // taken under the old wording — exactly what the version exists to prevent.
            if (!BenchmarkJudgePolicyValidator.VersionsAreCurrent(policy))
            {
                throw new BenchmarkExecutionException(OutdatedPolicyVersionMessage);
            }

            // The rubric is the question; a task item is one instance of it. A suite whose items all had to share one
            // expected answer could only ask one question, so an item may override the policy's reference answer and
            // any criterion's verifier config. The generated long-context cases are exactly that shape: one `exact`
            // criterion in the rubric, one expected passcode per case.
            policy = await ApplyItemOverridesAsync(policy, work.Run, token).ConfigureAwait(false);

            var runtime = attempt.JudgeRuntimeJson is { } runtimeJson
                ? BenchmarkJudgeSerialization.DeserializeRuntime(runtimeJson.Span)
                : throw new BenchmarkExecutionException("The frozen judge runtime is unavailable.");

            var snapshot = snapshots.Deserialize(work.Run.RuntimeSnapshotJson.Span);
            if (work.Run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded || work.Run.OutputPartsJson is not { } output)
            {
                throw new BenchmarkExecutionException("The primary benchmark result is unavailable for judging.");
            }

            // Verifiable criteria are decided HERE — before the model lease, before capacity admission, before any
            // spawn — because a rubric that needs no model must cost no GPU. A verifier that cannot run throws and
            // fails the attempt; it never contributes a 0, which is a score an answer can genuinely earn.
            var graded = BenchmarkOutputParts.ForJudge(BenchmarkExecutionSerialization.DeserializeParts(output.Span),
                Math.Min(runtime.RequestedContextTokens, runtime.Runtime.ContextTokens));
            var verifiable = policy.Rubric.Criteria.Where(static criterion => BenchmarkJudgeCriterionKinds.IsVerifiable(criterion.Kind)).ToArray();
            var answerText = verifiable.Length == 0 ? string.Empty : BenchmarkJudgeVerifiers.AnswerText(graded);
            List<BenchmarkJudgeVerifierResultV1> verified = [];
            foreach (var criterion in verifiable)
            {
                // Two paths, because only one of them can be unscorable. A pure verifier is a function of the answer
                // text; pythonTests runs the answer's code in the compute sandbox, so it is async and it refuses —
                // fail-closed, with the run left unranked — on a host that cannot be trusted to run it.
                verified.Add(BenchmarkJudgeCriterionKinds.IsExecutionVerified(criterion.Kind)
                    ? await pythonTests.VerifyAsync(criterion, answerText, token).ConfigureAwait(false)
                    : BenchmarkJudgeVerifiers.Verify(criterion, answerText));
            }

            IReadOnlyList<BenchmarkJudgeVerifierResultV1> verifierResults = verified;
            if (verifiable.Length == policy.Rubric.Criteria.Count)
            {
                await CompleteVerifiedAsync(work, policy, verifierResults, token).ConfigureAwait(false);
                return;
            }

            // Mixed rubric: the model is shown ONLY its own criteria, because BenchmarkJudgeResultParser.ReadCriteria
            // demands the array length match the rubric it parses against. The verified scores are merged back and
            // BenchmarkJudgeScoreCalculator.Compute re-checks the union against the FULL rubric, so the merge is
            // checked rather than trusted.
            var judgedPolicy = policy with
            {
                Rubric = policy.Rubric with
                {
                    Criteria = [.. policy.Rubric.Criteria.Where(static criterion => !BenchmarkJudgeCriterionKinds.IsVerifiable(criterion.Kind))]
                }
            };

            await using var modelLease = await installedModels.AcquireAsync(runtime.Model.ModelName, token).ConfigureAwait(false);
            if (!BenchmarkSnapshotModelComparer.Matches(runtime.Model, modelLease.Snapshot))
            {
                throw new BenchmarkExecutionException(FingerprintChangedMessage);
            }

            // The host facts this judging ran on, captured before anything is reserved or spawned. Non-throwing.
            environment = await environmentFacts.CaptureAsync(runtime.Runtime.Variant, token).ConfigureAwait(false);

            // Admission sizes against the frozen judge runtime's own context and its own frozen KV-cache type (null ⇒
            // f16), not the project's request.
            // No launch admission — see BenchmarkRunExecutor: the judge spawns its own process from frozen arguments.
            // A rejection is transient by nature — it means something holds the bytes RIGHT NOW — and the judge is
            // dequeued by the SAME FIFO consumer that just ran the primary, so it routinely arrives while the primary's
            // llama-server is still handing its VRAM back. Wait and re-decide instead of terminalizing the attempt.
            // ONE budget for the whole phase — see BenchmarkWaitBudget: the capacity wait and the exclusive-spawn
            // wait after it share this allowance rather than each taking a full one.
            var waitBudget = new BenchmarkWaitBudget(admissionRetry);
            var decision = await BenchmarkCapacityAdmission.AdmitAsync(capacity,
                                                               new CapacityRequest(runtime.Model.ModelName,
                                                                   ModelRole.Chat,
                                                                   runtime.Runtime.ContextTokens,
                                                                   PublishLaunchAdmission: false,
                                                                   runtime.Runtime.KvTypeK),
                                                               new BenchmarkAdmissionContext(work.RunId,
                                                                   "judge",
                                                                   runtime.RequestedContextTokens,
                                                                   runtime.Runtime.KvTypeK ?? BenchmarkKvCacheType.F16,
                                                                   CapacityRejectedMessage),
                                                               waitBudget,
                                                               logger,
                                                               token)
                                                           .ConfigureAwait(false);

            using var reservation = decision.Reservation;
            // Truncation and the silent-incomplete beside it are read through the shared predicates, not local copies:
            // the judging still runs — a truncated answer is a real answer that scored badly, and an absent one is a
            // real result too — but both the payload and the system prompt say which it is, and ranking must exclude
            // exactly the runs the judge was told about.
            var package = BuildJudgePackage(snapshot, judgedPolicy, runtime, graded,
                BenchmarkPrimaryStopReasons.IsTruncated(work.Run.PrimaryStopReason),
                BenchmarkPrimaryStopReasons.IsIncomplete(work.Run.PrimaryStopReason));
            var admission = new BenchmarkContextAdmissionPolicy(runtime.RequestedContextTokens);
            using var capture = new BenchmarkInvocationCapture(work.RunId, package.InvocationId, dispatcher, events);
            events.Append(work.RunId,
                BenchmarkRunStreamEventKind.JudgeState,
                new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Running));

            var currentVariant = await variantSelector.SelectVariantAsync(token).ConfigureAwait(false);
            if (currentVariant != runtime.Runtime.Variant)
            {
                throw new BenchmarkExecutionException("The selected judge llama.cpp runtime changed after the attempt was enqueued.");
            }

            var judgingAttempt = attempt;
            var judgingPolicyHash = policyHash;
            // A refused pre-spawn eviction is transient — the model is serving a request that ends on its own — so the
            // spawn waits and retries rather than terminalizing this attempt. See BenchmarkExclusiveSpawn.
            _ = await BenchmarkExclusiveSpawn.RunAsync(spawnToken =>
                                                     supervisor.RunExclusiveBenchmarkAsync(runtime.Model.ModelName,
                                                         ModelRole.Chat,
                                                         runtime.Runtime.ToResolvedLaunchArguments(),
                                                         runtime.Runtime.LaunchPolicy,
                                                         async (profiling, profilingToken) =>
                                                         {
                                                             // Durable BEFORE any token is generated — including on an attempt an operator
                                                             // cancellation has already terminalized (the successor-version clause).
                                                             await CheckpointAsync(work, judgingAttempt, judgingPolicyHash, profiling.LaunchReceipt, environment).ConfigureAwait(false);
                                                             using var endpointScope = endpointBinding.Bind(profiling.Endpoint);
                                                             await using var assignment = await dispatcher.ReportInvocationAssignedAsync(package, profilingToken).ConfigureAwait(false);
                                                             using var context = InvocationExecutionContext.CreatePlain(package,
                                                                 Guid.Empty,
                                                                 generationAdmissionPolicy: admission);
                                                             await runner.RunAsync(context, profilingToken).ConfigureAwait(false);
                                                             return true;
                                                         },
                                                         spawnToken),
                                                 waitBudget,
                                                 work.RunId,
                                                 "judge",
                                                 logger,
                                                 token)
                                             .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var terminal = capture.TerminalState;
            if (terminal?.Status != InvocationStatus.Completed)
            {
                throw new BenchmarkExecutionException(InvocationFailedMessage);
            }

            // Fail-closed parse against the attempt's own rubric, then the SERVER computes 0..100 — the judge only ever
            // scores individual criteria, so a model cannot hand itself an overall.
            var parsed = Merge(BenchmarkJudgeResultParser.Parse(terminal.StreamedContent, judgedPolicy.Rubric, runtime.Model.ModelContentFingerprint),
                policy.Rubric,
                verifierResults);
            var terminalEvent = events.Reserve(work.RunId,
                BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
                new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Succeeded, RunVersion: work.Run.Version + 1));
            var persisted = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(work.RunId,
                                           work.Version,
                                           BenchmarkJudgeSerialization.SerializeResult(parsed),
                                           terminalEvent.Sequence,
                                           parsed.Score), CancellationToken.None)
                                       .ConfigureAwait(false);
            events.PublishReserved(terminalEvent with
            {
                Payload = terminalEvent.Payload with
                {
                    RunVersion = persisted.Version
                }
            });
            events.EvictPlaintext(work.RunId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            events.EvictPlaintext(work.RunId);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminalizeCancelledAsync(work, attempt, policyHash, environment).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Benchmark judge work {RunId} failed.", work.RunId);
            await TerminalizeFailedAsync(work,
                attempt,
                policyHash,
                exception is BenchmarkExecutionException or BenchmarkSnapshotException or LlamaRuntimeException
                    ? exception.Message
                    : InvocationFailedMessage,
                environment).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The policy as THIS run's task item asks it: each criterion's verifier config resolved as item override ??
    ///     policy config, and the reference answer likewise.
    ///     <para>
    ///         Deliberately not a policy-hash change. The override lives on the item, so it is inside the item's input
    ///         hash and inside the project's item-set hash — which is what unranks the stale answers to it — and
    ///         moving the POLICY hash instead would force a project-wide re-judge of every item that did not change.
    ///     </para>
    /// </summary>
    private async Task<BenchmarkJudgePolicyV1> ApplyItemOverridesAsync(BenchmarkJudgePolicyV1 policy, BenchmarkRunRecord run, CancellationToken cancellationToken)
    {
        if (run.TaskItemId is not { } itemId)
        {
            return policy;
        }

        var items = await store.ListTaskItemsAsync(run.ProjectId, cancellationToken).ConfigureAwait(false);
        var item = items.FirstOrDefault(entry => entry.Id == itemId);
        if (item is null)
        {
            // The item was deleted after the run was frozen. The ranking read already excludes such a run as
            // item-set-revised, so judging it under the policy's own configuration costs nothing and refusing would
            // turn a stale row into a failed attempt.
            return policy;
        }

        var referenceAnswer = BenchmarkTaskItemService.DecodeOptional(item.ReferenceAnswerJson) ?? policy.ReferenceAnswer;
        var overrides = ReadVerifierOverrides(item);

        // An override that matches no criterion is not a no-op. Applying nothing and grading on leaves this item
        // measured against the POLICY's expected answer — another item's question — and the score would look like any
        // other. The item write refuses this, so reaching it means the rubric moved afterwards; the run is left
        // unranked under its own reason rather than scored.
        // An override that matches no criterion is not a no-op. Applying nothing and grading on leaves this item
        // measured against the POLICY's expected answer — another item's question — and the score would look like any
        // other. The item write refuses this, so reaching it means the rubric moved afterwards; the run is left
        // unranked under its own reason rather than scored.
        if (overrides.Keys.FirstOrDefault(id => !policy.Rubric.Criteria.Any(criterion => string.Equals(criterion.Id, id, StringComparison.Ordinal)))
            is { } unmatched)
        {
            throw new BenchmarkExecutionException(BenchmarkRunJudgeStates.OverrideUnmatchedPrefix
                                                  + $"The task item's verifier override names criterion '{unmatched}', which the judge rubric does not have. "
                                                  + "Edit the item's override or restore the criterion, then re-judge.");
        }

        var criteria = overrides.Count == 0
            ? policy.Rubric.Criteria
            :
            [
                .. policy.Rubric.Criteria.Select(criterion => overrides.TryGetValue(criterion.Id, out var config)
                    ? criterion with
                    {
                        Config = config
                    }
                    : criterion)
            ];

        return policy with
        {
            ReferenceAnswer = referenceAnswer,
            Rubric = policy.Rubric with
            {
                Criteria = criteria
            }
        };
    }

    /// <summary>
    ///     One item's <c>{criterionId: config}</c> overrides, as raw JSON per criterion — read through
    ///     <see cref="BenchmarkTaskItemService.ReadOverrides" />, the same call the item write validates with, so the
    ///     two cannot disagree about what an override is.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadVerifierOverrides(BenchmarkTaskItemRecord item)
    {
        if (item.VerifierConfigJson is not { IsEmpty: false } payload)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return BenchmarkTaskItemService.ReadOverrides(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or BenchmarkValidationException)
        {
            // Fail the attempt rather than judge under the policy's configuration: silently ignoring the override
            // would grade this item against another item's expected answer and call the result a score.
            throw new BenchmarkExecutionException($"The task item's verifier override is not valid JSON: {exception.Message}")
            {
                Source = exception.Source
            };
        }
    }

    /// <param name="graded">
    ///     The stored transcript reduced to its visible answer (see <see cref="BenchmarkOutputParts.ForJudge" />) and
    ///     bounded against the frozen judge window — the raw per-delta transcript of a thinking model does not fit it.
    ///     Computed by the caller so the verifiers and the model grade byte-identical text.
    /// </param>
    private RuntimePackage BuildJudgePackage(BenchmarkRuntimeSnapshotV1 snapshot,
        BenchmarkJudgePolicyV1 policy,
        BenchmarkJudgeRuntimeV1 runtime,
        IReadOnlyList<BenchmarkOutputPart> graded,
        bool primaryOutputTruncated,
        bool primaryOutputIncomplete)
    {
        var promptPayload = BenchmarkJudgePromptV2.BuildUserPayloadJson(JsonSerializer.Serialize(snapshot.CoreTask),
            policy.ReferenceAnswer,
            policy.Rubric,
            Encoding.UTF8.GetString(BenchmarkExecutionSerialization.SerializeParts(graded)),
            BenchmarkJudgeOutputSchemaV2.Json,
            primaryOutputTruncated,
            primaryOutputIncomplete);
        return packageBuilder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            BenchmarkJudgePromptV2.SystemPromptFor(primaryOutputTruncated, primaryOutputIncomplete),
            [
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = promptPayload,
                    SortOrder = 0
                }
            ],
            runtime.Model.ModelName,
            AgentDefinitionVersion: 1,
            ClientNodeId: LocalChatLoopbackDefaults.ClientNodeId,
            AllowedTools: [],
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            Timeouts: BenchmarkFrozenPolicies.FrozenTimeouts(),
            SamplingOptions: BenchmarkRunExecutor.ToSamplingOptions(runtime.Sampling, runtime.RequestedContextTokens),
            IsUnattended: true,
            // The prompt ASKS for this shape and the parser refuses anything else, which cost one judge invocation in
            // three against a small model. Constraining the decode makes the two agree instead of hoping they do; the
            // response-format schema drops the string-length bounds the parser still enforces (see the constant).
            ResponseJsonSchema: JudgeResponseFormatSchema));
    }

    /// <summary>
    ///     Terminalizes a judging every one of whose criteria was decided server-side. Nothing is leased, admitted or
    ///     spawned, so the attempt carries no launch receipt and no measured execution identity — it gets the
    ///     <see cref="BenchmarkJudgeExecutionKey.VerifiedSentinel" /> instead, which joins its cohort deterministically.
    /// </summary>
    private async Task CompleteVerifiedAsync(BenchmarkClaimedWork work,
        BenchmarkJudgePolicyV1 policy,
        IReadOnlyList<BenchmarkJudgeVerifierResultV1> verifiers,
        CancellationToken token)
    {
        events.Append(work.RunId,
            BenchmarkRunStreamEventKind.JudgeState,
            new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Running));
        token.ThrowIfCancellationRequested();
        var passed = verifiers.Count(static verifier => verifier.Passed);
        var result = Merge(new BenchmarkJudgeResultV2(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
                [],
                $"{passed} of {verifiers.Count} verifiable criteria passed. No judge model was run.",
                Score: 0,
                policy.Model.ModelContentFingerprint),
            policy.Rubric,
            verifiers);
        var terminalEvent = events.Reserve(work.RunId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Succeeded, RunVersion: work.Run.Version + 1));
        var persisted = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(work.RunId,
                                       work.Version,
                                       BenchmarkJudgeSerialization.SerializeResult(result),
                                       terminalEvent.Sequence,
                                       result.Score,
                                       BenchmarkJudgeExecutionKey.VerifiedSentinel), CancellationToken.None)
                                   .ConfigureAwait(false);
        events.PublishReserved(terminalEvent with
        {
            Payload = terminalEvent.Payload with
            {
                RunVersion = persisted.Version
            }
        });
        events.EvictPlaintext(work.RunId);
    }

    /// <summary>
    ///     Folds the verified criteria into whatever the model scored and recomputes the 0..100 against the FULL
    ///     rubric. A verified criterion is worth 10 or 0; the rubric's own weights do the rest, through the same
    ///     calculator a purely model-judged rubric goes through — which also rejects a merge that does not cover the
    ///     rubric exactly.
    /// </summary>
    private static BenchmarkJudgeResultV2 Merge(BenchmarkJudgeResultV2 judged,
        BenchmarkJudgeRubricV1 fullRubric,
        IReadOnlyList<BenchmarkJudgeVerifierResultV1> verifiers)
    {
        if (verifiers.Count == 0)
        {
            return judged;
        }

        List<BenchmarkJudgeCriterionScoreV2> merged = [.. judged.Criteria];
        merged.AddRange(verifiers.Select(static verifier => new BenchmarkJudgeCriterionScoreV2(verifier.Id,
            verifier.Passed ? BenchmarkJudgeVerifiers.PassScore : BenchmarkJudgeVerifiers.FailScore,
            verifier.Detail)));
        return judged with
        {
            Criteria = merged,
            Score = BenchmarkJudgeScoreCalculator.Compute(fullRubric, merged),
            Verifiers = verifiers
        };
    }

    /// <summary>
    ///     Writes the attempt's launch evidence and, in the same insert-if-null write, the rank-cohort key derived from
    ///     it. The key is computed fail-closed: an execution this node cannot fully describe gets no key, and the
    ///     attempt stays permanently unranked rather than joining a cohort it cannot be shown to belong to.
    /// </summary>
    private async Task CheckpointAsync(BenchmarkClaimedWork work,
        BenchmarkJudgeAttemptRecord? attempt,
        string? policyHash,
        LlamaServerLaunchReceipt? receipt,
        RuntimeEnvironmentFactsV1? environment)
    {
        if (attempt is null || policyHash is null)
        {
            return;
        }

        var command = BenchmarkLaunchEvidence.TryBuild(receipt,
            environment,
            attempt.LaunchIntent?.KvCacheTypeSource ?? BenchmarkKvCacheType.SourceAuto);
        if (command is null)
        {
            return;
        }

        try
        {
            _ = await store.MarkJudgeLaunchReadyAsync(attempt.Id,
                               work.QueueSequence,
                               work.Version,
                               command,
                               BenchmarkJudgeExecutionKey.TryCompute(policyHash, receipt, environment),
                               CancellationToken.None)
                           .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Evidence is not worth a measurement. A version race with an operator cancel, or a busy database, must
            // not turn a healthy judging into a failed one — and the token here is None, so nothing caught is a shutdown.
            logger.LogWarning(exception, "Benchmark judge {RunId}: the launch-evidence checkpoint could not be recorded.", work.RunId);
        }
    }

    private async Task TerminalizeCancelledAsync(BenchmarkClaimedWork work,
        BenchmarkJudgeAttemptRecord? attempt,
        string? policyHash,
        RuntimeEnvironmentFactsV1? environment)
    {
        await CheckpointAsync(work, attempt, policyHash, receipt: null, environment).ConfigureAwait(false);
        var runId = work.RunId;
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null || run.Judge?.State != BenchmarkRunJudgeStates.Running)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Cancelled, RunVersion: run.Version + 1));
        try
        {
            var persisted = await store.MarkJudgeCancelledAsync(runId, work.Version, terminal.Sequence, CancellationToken.None).ConfigureAwait(false);
            events.PublishReserved(terminal with
            {
                Payload = terminal.Payload with
                {
                    RunVersion = persisted.Version
                }
            });
        }
        catch (BenchmarkConflictException)
        {
            var current = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
            if (current?.Judge?.State != BenchmarkRunJudgeStates.Cancelled)
            {
                throw;
            }
        }

        events.EvictPlaintext(runId);
    }

    private async Task TerminalizeFailedAsync(BenchmarkClaimedWork work,
        BenchmarkJudgeAttemptRecord? attempt,
        string? policyHash,
        string message,
        RuntimeEnvironmentFactsV1? environment)
    {
        await CheckpointAsync(work, attempt, policyHash, receipt: null, environment).ConfigureAwait(false);
        var runId = work.RunId;
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null
            || run.Judge?.State is BenchmarkRunJudgeStates.Succeeded or BenchmarkRunJudgeStates.Failed or BenchmarkRunJudgeStates.Cancelled)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Failed, RunVersion: run.Version + 1));
        var persisted = await store.MarkJudgeFailedAsync(runId, work.Version, message, terminal.Sequence, CancellationToken.None).ConfigureAwait(false);
        events.PublishReserved(terminal with
        {
            Payload = terminal.Payload with
            {
                RunVersion = persisted.Version
            }
        });
        events.EvictPlaintext(runId);
    }
}
