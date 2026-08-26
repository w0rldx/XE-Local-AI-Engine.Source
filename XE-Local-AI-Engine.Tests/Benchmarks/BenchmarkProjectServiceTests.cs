namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkProjectServiceTests
{
    private static readonly Guid ProjectId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RevisionId = new("22222222-2222-2222-2222-222222222222");

    [Test]
    public async Task Create_PreservesExactTaskPayload()
    {
        var context = new ServiceContext();

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "  exact task  ", 4096, context.AgentId));

        AssertEx.Equal("  exact task  ", BenchmarkProjectService.DecodeCoreTask(AssertEx.NotNull(context.CreatedInput).CoreTaskJson.Span));
        _ = context.Store.DidNotReceive().ActivateJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(),
            Arg.Any<BenchmarkJudgeAttemptSeed?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_RejectsUnsupportedContextAndNonSingleAgentBeforePersistence()
    {
        var context = new ServiceContext(agentKind: AgentDefinitionKind.Orchestrator);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 1234, context.AgentId)));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId)));

        _ = context.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<BenchmarkJudgePolicyChangeInput?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_OutputBudgetMustFitInsideTheRequestedContext()
    {
        var context = new ServiceContext();

        // A budget at or above the window can never be honoured — it would behave exactly like no budget, which is a
        // silently different measurement rather than an error the operator can see.
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId, MaxOutputTokens: 4096)));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId, MaxOutputTokens: 0)));
        _ = context.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<BenchmarkJudgePolicyChangeInput?>(),
            Arg.Any<CancellationToken>());

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId, MaxOutputTokens: 2048));
        AssertEx.Equal<int?>(2048, AssertEx.NotNull(context.CreatedInput).MaxOutputTokens);

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId));
        AssertEx.Null(AssertEx.NotNull(context.CreatedInput).MaxOutputTokens, "An omitted budget stays absent: generation is context-limited.");
    }

    [Test]
    public async Task Create_ReasoningBudgetMustFitTheContextAlongsideTheOutputBudget()
    {
        var context = new ServiceContext();

        // Same rule as the output budget on its own.
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId, ReasoningBudgetTokens: 4096)));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId, ReasoningBudgetTokens: 0)));

        // The pair is what actually bites: each budget fits on its own, but together with the prompt they cannot, so
        // every run this project could ever freeze would burn its window and be excluded from its own ranking.
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId, MaxOutputTokens: 2048,
                ReasoningBudgetTokens: 2000)));
        _ = context.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<BenchmarkJudgePolicyChangeInput?>(),
            Arg.Any<CancellationToken>());

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId, MaxOutputTokens: 1024,
            ReasoningBudgetTokens: 2048));
        AssertEx.Equal<int?>(2048, AssertEx.NotNull(context.CreatedInput).ReasoningBudgetTokens);

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId));
        AssertEx.Null(AssertEx.NotNull(context.CreatedInput).ReasoningBudgetTokens,
            "An omitted budget stays absent: the reasoning keeps the effort ladder's ceiling.");
    }

    [Test]
    public async Task Create_GenerationTimeoutMustBePlausible()
    {
        var context = new ServiceContext();

        // The floor stops a typo from cancelling every run before the model even warms; the ceiling stops a runaway
        // run from owning the queue for a day.
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId,
                InvocationTimeoutSeconds: 59)));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId,
                InvocationTimeoutSeconds: 7201)));
        _ = context.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<BenchmarkJudgePolicyChangeInput?>(),
            Arg.Any<CancellationToken>());

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId,
            InvocationTimeoutSeconds: 1800));
        AssertEx.Equal<int?>(1800, AssertEx.NotNull(context.CreatedInput).InvocationTimeoutSeconds);

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId));
        AssertEx.Null(AssertEx.NotNull(context.CreatedInput).InvocationTimeoutSeconds, "An omitted timeout takes the node default.");
    }

    [Test]
    public async Task Create_WithJudge_CarriesAHashedPolicyRevisionIntoTheProjectWriteItself()
    {
        var context = new ServiceContext();

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId,
            "Benchmark",
            "task",
            4096,
            context.AgentId,
            new BenchmarkJudgePolicyDraft("  judge-model  ", 8192)));

        var change = AssertEx.NotNull(context.CreatedPolicy, "The judge is part of the create, not a second transaction.");
        AssertEx.True(change.PolicyJson is not null, "The create must carry the canonical policy bytes.");
        var policy = BenchmarkJudgeSerialization.DeserializePolicy(change.PolicyJson!.Value.Span);
        AssertEx.Equal(8192, policy.RequestedContextTokens);
        AssertEx.Equal(BenchmarkJudgePolicyVersions.PromptVersion, policy.PromptVersion);
        AssertEx.Equal(BenchmarkJudgeRubricDefaults.Default().Criteria.Count, policy.Rubric.Criteria.Count);
        AssertEx.Equal(BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy), change.PolicyHash);
        _ = context.Store.DidNotReceive().ActivateJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(),
            Arg.Any<BenchmarkJudgeAttemptSeed?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_WithoutAJudge_DisablesItInTheSameProjectWrite()
    {
        var context = new ServiceContext();

        _ = await context.Service.UpdateAsync(ProjectId, 1, new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId));

        AssertEx.Equal(BenchmarkJudgePolicyChangeInput.Disabled, AssertEx.NotNull(context.UpdatedPolicy));
        _ = context.Store.DidNotReceive().DisableJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WithAnAuxiliaryAssetJudgeModel_IsRejectedBeforePersistence()
    {
        var context = new ServiceContext(judgeCarriesProjector: true);

        var exception = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId,
                "Benchmark",
                "task",
                4096,
                context.AgentId,
                new BenchmarkJudgePolicyDraft("judge-model", 4096))));

        AssertEx.True(exception.Message.Contains("auxiliary asset", StringComparison.Ordinal),
            "A judging from an aux-asset model can never be ranked, so it is refused at the policy.");
        _ = context.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<BenchmarkJudgePolicyChangeInput?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WithAMissingOrIncompleteJudge_IsRejectedBeforePersistence()
    {
        var missing = new ServiceContext(judgeModelInstalled: false);
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            missing.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, missing.AgentId, new BenchmarkJudgePolicyDraft("gone", 4096))));

        var incomplete = new ServiceContext();
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            incomplete.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, incomplete.AgentId, new BenchmarkJudgePolicyDraft("   ", 4096))));

        _ = missing.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<BenchmarkJudgePolicyChangeInput?>(), Arg.Any<CancellationToken>());
        _ = incomplete.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<BenchmarkJudgePolicyChangeInput?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_WithTheSameHash_IsANoOp()
    {
        var context = new ServiceContext();
        var draft = new BenchmarkJudgePolicyDraft("judge-model", 4096);
        context.SetCurrentRevision(await context.BuildPolicyHashAsync(draft));

        _ = await context.Service.UpdateJudgePolicyAsync(ProjectId, 1, draft, confirmRejudge: false);

        _ = context.Store.DidNotReceive().ActivateJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(),
            Arg.Any<BenchmarkJudgeAttemptSeed?>(), Arg.Any<CancellationToken>());
        _ = context.Store.DidNotReceive().EnqueueJudgeAttemptAsync(Arg.Any<BenchmarkEnqueueJudgeAttemptCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_CarriesTheFidelitySettingsAndResolvesTheBaseFingerprintServerSide()
    {
        // The fingerprint is an INPUT to the KLD comparability digest, so accepting one from the caller would let two
        // sets of numbers measured against different weights compare as if they were the same measurement. It is read
        // from the eligible-model catalog, which also proves the named base is an eligible local model at all.
        var context = new ServiceContext();

        _ = await context.Service.CreateAsync(Draft(context) with
        {
            FidelityEnabled = true,
            FidelityKldEnabled = true,
            FidelityChunks = 50,
            FidelityKldBaseModelName = $"  {ServiceContext.BaseModelName}  "
        });

        var input = AssertEx.NotNull(context.CreatedInput);
        AssertEx.True(input.FidelityEnabled);
        AssertEx.True(input.FidelityKldEnabled);
        AssertEx.Equal<int?>(50, input.FidelityChunks);
        AssertEx.Equal(ServiceContext.BaseModelName, input.FidelityKldBaseModelName);
        AssertEx.Equal(ServiceContext.BaseFingerprint, input.FidelityKldBaseFingerprint);
    }

    [Test]
    public async Task Create_WithNoFidelitySettings_PersistsNoBaseModelAndNoFingerprint()
    {
        var context = new ServiceContext();

        _ = await context.Service.CreateAsync(Draft(context));

        var input = AssertEx.NotNull(context.CreatedInput);
        AssertEx.False(input.FidelityEnabled);
        AssertEx.False(input.FidelityKldEnabled);
        AssertEx.Null(input.FidelityChunks);
        AssertEx.Null(input.FidelityKldBaseModelName);
        AssertEx.Null(input.FidelityKldBaseFingerprint);
        _ = context.Catalog.DidNotReceive().ListEligibleModelsAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WithAnUnusableFidelityConfiguration_IsRefused()
    {
        var context = new ServiceContext();

        var tooFew = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.CreateAsync(Draft(context) with
        {
            FidelityChunks = BenchmarkFidelityPolicy.MinimumChunks - 1
        }));
        var tooMany = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.CreateAsync(Draft(context) with
        {
            FidelityChunks = BenchmarkFidelityPolicy.MaximumChunks + 1
        }));
        var noBase = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.CreateAsync(Draft(context) with
        {
            FidelityEnabled = true,
            FidelityKldEnabled = true
        }));
        var unknownBase = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.CreateAsync(Draft(context) with
        {
            FidelityKldEnabled = true,
            FidelityKldBaseModelName = "not-installed.gguf"
        }));

        AssertEx.Contains(tooFew.Message, "chunk count");
        AssertEx.Contains(tooMany.Message, "chunk count");
        AssertEx.Contains(noBase.Message, "requires a base model");
        AssertEx.Contains(unknownBase.Message, "not an eligible local model");
        AssertEx.Equal(BenchmarkFidelityPolicy.MinimumChunks, 50);
        AssertEx.Equal(BenchmarkFidelityPolicy.MaximumChunks, 655);
    }

    [Test]
    public async Task Create_AtTheChunkBoundaries_IsAccepted()
    {
        var context = new ServiceContext();

        _ = await context.Service.CreateAsync(Draft(context) with
        {
            FidelityChunks = BenchmarkFidelityPolicy.MinimumChunks
        });
        _ = await context.Service.CreateAsync(Draft(context) with
        {
            FidelityChunks = BenchmarkFidelityPolicy.MaximumChunks
        });

        AssertEx.Equal<int?>(BenchmarkFidelityPolicy.MaximumChunks, AssertEx.NotNull(context.CreatedInput).FidelityChunks);
    }

    [Test]
    public async Task UpdateFidelity_ResolvesTheFingerprintValidatesAndWakesTheQueueOnlyWhenItQueuedSomething()
    {
        // Same validation as the project write, through the same resolver — a second copy is how the two paths drift
        // into disagreeing about what a valid base model is.
        var context = new ServiceContext();
        BenchmarkProjectFidelityInput? input = null;
        context.Store.UpdateProjectFidelityAsync(ProjectId, 1, Arg.Do<BenchmarkProjectFidelityInput>(value => input = value),
                   Arg.Any<bool>(), Arg.Any<CancellationToken>())
               .Returns(call => new BenchmarkProjectFidelityChange(ServiceContext.CurrentProject(), call.ArgAt<bool>(3) ? [Guid.NewGuid()] : []));

        var quiet = await context.Service.UpdateFidelityAsync(ProjectId, 1,
            new BenchmarkProjectFidelitySettings(Enabled: true, KldEnabled: true, Chunks: 50, $"  {ServiceContext.BaseModelName}  "));

        AssertEx.Empty(quiet.EnqueuedRunIds);
        AssertEx.Equal(ServiceContext.BaseModelName, AssertEx.NotNull(input).FidelityKldBaseModelName);
        AssertEx.Equal(ServiceContext.BaseFingerprint, input!.FidelityKldBaseFingerprint);
        AssertEx.Equal<int?>(50, input.FidelityChunks);

        var measured = await context.Service.UpdateFidelityAsync(ProjectId, 1,
            new BenchmarkProjectFidelitySettings(Enabled: true, KldEnabled: false, Chunks: null, null),
            measureExisting: true);

        AssertEx.Equal(1, measured.EnqueuedRunIds.Count);
        AssertEx.Null(AssertEx.NotNull(input).FidelityKldBaseFingerprint, "No base named, so nothing to resolve.");
    }

    [Test]
    public async Task UpdateFidelity_WithAnUnusableConfiguration_IsRefusedBeforeTheStoreIsTouched()
    {
        var context = new ServiceContext();

        var tooMany = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.UpdateFidelityAsync(ProjectId, 1,
            new BenchmarkProjectFidelitySettings(Enabled: true, KldEnabled: false, BenchmarkFidelityPolicy.MaximumChunks + 1, null)));
        var noBase = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.UpdateFidelityAsync(ProjectId, 1,
            new BenchmarkProjectFidelitySettings(Enabled: true, KldEnabled: true, Chunks: null, null)));
        var unknownBase = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.UpdateFidelityAsync(ProjectId, 1,
            new BenchmarkProjectFidelitySettings(Enabled: true, KldEnabled: true, Chunks: null, "not-installed.gguf")));

        AssertEx.Contains(tooMany.Message, "chunk count");
        AssertEx.Contains(noBase.Message, "requires a base model");
        AssertEx.Contains(unknownBase.Message, "not an eligible local model");
        _ = context.Store.DidNotReceiveWithAnyArgs().UpdateProjectFidelityAsync(Guid.Empty, 0, null!, false, CancellationToken.None);
    }

    private static BenchmarkProjectDraft Draft(ServiceContext context) =>
        new(ProjectId, "Benchmark", "task", 4096, context.AgentId);

    [Test]
    public async Task UpdateJudgePolicy_InPairwiseMode_ActivatesLikeAnyOtherMode()
    {
        // The mode is inside the policy hash, so switching to it mints a revision and re-judges the project — which is
        // the whole cost of the switch, and the pre-flight estimate is what puts that number in front of the operator.
        var context = new ServiceContext();

        _ = await context.Service.UpdateJudgePolicyAsync(ProjectId,
            1,
            new BenchmarkJudgePolicyDraft("judge-model", 4096, Mode: BenchmarkJudgePolicyModes.Pairwise),
            confirmRejudge: false);

        _ = context.Store.Received(1).ActivateJudgePolicyAsync(ProjectId, Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(),
            Arg.Any<BenchmarkJudgeAttemptSeed?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_WithAnAllVerifiableRubric_ActivatesAndStoresTheConfig()
    {
        var context = new ServiceContext();

        var change = await context.Service.UpdateJudgePolicyAsync(ProjectId,
            1,
            new BenchmarkJudgePolicyDraft("judge-model", 4096, BenchmarkJudgeRubricDefaults.Verifiable()),
            confirmRejudge: false);

        AssertEx.NotNull(change);
        _ = context.Store.Received(1).ActivateJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(),
            Arg.Any<BenchmarkJudgeAttemptSeed?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_WithAnUnrunnableVerifierConfig_IsRefusedAtActivation()
    {
        var context = new ServiceContext();
        var rubric = new BenchmarkJudgeRubricV1(BenchmarkJudgePolicyVersions.RubricVersion,
        [
            new BenchmarkJudgeRubricCriterionV1("c0", "Title", "Description", 100, BenchmarkJudgeCriterionKinds.Regex,
                """{"pattern":"(?=lookahead)"}""")
        ]);

        var exception = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.UpdateJudgePolicyAsync(ProjectId, 1, new BenchmarkJudgePolicyDraft("judge-model", 4096, rubric), confirmRejudge: false));

        AssertEx.Contains(exception.Message, "linear time");
    }

    [Test]
    public async Task UpdateJudgePolicy_WithADifferentHashOnAProjectWithRuns_RequiresConfirmation()
    {
        var context = new ServiceContext(runCount: 2);
        context.SetCurrentRevision("f" + new string('0', count: 63));

        var exception = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() =>
            context.Service.UpdateJudgePolicyAsync(ProjectId, 1, new BenchmarkJudgePolicyDraft("judge-model", 4096), confirmRejudge: false));

        AssertEx.Equal("RejudgeRequired", exception.Code);
        _ = context.Store.DidNotReceive().ActivateJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(),
            Arg.Any<BenchmarkJudgeAttemptSeed?>(), Arg.Any<CancellationToken>());

        // The refusal must cost nothing: taking the lease VERIFIES the model by re-hashing every member file, which
        // made this 409 take 57 s to return for a 22 GB judge.
        _ = context.Models.DidNotReceive().AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_ReSavingTheSameJudgeOnAProjectWithRuns_IsANoOpWithoutVerifyingTheModel()
    {
        // Re-saving an unchanged judge is not a change, so it must not demand the re-judge confirmation — and it must
        // reach that answer without the verifying lease, which is the 57 s this ordering exists to avoid.
        var context = new ServiceContext(runCount: 2);
        var draft = new BenchmarkJudgePolicyDraft("judge-model", 4096);
        await context.SetCurrentPolicyAsync(draft);
        context.Models.ClearReceivedCalls();

        var change = await context.Service.UpdateJudgePolicyAsync(ProjectId, 1, draft, confirmRejudge: false);

        AssertEx.Equal<int?>(1, change.CohortGeneration, "The project keeps the cohort it already had.");
        _ = context.Models.DidNotReceive().AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = context.Store.DidNotReceive().ActivateJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(),
            Arg.Any<BenchmarkJudgeAttemptSeed?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_EditingTheJudgeOnAProjectWithRuns_RequiresConfirmationWithoutVerifyingTheModel()
    {
        // The same path against a REAL stored policy, so the refusal comes from the comparison answering "different"
        // rather than from a revision it could not read.
        var context = new ServiceContext(runCount: 2);
        await context.SetCurrentPolicyAsync(new BenchmarkJudgePolicyDraft("judge-model", 4096));
        context.Models.ClearReceivedCalls();

        var exception = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() =>
            context.Service.UpdateJudgePolicyAsync(ProjectId, 1, new BenchmarkJudgePolicyDraft("judge-model", 8192), confirmRejudge: false));

        AssertEx.Equal("RejudgeRequired", exception.Code);
        _ = context.Models.DidNotReceive().AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_WhenConfirmed_ActivatesAndEnqueuesOneAttemptPerSucceededRun()
    {
        var context = new ServiceContext(runCount: 2, succeededRunIds: [Guid.NewGuid(), Guid.NewGuid()]);
        context.SetCurrentRevision("f" + new string('0', count: 63));

        var change = await context.Service.UpdateJudgePolicyAsync(ProjectId, 1, new BenchmarkJudgePolicyDraft("judge-model", 4096), confirmRejudge: true);

        AssertEx.Equal(expected: 2, change.EnqueuedRunIds.Count);
        AssertEx.True(AssertEx.NotNull(context.ActivatedSeed).RuntimeJson is not null, "A resolvable runtime is frozen onto every attempt of the cohort.");
        AssertEx.Equal(expected: 1, context.RuntimeResolutions, "The runtime depends only on the policy, so it is resolved once per activation.");
        _ = context.Store.DidNotReceive().EnqueueJudgeAttemptAsync(Arg.Any<BenchmarkEnqueueJudgeAttemptCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_WhenTheJudgeRuntimeCannotBeResolved_EnqueuesAttemptsCarryingTheReason()
    {
        var context = new ServiceContext(runCount: 1, succeededRunIds: [Guid.NewGuid()], judgeRuntimeResolves: false);
        context.SetCurrentRevision("f" + new string('0', count: 63));

        _ = await context.Service.UpdateJudgePolicyAsync(ProjectId, 1, new BenchmarkJudgePolicyDraft("judge-model", 4096), confirmRejudge: true);

        var seed = AssertEx.NotNull(context.ActivatedSeed);
        AssertEx.Null(seed.RuntimeJson, "An unresolvable runtime is a failed attempt, not a refused activation.");
        AssertEx.Equal("judge runtime is unavailable", seed.RuntimeUnresolvedReason);
    }

    [Test]
    public async Task RejudgeProject_ResolvesTheRuntimeOnceAndPinsTheRevisionItWasResolvedFor()
    {
        var context = new ServiceContext(runCount: 2, succeededRunIds: [Guid.NewGuid(), Guid.NewGuid()]);
        await context.SetCurrentPolicyAsync(new BenchmarkJudgePolicyDraft("judge-model", 4096));

        var change = await context.Service.RejudgeProjectAsync(ProjectId, 1);

        var seed = AssertEx.NotNull(context.RejudgeSeed);
        AssertEx.Equal(RevisionId, seed.ExpectedJudgePolicyRevisionId, "A revision swap between resolve and reset must roll the whole re-judge back.");
        AssertEx.True(seed.RuntimeJson is not null, "The cohort's runtime is resolved once, before the reset.");
        AssertEx.Equal(expected: 2, change.EnqueuedRunIds.Count);
        AssertEx.Equal(expected: 1, context.RuntimeResolutions);
        _ = context.Store.DidNotReceive().EnqueueJudgeAttemptAsync(Arg.Any<BenchmarkEnqueueJudgeAttemptCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RejudgeRun_WithoutACurrentPolicy_IsRefused()
    {
        var context = new ServiceContext();

        var exception = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => context.Service.RejudgeRunAsync(Guid.NewGuid(), 1, force: false));

        AssertEx.Equal("JudgeDisabled", exception.Code);
    }

    private sealed class ServiceContext
    {
        public ServiceContext(AgentDefinitionKind agentKind = AgentDefinitionKind.Single,
            bool judgeModelInstalled = true,
            bool judgeCarriesProjector = false,
            bool judgeRuntimeResolves = true,
            int runCount = 0,
            IReadOnlyList<Guid>? succeededRunIds = null)
        {
            AgentId = Guid.NewGuid();
            Store = Substitute.For<IBenchmarkStore>();
            Store.CreateProjectAsync(Arg.Do<BenchmarkProjectInput>(input => CreatedInput = input),
                     Arg.Do<BenchmarkJudgePolicyChangeInput?>(change => CreatedPolicy = change),
                     Arg.Any<CancellationToken>())
                 .Returns(call => Project(call.Arg<BenchmarkProjectInput>()));
            Store.UpdateProjectAsync(ProjectId,
                     Arg.Any<long>(),
                     Arg.Any<BenchmarkProjectInput>(),
                     Arg.Do<BenchmarkJudgePolicyChangeInput?>(change => UpdatedPolicy = change),
                     Arg.Any<CancellationToken>())
                 .Returns(call => Project(call.Arg<BenchmarkProjectInput>()));
            Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(_ => CurrentProject());
            Store.CountRunsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(runCount);
            Store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(_ => _currentRevision);
            Store.GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => Run(call.Arg<Guid>()));
            Store.ActivateJudgePolicyAsync(ProjectId,
                     Arg.Any<long>(),
                     Arg.Any<ReadOnlyMemory<byte>>(),
                     Arg.Any<string>(),
                     Arg.Any<BenchmarkJudgeAttemptSeed?>(),
                     Arg.Any<CancellationToken>())
                 .Returns(call =>
                 {
                     ActivatedPolicyJson = call.ArgAt<ReadOnlyMemory<byte>>(2);
                     ActivatedHash = call.ArgAt<string>(3);
                     ActivatedSeed = call.ArgAt<BenchmarkJudgeAttemptSeed?>(4);
                     return new BenchmarkJudgePolicyActivation(Revision(ActivatedHash), WasCreated: true, succeededRunIds ?? []);
                 });
            Store.BeginProjectRejudgeAsync(ProjectId, Arg.Any<long>(), Arg.Any<BenchmarkJudgeAttemptSeed?>(), Arg.Any<CancellationToken>())
                 .Returns(call =>
                 {
                     RejudgeSeed = call.ArgAt<BenchmarkJudgeAttemptSeed?>(2);
                     return new BenchmarkJudgePolicyActivation(Revision(_currentRevision?.PolicyHash), WasCreated: false, succeededRunIds ?? []);
                 });
            Store.EnqueueJudgeAttemptAsync(Arg.Do<BenchmarkEnqueueJudgeAttemptCommand>(command => Enqueued.Add(command)), Arg.Any<CancellationToken>())
                 .Returns(call => Attempt(call.Arg<BenchmarkEnqueueJudgeAttemptCommand>()));

            var agents = Substitute.For<IAgentDefinitionStore>();
            agents.GetByIdAsync(AgentId, Arg.Any<CancellationToken>()).Returns(Definition(AgentId, agentKind));

            Models = Substitute.For<IBenchmarkInstalledModelLeaseProvider>();
            if (judgeModelInstalled)
            {
                Models.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                      .Returns(call => Task.FromResult<IBenchmarkInstalledModelLease>(new JudgeLease(Installed(call.ArgAt<string>(0), judgeCarriesProjector))));
            }
            else
            {
                Models.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                      .Returns<Task<IBenchmarkInstalledModelLease>>(_ => throw new KeyNotFoundException());
            }

            var runtimes = Substitute.For<IBenchmarkJudgeRuntimeResolver>();
            runtimes.ResolveAsync(Arg.Any<BenchmarkJudgePolicyV1>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        RuntimeResolutions++;
                        return judgeRuntimeResolves
                            ? Resolution(call.Arg<BenchmarkJudgePolicyV1>())
                            : throw new BenchmarkEligibilityException("judge runtime is unavailable");
                    });

            Catalog = Substitute.For<IBenchmarkCatalogService>();
            Catalog.ListEligibleModelsAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
                   .Returns<IReadOnlyList<BenchmarkEligibleModel>>(_ =>
                   [
                       new BenchmarkEligibleModel(BaseModelName, 32768, null, null, BaseFingerprint, SupportsTools: true)
                   ]);

            Service = new BenchmarkProjectService(Store, agents, Models, runtimes, Catalog);
        }

        public const string BaseModelName = "base.gguf";
        public const string BaseFingerprint = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        public IBenchmarkCatalogService Catalog { get; }

        public Guid AgentId { get; }
        public IBenchmarkStore Store { get; }
        public IBenchmarkInstalledModelLeaseProvider Models { get; }
        public IBenchmarkProjectService Service { get; }
        public BenchmarkProjectInput? CreatedInput { get; private set; }
        public BenchmarkJudgePolicyChangeInput? CreatedPolicy { get; private set; }
        public BenchmarkJudgePolicyChangeInput? UpdatedPolicy { get; private set; }
        public ReadOnlyMemory<byte>? ActivatedPolicyJson { get; private set; }
        public string? ActivatedHash { get; private set; }
        public BenchmarkJudgeAttemptSeed? ActivatedSeed { get; private set; }
        public BenchmarkJudgeAttemptSeed? RejudgeSeed { get; private set; }
        public int RuntimeResolutions { get; private set; }
        public List<BenchmarkEnqueueJudgeAttemptCommand> Enqueued { get; } = [];

        private BenchmarkJudgePolicyRevisionRecord? _currentRevision;

        public void SetCurrentRevision(string policyHash) =>
            _currentRevision = Revision(policyHash);

        /// <summary>Puts a real, deserializable policy revision on the project, the way an activation would have.</summary>
        public async Task SetCurrentPolicyAsync(BenchmarkJudgePolicyDraft draft)
        {
            var policy = await BuildPolicyAsync(draft).ConfigureAwait(false);
            _currentRevision = new BenchmarkJudgePolicyRevisionRecord(RevisionId,
                ProjectId,
                1,
                BenchmarkJudgeSerialization.SerializePolicy(policy),
                BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy),
                null,
                1,
                1);
        }

        /// <summary>The hash the service computes for <paramref name="draft" />, built the same way it does.</summary>
        public async Task<string> BuildPolicyHashAsync(BenchmarkJudgePolicyDraft draft) =>
            BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(await BuildPolicyAsync(draft).ConfigureAwait(false));

        private async Task<BenchmarkJudgePolicyV1> BuildPolicyAsync(BenchmarkJudgePolicyDraft draft)
        {
            await using var lease = await Models.AcquireAsync(draft.ModelName, CancellationToken.None).ConfigureAwait(false);
            return new BenchmarkJudgePolicyV1(BenchmarkJudgePolicyModelV1.FromSnapshot(BenchmarkInstalledModelSnapshotMapper.ToSnapshot(lease.Snapshot)),
                draft.ContextTokens,
                BenchmarkJudgePolicyVersions.PromptVersion,
                BenchmarkJudgePolicyVersions.OutputSchemaVersion,
                BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
                draft.Rubric ?? BenchmarkJudgeRubricDefaults.Default(),
                draft.ReferenceAnswer);
        }

        internal static BenchmarkProjectRecord CurrentProject() =>
            new(ProjectId, "Benchmark", Encoding.UTF8.GetBytes("\"task\""), 4096, Guid.NewGuid(), JudgeEnabled: true, RevisionId, IsFrozen: true, 1, 1, 1);

        private static BenchmarkJudgePolicyRevisionRecord Revision(string? policyHash) =>
            new(RevisionId, ProjectId, 1, Encoding.UTF8.GetBytes("{}"), policyHash ?? new string('a', count: 64), null, 1, 1);

        private static BenchmarkJudgeRuntimeResolution Resolution(BenchmarkJudgePolicyV1 policy) =>
            new(new BenchmarkJudgeRuntimeV1(BenchmarkJudgeRuntimeV1.CurrentSchemaVersion,
                    BenchmarkInstalledModelSnapshotMapper.ToSnapshot(Installed(policy.Model.ModelName, carriesProjector: false)),
                    policy.RequestedContextTokens,
                    new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cpu, policy.RequestedContextTokens, null, null, null, null, null, false,
                        LlamaServerBenchmarkLaunchPolicy.DeterministicV1),
                    BenchmarkFrozenPolicies.DeterministicSampling()),
                new BenchmarkRunLaunchIntent("cpu", "f16", "auto", "cpu-variant", "auto", new string('c', count: 64), null));

        private static BenchmarkRunRecord Run(Guid runId) =>
            new(runId, ProjectId, new byte[]
                {
                    1
                }, "model.gguf", LocalModelOrigin.Imported, $"v1:{new string('a', count: 64)}", "Agent", 1, 4096,
                BenchmarkPrimaryStatus.Succeeded, null, null, null, null, null, 0, null, null, 3, 1, 1, null, 1);

        private static BenchmarkJudgeAttemptRecord Attempt(BenchmarkEnqueueJudgeAttemptCommand command) =>
            new(Guid.NewGuid(), command.RunId, 1, command.PolicyRevisionId, 1, command.RuntimeJson, null,
                command.RuntimeJson is null ? BenchmarkJudgeAttemptStatus.Failed : BenchmarkJudgeAttemptStatus.Queued,
                null, null, command.RuntimeUnresolvedReason, 1, null, null, 1);

        private static BenchmarkProjectRecord Project(BenchmarkProjectInput input) =>
            new(input.Id, input.Name, input.CoreTaskJson, input.ContextTokens, input.AgentDefinitionId, JudgeEnabled: false,
                CurrentJudgePolicyRevisionId: null, IsFrozen: false, 1, 1, 1);

        private static AgentDefinitionRecord Definition(Guid id, AgentDefinitionKind kind) =>
            new(id, "Agent", null, "instructions", null, null, kind, [], new Dictionary<string, bool>(), null, 1, 1, 1);

        private static InstalledModelSnapshot Installed(string modelName, bool carriesProjector)
        {
            var revision = $"v1:{new string('a', count: 64)}";
            InstalledModelPhysicalMember[] members = carriesProjector
                ?
                [
                    Member(modelName, InstalledModelPhysicalMemberRole.Weight),
                    Member($"{modelName}-mmproj", InstalledModelPhysicalMemberRole.Projector)
                ]
                : [Member(modelName, InstalledModelPhysicalMemberRole.Weight)];
            return new InstalledModelSnapshot(modelName,
                revision,
                [],
                revision,
                members,
                revision,
                LocalModelOrigin.Imported,
                "llamacpp",
                "map-revision",
                "repo/judge",
                "revision",
                "Q4_K_M",
                GgufRole.Chat,
                revision);

            InstalledModelPhysicalMember Member(string path, InstalledModelPhysicalMemberRole role) =>
                new(path, role, 12, new string('b', count: 64), $"sha256:{new string('b', count: 64)}:12", [modelName], true, null);
        }

        private sealed class JudgeLease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
        {
            public InstalledModelSnapshot Snapshot { get; } = snapshot;

            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;
        }
    }
}
