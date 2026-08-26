namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Expansion at item-WRITE time. A case generated during a freeze would have no durable identity — nothing to
///     stamp on the run, nothing for the caps to count, no way for the ranking read to know how many probes a cell
///     owed. Expanded here, a case is an ordinary task item and every one of those mechanisms reaches it for free.
/// </summary>
public sealed class BenchmarkNiahTaskItemTests
{
    private static readonly Guid ProjectId = new("55555555-5555-5555-5555-555555555555");

    [Test]
    public async Task Create_ExpandsAProbeIntoChildCases_AtWriteTime()
    {
        var store = StoreWith(contextTokens: 65536);
        var (_, children) = await CreateProbeAsync(store, "{\"contextTokens\":[8192,32768],\"needleDepthPercent\":[10,50,90]}");

        var cases = AssertEx.NotNull(children);
        AssertEx.Equal(6, cases.Count, "Two lengths and three depths are six cases, and six leaf items.");
        AssertEx.True(cases.All(child => string.Equals(child.Kind, BenchmarkTaskItemKinds.NiahCase, StringComparison.Ordinal)),
            "Every child is a case kind, so the generator itself is never a run target.");
        AssertEx.True(cases.All(child => child.ParentItemId is not null), "Each case points back at the probe that produced it.");
        AssertEx.Equal(1, cases.Select(child => child.ParentItemId).Distinct().Count());
        AssertEx.True(cases.All(static child => !child.CountsTowardScore),
            "Recall is a capability, not quality: a case stays out of the project's ranked mean unless the operator opts in.");
        AssertEx.True(cases.All(static child => child.VerifierConfigJson is { IsEmpty: false }),
            "Each case carries the expected passcode as its own exact-criterion override.");
        AssertEx.True(cases.All(static child => child.GeneratorConfigJson is { IsEmpty: false }),
            "And its own parameters, so the freeze can re-check its length without parsing the haystack back out.");
    }

    /// <summary>The generator's own row is written too, and it is NOT a leaf — a freeze fans out over the cases.</summary>
    [Test]
    public async Task Create_TheProbeItselfIsAGeneratorNotARunTarget()
    {
        var store = StoreWith(contextTokens: 16384);
        var (input, _) = await CreateProbeAsync(store, "{\"contextTokens\":[8192],\"needleDepthPercent\":[50]}");

        AssertEx.Equal(BenchmarkTaskItemKinds.Niah, input.Kind);
        AssertEx.False(BenchmarkTaskItemKinds.IsLeaf(input.Kind), "A generator is never frozen into a run.");
        AssertEx.NotEqual(Guid.Empty, input.Id, "The service mints the id, because every case is derived from it.");
    }

