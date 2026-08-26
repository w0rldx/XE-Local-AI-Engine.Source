namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The task-item service owns the wire shape and the caps; the store owns identity. These pin the half that can
///     be got wrong without any database: what an operator is allowed to write, and how a prompt is encoded.
/// </summary>
public sealed class BenchmarkTaskItemServiceTests
{
    private static readonly Guid ProjectId = new("44444444-4444-4444-4444-444444444444");

    /// <summary>
    ///     Item prompts are encoded exactly as the project's core task is, because item 0 IS the project's core task
    ///     for every project created before task items existed — two encodings would make the backfill lossy.
    /// </summary>
    [Test]
    public async Task Create_EncodesThePromptTheSameWayTheProjectsCoreTaskIs()
    {
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkTaskItemInput? captured = null;
        _ = store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Array.Empty<BenchmarkTaskItemRecord>());
        _ = store.CreateTaskItemAsync(ProjectId, Arg.Any<long>(), Arg.Do<BenchmarkTaskItemInput>(input => captured = input), Arg.Any<IReadOnlyList<BenchmarkTaskItemInput>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns(call => Record(call.Arg<BenchmarkTaskItemInput>()));
        var service = new BenchmarkTaskItemService(store);

        _ = await service.CreateAsync(ProjectId, expectedProjectVersion: 1, new BenchmarkTaskItemDraft("  sort the list  "));

        var input = AssertEx.NotNull(captured);
        AssertEx.Equal("  sort the list  ", JsonSerializer.Deserialize<string>(input.PromptJson.Span), "The prompt is stored verbatim, whitespace included.");
        AssertEx.Equal(BenchmarkTaskItemKinds.Prompt, input.Kind);
        AssertEx.True(input.ReferenceAnswerJson is null, "An omitted reference answer stays absent rather than becoming an empty payload.");
        AssertEx.True(input.VerifierConfigJson is null);
        AssertEx.True(input.CountsTowardScore, "An authored prompt counts toward the project score.");
    }

    [Test]
    public async Task Create_CarriesTheReferenceAnswerAndTheVerifierOverride()
    {
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkTaskItemInput? captured = null;
        _ = store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Array.Empty<BenchmarkTaskItemRecord>());
        _ = store.CreateTaskItemAsync(ProjectId, Arg.Any<long>(), Arg.Do<BenchmarkTaskItemInput>(input => captured = input), Arg.Any<IReadOnlyList<BenchmarkTaskItemInput>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns(call => Record(call.Arg<BenchmarkTaskItemInput>()));
        var service = new BenchmarkTaskItemService(store);
        using var config = JsonDocument.Parse("""{"correctness":{"expected":"[1,2,3]"}}""");

        _ = await service.CreateAsync(ProjectId,
            expectedProjectVersion: 1,
            new BenchmarkTaskItemDraft("sort", ReferenceAnswer: " [1,2,3] ", VerifierConfig: config.RootElement));

        var input = AssertEx.NotNull(captured);
        AssertEx.Equal("[1,2,3]", BenchmarkTaskItemService.DecodeOptional(input.ReferenceAnswerJson));
        AssertEx.Equal("""{"correctness":{"expected":"[1,2,3]"}}""", Encoding.UTF8.GetString(input.VerifierConfigJson!.Value.Span),
            "The verifier override is carried opaquely — it is the judge's contract, not this layer's.");
    }