    /// <summary>
    ///     The cases count against the item cap as themselves. A probe that cost 1 against a cap and 6 against the GPU
    ///     would let an operator schedule a night of work while the form said the project held two questions.
    /// </summary>
    [Test]
    public async Task Create_CasesCountIndividuallyAgainstTheItemCap()
    {
        var store = StoreWith(contextTokens: 65536, existingLeaves: 15);
        var service = new BenchmarkTaskItemService(store);

        var failure = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId,
            expectedProjectVersion: 1,
            Draft("{\"contextTokens\":[8192,32768],\"needleDepthPercent\":[10,50,90]}")));

        AssertEx.Contains(failure.Message, "20", message: "Fifteen leaves plus six cases is past the cap, and the cap is named.");
    }

    [Test]
    public async Task Update_RegeneratesTheCases()
    {
        var store = StoreWith(contextTokens: 65536);
        var probeId = Guid.NewGuid();
        _ = store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>())
                 .Returns([Item(probeId, BenchmarkTaskItemKinds.Niah, null), Item(Guid.NewGuid(), BenchmarkTaskItemKinds.NiahCase, probeId)]);
        IReadOnlyList<BenchmarkTaskItemInput>? children = null;
        _ = store.UpdateTaskItemAsync(ProjectId,
                     probeId,
                     Arg.Any<long>(),
                     Arg.Any<BenchmarkTaskItemInput>(),
                     Arg.Do<IReadOnlyList<BenchmarkTaskItemInput>?>(value => children = value),
                     Arg.Any<CancellationToken>())
                 .Returns(call => Record(call.Arg<BenchmarkTaskItemInput>()));
        var service = new BenchmarkTaskItemService(store);

        _ = await service.UpdateAsync(ProjectId, probeId, expectedVersion: 1, Draft("{\"contextTokens\":[4096],\"needleDepthPercent\":[25,75]}"));

        var regenerated = AssertEx.NotNull(children);
        AssertEx.Equal(2, regenerated.Count, "The edit hands the store a full replacement set, not a patch.");
        AssertEx.True(regenerated.All(child => child.ParentItemId == probeId));
    }

    /// <summary>
    ///     A case cannot be edited or deleted on its own. Its parameters live on the generator, so an edit here would
    ///     survive exactly until the next re-expansion — and a case that disagrees with its probe measures something
    ///     nobody configured.
    /// </summary>
    [Test]
    public async Task GeneratedCases_CannotBeEditedOrDeletedOnTheirOwn()
    {
        var store = StoreWith(contextTokens: 16384);
        var probeId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        _ = store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>())
                 .Returns([Item(probeId, BenchmarkTaskItemKinds.Niah, null), Item(caseId, BenchmarkTaskItemKinds.NiahCase, probeId)]);
        var service = new BenchmarkTaskItemService(store);

        var edit = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.UpdateAsync(ProjectId, caseId, expectedVersion: 1, new BenchmarkTaskItemDraft("rewritten")));
        var delete = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.DeleteAsync(ProjectId, caseId, expectedVersion: 1));

        AssertEx.Contains(edit.Message, "generated", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(delete.Message, "generated", StringComparison.OrdinalIgnoreCase);
        await store.DidNotReceive().DeleteTaskItemAsync(ProjectId, caseId, Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_AProbeWithoutItsGeneratorConfiguration_IsRefused()
    {
        var store = StoreWith(contextTokens: 16384);
        var service = new BenchmarkTaskItemService(store);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId,
            expectedProjectVersion: 1,
            new BenchmarkTaskItemDraft("a long-context probe", BenchmarkTaskItemKinds.Niah)));
    }

    /// <summary>The refusal an operator meets while still looking at the form, rather than an hour into a batch.</summary>
    [Test]
    public async Task Create_AProbeLongerThanTheProjectWindow_IsRefusedAtExpansion()
    {
        var store = StoreWith(contextTokens: 8192);
        var service = new BenchmarkTaskItemService(store);

        var failure = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId,
            expectedProjectVersion: 1,
            Draft("{\"contextTokens\":[32768],\"needleDepthPercent\":[50]}")));

        AssertEx.Contains(failure.Message, "32768");
        AssertEx.Contains(failure.Message, "8192");
        await store.DidNotReceive().CreateTaskItemAsync(ProjectId,
            Arg.Any<long>(),
            Arg.Any<BenchmarkTaskItemInput>(),
            Arg.Any<IReadOnlyList<BenchmarkTaskItemInput>?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A case is written by the generator that owns it; one written by hand would carry a parent that does not describe it.</summary>
    [Test]
    public async Task Create_ACaseKindWrittenByHand_IsRefused()
    {
        var store = StoreWith(contextTokens: 16384);
        var service = new BenchmarkTaskItemService(store);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId,
            expectedProjectVersion: 1,
            new BenchmarkTaskItemDraft("hand-written case", BenchmarkTaskItemKinds.NiahCase)));
    }

    /// <summary>The override the judge resolves per criterion. It has to parse as an exact criterion's own config.</summary>
    [Test]
    public async Task Create_EachCaseCarriesAnExactCriterionOverrideThatParses()
    {
        var store = StoreWith(contextTokens: 16384);
        var (_, children) = await CreateProbeAsync(store, "{\"contextTokens\":[4096],\"needleDepthPercent\":[50],\"criterionId\":\"needle\"}");

        var overrides = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Encoding.UTF8.GetString(AssertEx.NotNull(children)[0].VerifierConfigJson!.Value.Span))!;
        AssertEx.True(overrides.ContainsKey("needle"), "The override is keyed by the criterion the configuration named.");

        var spec = AssertEx.NotNull(BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Exact, overrides["needle"].GetRawText()));
        AssertEx.NotNullOrEmpty(spec.ExpectedText);
        AssertEx.True(spec.Normalize.CaseInsensitive, "A recall probe asks whether the model found the passcode, not whether it held the shift key.");
    }

    /// <summary>
    ///     Every case's override targets ONE criterion id, and the probe's whole measurement is that criterion. Named
    ///     against a rubric that does not have it, the cases would be graded on the policy's own configuration —
    ///     every needle checked against one shared expected answer — so the probe is refused rather than expanded.
    /// </summary>
    [Test]
    public async Task Create_WhenTheRubricLacksTheCriterionTheCasesOverride_IsRefused()
    {
        var store = StoreWith(contextTokens: 16384,
            rubric: new BenchmarkJudgeRubricCriterionV1("correctness", "Correctness", "Is it right?", 40));
        var service = new BenchmarkTaskItemService(store);

        var failure = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(ProjectId,
            expectedProjectVersion: 1,
            Draft("{\"contextTokens\":[4096],\"needleDepthPercent\":[50],\"criterionId\":\"needle\"}")));

        AssertEx.Contains(failure.Message, "needle", message: "The refusal names the criterion the cases would have overridden.");
        _ = store.DidNotReceive().CreateTaskItemAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<BenchmarkTaskItemInput>(),
            Arg.Any<IReadOnlyList<BenchmarkTaskItemInput>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>And expands when the rubric does carry it, as the <c>exact</c> criterion the cases configure.</summary>
    [Test]
    public async Task Create_WhenTheRubricCarriesTheCriterionAsExact_Expands()
    {
        var store = StoreWith(contextTokens: 16384,
            rubric: new BenchmarkJudgeRubricCriterionV1("needle", "Needle", "Was the passcode recalled?", 100,
                BenchmarkJudgeCriterionKinds.Exact, """{"expected":"placeholder"}"""));

        var (_, children) = await CreateProbeAsync(store, "{\"contextTokens\":[4096],\"needleDepthPercent\":[50],\"criterionId\":\"needle\"}");

        AssertEx.Equal(1, AssertEx.NotNull(children).Count);
    }

    private static async Task<(BenchmarkTaskItemInput Input, IReadOnlyList<BenchmarkTaskItemInput>? Children)> CreateProbeAsync(IBenchmarkStore store,
        string generatorConfigJson)
    {
        BenchmarkTaskItemInput? input = null;
        IReadOnlyList<BenchmarkTaskItemInput>? children = null;
        _ = store.CreateTaskItemAsync(ProjectId,
                     Arg.Any<long>(),
                     Arg.Do<BenchmarkTaskItemInput>(value => input = value),
                     Arg.Do<IReadOnlyList<BenchmarkTaskItemInput>?>(value => children = value),
                     Arg.Any<CancellationToken>())
                 .Returns(call => Record(call.Arg<BenchmarkTaskItemInput>()));
        var service = new BenchmarkTaskItemService(store);

        _ = await service.CreateAsync(ProjectId, expectedProjectVersion: 1, Draft(generatorConfigJson));

        return (AssertEx.NotNull(input), children);
    }

    private static BenchmarkTaskItemDraft Draft(string generatorConfigJson)
    {
        using var document = JsonDocument.Parse(generatorConfigJson);
        return new BenchmarkTaskItemDraft("a long-context probe", BenchmarkTaskItemKinds.Niah, GeneratorConfig: document.RootElement.Clone());
    }

    private static IBenchmarkStore StoreWith(int contextTokens, int existingLeaves = 0, BenchmarkJudgeRubricCriterionV1? rubric = null)
    {
        var store = Substitute.For<IBenchmarkStore>();
        _ = store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>())
                 .Returns(Enumerable.Range(0, existingLeaves).Select(_ => Item(Guid.NewGuid(), BenchmarkTaskItemKinds.Prompt, null)).ToArray());
        _ = store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project(contextTokens));

        // No revision unless a test asks for one: a disabled judge has no rubric an override can be checked against,
        // which is the state every case-shape test above is written in.
        if (rubric is not null)
        {
            var policy = new BenchmarkJudgePolicyV1(new BenchmarkJudgePolicyModelV1("judge.gguf", "v1:" + new string('c', count: 64), [new string('b', count: 64)]),
                4096,
                BenchmarkJudgePolicyVersions.PromptVersion,
                BenchmarkJudgePolicyVersions.OutputSchemaVersion,
                BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
                new BenchmarkJudgeRubricV1(BenchmarkJudgePolicyVersions.RubricVersion, [rubric]),
                ReferenceAnswer: null);
            _ = store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>())
                     .Returns(new BenchmarkJudgePolicyRevisionRecord(Guid.NewGuid(), ProjectId, 1,
                         BenchmarkJudgeSerialization.SerializePolicy(policy), new string('0', count: 64), null, 1, 0));
        }

        return store;
    }

    private static BenchmarkTaskItemRecord Item(Guid id, string kind, Guid? parentItemId) =>
        new(id, ProjectId, parentItemId, Index: 0, kind, Revision: 1, "v1:hash", CountsTowardScore: true,
            "{}"u8.ToArray(), null, null, null, Version: 1, CreatedAtUtc: 0, UpdatedAtUtc: 0);

    private static BenchmarkTaskItemRecord Record(BenchmarkTaskItemInput input) =>
        new(input.Id, ProjectId, input.ParentItemId, Index: 0, input.Kind, Revision: 1, "v1:hash", input.CountsTowardScore,
            input.PromptJson, input.ReferenceAnswerJson, input.VerifierConfigJson, input.GeneratorConfigJson,
            Version: 1, CreatedAtUtc: 0, UpdatedAtUtc: 0);

    private static BenchmarkProjectRecord Project(int contextTokens) =>
        new(ProjectId, "probe project", "{}"u8.ToArray(), contextTokens, Guid.NewGuid(), JudgeEnabled: true,
            CurrentJudgePolicyRevisionId: null, IsFrozen: false, Version: 1, CreatedAtUtc: 0, UpdatedAtUtc: 0);
}