    /// <summary>
    ///     The cap counts LEAVES, so a generator's cases each count against it. Past it a matrix stops being merely
    ///     slow and becomes unschedulable.
    /// </summary>
    [Test]
    public async Task Create_PastTheItemCap_IsRefusedWithoutTouchingTheStore()
    {
        var store = Substitute.For<IBenchmarkStore>();
        _ = store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>())
                 .Returns(Enumerable.Range(0, BenchmarkTaskItemService.MaxTaskItems).Select(index => Record(new BenchmarkTaskItemInput(Encoding.UTF8.GetBytes("x")), index))
                                    .ToArray());
        var service = new BenchmarkTaskItemService(store);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId, expectedProjectVersion: 1, new BenchmarkTaskItemDraft("one more")));

        _ = store.DidNotReceive().CreateTaskItemAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<BenchmarkTaskItemInput>(), Arg.Any<IReadOnlyList<BenchmarkTaskItemInput>?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The generator kinds are already in the schema's CHECK and in the store's vocabulary, ready to be written the
    ///     moment something can execute them. Nothing can yet, and accepting an item this build would never run is
    ///     worse than refusing it while the operator is still looking at the form.
    /// </summary>
    [Test]
    public async Task Create_WithAKindThisBuildCannotExecute_IsRefused()
    {
        var store = Substitute.For<IBenchmarkStore>();
        _ = store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Array.Empty<BenchmarkTaskItemRecord>());
        var service = new BenchmarkTaskItemService(store);

        foreach (var kind in new[]
                 {
                     BenchmarkTaskItemKinds.Niah,
                     BenchmarkTaskItemKinds.NiahCase,
                     "invented"
                 })
        {
            _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId, expectedProjectVersion: 1, new BenchmarkTaskItemDraft("probe", kind)),
                $"'{kind}' must be refused for now.");
        }

        _ = store.DidNotReceive().CreateTaskItemAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<BenchmarkTaskItemInput>(), Arg.Any<IReadOnlyList<BenchmarkTaskItemInput>?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WithABlankPrompt_IsRefused()
    {
        var store = Substitute.For<IBenchmarkStore>();
        _ = store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Array.Empty<BenchmarkTaskItemRecord>());
        var service = new BenchmarkTaskItemService(store);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId, expectedProjectVersion: 1, new BenchmarkTaskItemDraft("   ")));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.UpdateAsync(ProjectId, Guid.NewGuid(), expectedVersion: 1, new BenchmarkTaskItemDraft("")));
    }

    /// <summary>
    ///     An override names its criterion by id, and the judge applies it by matching that id against the rubric. A
    ///     misspelled or deleted id therefore does not fail — it applies NOTHING and the item is graded under the
    ///     policy's own configuration, against another item's expected answer, with the wrong score indistinguishable
    ///     from a right one. Refused while the operator is still looking at the form.
    /// </summary>
    [Test]
    public async Task Create_WithAnOverrideNamingACriterionTheRubricDoesNotHave_IsRefused()
    {
        var store = StoreWithRubric(new BenchmarkJudgeRubricCriterionV1("correctness", "Correctness", "Is it right?", 40,
            BenchmarkJudgeCriterionKinds.Exact, """{"expected":"answer"}"""));
        var service = new BenchmarkTaskItemService(store);
        using var config = JsonDocument.Parse("""{"correctnes":{"expected":"[1,2,3]"}}""");

        var failure = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId,
            expectedProjectVersion: 1,
            new BenchmarkTaskItemDraft("sort", VerifierConfig: config.RootElement)));

        AssertEx.Contains(failure.Message, "correctnes", message: "The refusal names the criterion that matched nothing.");
        _ = store.DidNotReceive().CreateTaskItemAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<BenchmarkTaskItemInput>(),
            Arg.Any<IReadOnlyList<BenchmarkTaskItemInput>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Matching the id is not enough: an <c>exact</c> configuration handed to an <c>llm</c> criterion is applied,
    ///     then ignored by a model that never reads a config, and the operator's expected answer is never checked. The
    ///     policy's OWN parser decides, so an override can never mean something a criterion's config could not.
    /// </summary>
    [Test]
    public async Task Create_WithAnOverrideTheCriterionsKindCannotHonour_IsRefused()
    {
        var store = StoreWithRubric(new BenchmarkJudgeRubricCriterionV1("correctness", "Correctness", "Is it right?", 40));
        var service = new BenchmarkTaskItemService(store);
        using var config = JsonDocument.Parse("""{"correctness":{"expected":"[1,2,3]"}}""");

        var failure = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId,
            expectedProjectVersion: 1,
            new BenchmarkTaskItemDraft("sort", VerifierConfig: config.RootElement)));

        AssertEx.Contains(failure.Message, "correctness");
        _ = store.DidNotReceive().CreateTaskItemAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<BenchmarkTaskItemInput>(),
            Arg.Any<IReadOnlyList<BenchmarkTaskItemInput>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An override the rubric can honour is written unchanged — the check refuses, it never rewrites.</summary>
    [Test]
    public async Task Create_WithAnOverrideTheRubricHonours_IsWritten()
    {
        var store = StoreWithRubric(new BenchmarkJudgeRubricCriterionV1("correctness", "Correctness", "Is it right?", 40,
            BenchmarkJudgeCriterionKinds.Exact, """{"expected":"answer"}"""));
        BenchmarkTaskItemInput? captured = null;
        _ = store.CreateTaskItemAsync(ProjectId, Arg.Any<long>(), Arg.Do<BenchmarkTaskItemInput>(input => captured = input),
                     Arg.Any<IReadOnlyList<BenchmarkTaskItemInput>?>(), Arg.Any<CancellationToken>())
                 .Returns(call => Record(call.Arg<BenchmarkTaskItemInput>()));
        var service = new BenchmarkTaskItemService(store);
        using var config = JsonDocument.Parse("""{"correctness":{"expected":"[1,2,3]"}}""");

        _ = await service.CreateAsync(ProjectId, expectedProjectVersion: 1, new BenchmarkTaskItemDraft("sort", VerifierConfig: config.RootElement));

        AssertEx.Equal("""{"correctness":{"expected":"[1,2,3]"}}""",
            Encoding.UTF8.GetString(AssertEx.NotNull(captured).VerifierConfigJson!.Value.Span));
    }

    /// <summary>A project with an enabled judge, so an item's override has a rubric to be checked against.</summary>
    private static IBenchmarkStore StoreWithRubric(params BenchmarkJudgeRubricCriterionV1[] criteria)
    {
        var store = Substitute.For<IBenchmarkStore>();
        _ = store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Array.Empty<BenchmarkTaskItemRecord>());
        var policy = new BenchmarkJudgePolicyV1(new BenchmarkJudgePolicyModelV1("judge.gguf", "v1:" + new string('c', count: 64), [new string('b', count: 64)]),
            4096,
            BenchmarkJudgePolicyVersions.PromptVersion,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            new BenchmarkJudgeRubricV1(BenchmarkJudgePolicyVersions.RubricVersion, criteria),
            ReferenceAnswer: null);
        _ = store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>())
                 .Returns(new BenchmarkJudgePolicyRevisionRecord(Guid.NewGuid(), ProjectId, 1,
                     BenchmarkJudgeSerialization.SerializePolicy(policy), new string('0', count: 64), null, 1, 0));
        return store;
    }

    [Test]
    public void Decode_ReadsBackWhatCreateWrote()
    {
        AssertEx.Equal("a prompt", BenchmarkTaskItemService.DecodePrompt(JsonSerializer.SerializeToUtf8Bytes("a prompt")));
        AssertEx.Null(BenchmarkTaskItemService.DecodeOptional(payload: null), "An absent payload decodes to null, not to an empty string.");
        AssertEx.Equal("an answer", BenchmarkTaskItemService.DecodeOptional(JsonSerializer.SerializeToUtf8Bytes("an answer")));
        AssertEx.True(BenchmarkTaskItemService.DecodeJson(payload: null) is null, "An absent config decodes to null.");
        AssertEx.Equal("""{"a":1}""", BenchmarkTaskItemService.DecodeJson(Encoding.UTF8.GetBytes("""{"a":1}"""))?.ToString());
    }

    private static BenchmarkTaskItemRecord Record(BenchmarkTaskItemInput input, int index = 0) =>
        new(Guid.NewGuid(), ProjectId, input.ParentItemId, index, input.Kind, Revision: 1, "v1:" + new string('a', count: 64),
            input.CountsTowardScore, input.PromptJson, input.ReferenceAnswerJson, input.VerifierConfigJson, input.GeneratorConfigJson,
            Version: 1, CreatedAtUtc: 1, UpdatedAtUtc: 1);
}
