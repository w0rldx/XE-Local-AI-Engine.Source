namespace XE_Local_AI_Engine.Tests.Invocation;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The deterministic <c>auto</c> ladder: every signal in isolation, both hard rules, both tie-break boundaries,
///     both language phrase sets, and the swap gates. The dispatcher never fails a turn — under any refusal it falls
///     back to the resolved model at a lower effort — so every case here asserts a decision, never an exception.
/// </summary>
public sealed class ReasoningEffortDispatcherTests
{
    private const string ResolvedModel = "qwen3.8-27b";

    // 700 characters: past the long-message threshold but short of the very-long one.
    private static readonly string LongText = new('a', 700);

    // 1300 characters: past both length thresholds.
    private static readonly string VeryLongText = new('a', 1300);

    [Test]
    public async Task Dispatch_WhenOrchestrated_IsNormalAndNeverConsultsTheTrustResolver()
    {
        // Hard rule 1: an orchestrated turn is many models' work behind one package, so no single tier belongs to it
        // and no score is computed. It is also the one branch that must not pay for a trust lookup.
        var trustResolver = CreateTrustResolver(ModelTrustLocality.Local);
        var decision = await DispatchAsync(Request(VeryLongText + "```", hasOrchestration: true, conversationDepth: 20), trustResolver);

        AssertEx.Equal(ReasoningTier.Normal, decision.Tier);
        AssertEx.Equal("medium", decision.Effort);
        AssertEx.Equal(ReasoningDispatchReasons.Orchestration, decision.ReasonCode);
        AssertEx.Null(decision.MaxOutputTokens);
        await trustResolver.DidNotReceive().ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("hi", 1, "none")]
    [Arguments("Walk me through the whole architecture, carefully and thoroughly, because I need to understand every hop before I touch it.", 12, "on")]
    public async Task Dispatch_WhenModelIsBinary_MapsTheTierOntoTheBinaryPairAndNeverSwaps(string text, int conversationDepth, string expectedEffort)
    {
        // Hard rule 2: a stale `auto` can still reach a model with no graded ladder (the composer never offers it
        // there). It has to mean something, and "reason, unless the turn is trivial" is it.
        var decision = await DispatchAsync(Request(text, supportsThinking: false, allowAutoModelSwap: true, conversationDepth: conversationDepth));

        AssertEx.Equal(expectedEffort, decision.Effort);
        AssertEx.Equal(ReasoningDispatchReasons.BinaryModel, decision.ReasonCode);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.False(decision.SupportsThinking);
    }

    [Test]
    public async Task Dispatch_WhenModelIsBinaryAndTierIsFast_CarriesTheBinaryOutputBudget()
    {
        var decision = await DispatchAsync(Request("hi", supportsThinking: false));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal(expected: 2048, decision.MaxOutputTokens);
    }

    // ---- the score terms, one at a time -------------------------------------------------------------------------

    [Test]
    public async Task Dispatch_WhenNothingIsDecisive_IsNormalAndBalanced()
    {
        // 300 characters: no length bonus, no penalty, no phrase, no fence, a shallow conversation. Score 0.
        var decision = await DispatchAsync(Request(new string('a', 300)));

        AssertEx.Equal(ReasoningTier.Normal, decision.Tier);
        AssertEx.Equal("medium", decision.Effort);
        AssertEx.Equal(ReasoningDispatchReasons.Balanced, decision.ReasonCode);
        AssertEx.Null(decision.MaxOutputTokens);
    }

    [Test]
    public async Task Dispatch_WhenMessageIsShort_IsFast()
    {
        var decision = await DispatchAsync(Request("thanks, noted"));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
    }

    // On a FAST turn the notice message already names the tier, so the reason names what the message does NOT say:
    // why the model was not replaced. The tier signal is what the reason names on a Deep turn, and on a Fast turn
    // whose swap was admitted.
    [Test]
    public async Task Dispatch_WhenTierIsFast_TheReasonNamesTheSwapGateRatherThanTheTierSignal()
    {
        var pinned = await DispatchAsync(Request("thanks, noted"));
        var unset = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true));

        AssertEx.Equal(ReasoningTier.Fast, pinned.Tier);
        AssertEx.Equal(ReasoningDispatchReasons.ModelPinned, pinned.ReasonCode);
        AssertEx.Equal(ReasoningTier.Fast, unset.Tier);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnset, unset.ReasonCode);
    }

    [Test]
    public async Task Dispatch_WhenShortQuestionEarlyInAConversation_IsFast()
    {
        // Both short-turn terms fire: a short message AND a question mark inside a shallow conversation. Score -2.
        var decision = await DispatchAsync(Request("What port does the node listen on?", conversationDepth: 1));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
    }

    [Test]
    public async Task Dispatch_WhenQuestionArrivesDeepInAConversation_LosesTheQuickQuestionBonus()
    {
        // The SAME question, past the shallow-conversation cutoff: the question-mark term no longer fires, and the
        // deep-conversation term cancels the short-message penalty, so the turn is ordinary again.
        var decision = await DispatchAsync(Request("What port does the node listen on?", conversationDepth: 9));

        AssertEx.Equal(ReasoningTier.Normal, decision.Tier);
    }

    [Test]
    [Arguments("Answer briefly: what does the supervisor do when a lease is refused by a profiling-owned process?")]
    [Arguments("tl;dr on the retention sweeper, please — I only need the gist of what it prunes and how often.")]
    [Arguments("Just tell me which port the node binds; I do not need the reasoning behind the choice at all.")]
    // The German half of the same vocabulary — both phrase lists are one rule, so they are pinned by one test.
    [Arguments("Fasse den Aufbau des Supervisors kurz zusammen, ohne auf die einzelnen Zustände im Detail einzugehen.")]
    [Arguments("Erklär mir in einem Satz, was der Retention-Sweeper eigentlich löscht und in welchem Rhythmus er läuft.")]
    [Arguments("Sag mir schnell, welchen Port der Node bindet; die Begründung dahinter brauche ich diesmal nicht.")]
    public async Task Dispatch_WhenTextCarriesAFastPhrase_IsFast(string text)
    {
        // Each of these is long enough that the short-message term does NOT fire, so the phrase alone is what
        // demotes the turn — which is what makes this a test of the phrase list rather than of the length.
        var decision = await DispatchAsync(Request(text));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
    }

    [Test]
    public async Task Dispatch_WhenAFastPhraseIsEmbeddedInALongerWord_DoesNotFire()
    {
        // "kurz" must not match inside "Kurzschluss", and "prove" must not match inside "improve" — the word-boundary
        // rule is what keeps a translated phrase list from firing on unrelated prose.
        var decision = await DispatchAsync(Request("Ein Kurzschluss im Netzteil ist nicht dasselbe wie ein Fehler in der Firmware, und wir sollten das sauber auseinanderhalten."));

        AssertEx.Equal(ReasoningTier.Normal, decision.Tier);
        AssertEx.Equal(ReasoningDispatchReasons.Balanced, decision.ReasonCode);
    }

    [Test]
    [Arguments("Please think it through before you answer, because the ordering of the two disposals is what actually matters here and I keep getting it wrong.")]
    [Arguments("Walk me through the root cause of the lease refusal, because I have read the supervisor twice and still cannot see which guard fires first.")]
    // The German half of the same vocabulary.
    [Arguments("Bitte geh das Schritt für Schritt durch, damit ich die Reihenfolge der beiden Freigaben endlich verstehe und nicht wieder rate.")]
    [Arguments("Nenn mir bitte die Ursache dieser Absage, denn ich habe den Supervisor zweimal gelesen und sehe die Reihenfolge immer noch nicht.")]
    public async Task Dispatch_WhenADeepPhraseMeetsADeepConversation_IsDeepAndNamesThePhrase(string text)
    {
        // A deep phrase alone is +2, which is not enough on its own — that is deliberate, so one polite "carefully"
        // cannot force the top tier. With a conversation that has already accumulated state it reaches Deep.
        var decision = await DispatchAsync(Request(text, conversationDepth: 12));

        AssertEx.Equal(ReasoningTier.Deep, decision.Tier);
        AssertEx.Equal("high", decision.Effort);
        AssertEx.Equal(ReasoningDispatchReasons.DeepPhrase, decision.ReasonCode);
        AssertEx.Null(decision.MaxOutputTokens);
    }

    [Test]
    public async Task Dispatch_WhenACodeFenceMeetsALongMessage_IsDeepAndNamesTheFence()
    {
        var decision = await DispatchAsync(Request("```csharp\n" + LongText + "\n```"));

        AssertEx.Equal(ReasoningTier.Deep, decision.Tier);
        AssertEx.Equal(ReasoningDispatchReasons.CodeFence, decision.ReasonCode);
    }

    [Test]
    public async Task Dispatch_WhenAShortMessageCarriesACodeFence_LosesTheShortMessagePenalty()
    {
        // A short message with code in it is not a remark. Score +2, so it lands on Normal rather than Fast.
        var decision = await DispatchAsync(Request("```\nvar x = 1;\n```"));

        AssertEx.Equal(ReasoningTier.Normal, decision.Tier);
    }

    [Test]
    public async Task Dispatch_WhenMessageIsVeryLongInADeepConversation_IsDeepAndNamesTheLength()
    {
        // Both length terms plus the deep-conversation term, with no fence and no phrase, so the LENGTH is what the
        // notice names.
        var decision = await DispatchAsync(Request(VeryLongText, conversationDepth: 10));

        AssertEx.Equal(ReasoningTier.Deep, decision.Tier);
        AssertEx.Equal(ReasoningDispatchReasons.LongMessage, decision.ReasonCode);
    }

    [Test]
    public async Task Dispatch_WhenContextIsEmpty_DegradesToNormal()
    {
        // An empty or assistant-only conversation gives the dispatcher no text and no attachment. It must score 0 and
        // land on Normal rather than reading "no text" as "a trivially short turn" and quietly resolving to Fast.
        var decision = await DispatchAsync(Request(string.Empty, conversationDepth: 0));

        AssertEx.Equal(ReasoningTier.Normal, decision.Tier);
        AssertEx.Equal(ReasoningDispatchReasons.Balanced, decision.ReasonCode);
    }

    // ---- tie-break boundaries ------------------------------------------------------------------------------------

    [Test]
    public async Task Dispatch_AtTheDeepBoundary_PromotesAtThreeAndNotAtTwo()
    {
        // Code fence (+2) alone is 2 → Normal; the same fence in a deep conversation (+1) is 3 → Deep. The threshold
        // is pinned from both sides so a weight change cannot move it silently.
        var atTwo = await DispatchAsync(Request("```\nvar x = 1;\n```"));
        var atThree = await DispatchAsync(Request("```\nvar x = 1;\n```", conversationDepth: 8));

        AssertEx.Equal(ReasoningTier.Normal, atTwo.Tier);
        AssertEx.Equal(ReasoningTier.Deep, atThree.Tier);
    }

    [Test]
    public async Task Dispatch_AtTheFastBoundary_DemotesAtMinusOneAndNotAtZero()
    {
        // A short message (-1) is Fast; the same short message carrying an image (+1) is 0 → Normal.
        var atMinusOne = await DispatchAsync(Request("thanks, noted"));
        var atZero = await DispatchAsync(Request("thanks, noted", hasAttachments: true));

        AssertEx.Equal(ReasoningTier.Fast, atMinusOne.Tier);
        AssertEx.Equal(ReasoningTier.Normal, atZero.Tier);
    }

    [Test]
    public async Task Dispatch_WhenTheTurnCarriesAnImage_ScoresItUpwards()
    {
        // The attachment term PROMOTES. It is the same flag that refuses the model swap, and pointing it downwards
        // would have made Fast unreachable on every image turn.
        var withoutImage = await DispatchAsync(Request("summarise this", conversationDepth: 1));
        var withImage = await DispatchAsync(Request("summarise this", hasAttachments: true, conversationDepth: 1));

        AssertEx.Equal(ReasoningTier.Fast, withoutImage.Tier);
        AssertEx.Equal(ReasoningTier.Normal, withImage.Tier);
    }

    // ---- the swap gates ------------------------------------------------------------------------------------------

    [Test]
    public async Task Fast_WhenToolsAreOffered_StaysFastAndRefusesTheSwap()
    {
        // The reachability case for "tools never demote the tier". A short turn on a tool-enabled agent must still be
        // FAST at `low` on the SAME model — only the swap is refused. A Normal tier here is the regression this pins.
        var decision = await DispatchAsync(Request("thanks, noted", offeredToolCount: 9, allowAutoModelSwap: true));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.ToolsNoSwap, decision.ReasonCode);
        AssertEx.Null(decision.CapacityReservation);
    }

    [Test]
    public async Task Fast_WhenToolsAreOffered_NeverSwaps()
    {
        var decision = await DispatchAsync(Request("thanks, noted", offeredToolCount: 1, allowAutoModelSwap: true));

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.ToolsNoSwap, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenAttachmentsPresent_NeverSwaps()
    {
        // The attachment term promotes, so the turn has to be short enough to stay FAST with it. A fast phrase does it.
        var decision = await DispatchAsync(Request("tl;dr please", hasAttachments: true, allowAutoModelSwap: true));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.AttachmentsNoSwap, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenSkillsPresent_NeverSwaps()
    {
        var decision = await DispatchAsync(Request("thanks, noted", hasSkills: true, allowAutoModelSwap: true));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.SkillsNoSwap, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenResponseSchemaPresent_NeverSwaps()
    {
        var decision = await DispatchAsync(Request("thanks, noted", hasResponseSchema: true, allowAutoModelSwap: true));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.SchemaNoSwap, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenUnattended_NeverSwaps()
    {
        var decision = await DispatchAsync(Request("thanks, noted", isUnattended: true, allowAutoModelSwap: true));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.UnattendedNoSwap, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenTheModelIsPinned_NeverSwaps()
    {
        // The fail-closed default. `AllowAutoModelSwap` is false on an explicit user pick, an honored agent pin, and
        // every construction site that never heard of this feature.
        var decision = await DispatchAsync(Request("thanks, noted"));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.ModelPinned, decision.ReasonCode);
    }

    [Test]
    [Arguments(ModelTrustLocality.Cloud)]
    [Arguments(ModelTrustLocality.Unresolved)]
    public async Task Fast_WhenResolvedModelIsCloudOrUnresolved_NeverSwapsAndCarriesNoOutputBudget(ModelTrustLocality locality)
    {
        // `Unresolved` is treated exactly as `Cloud` by every gate, so the `== Local` comparison is fail-closed by
        // construction. A cloud turn keeps the ladder but never the swap and never a node-side output budget.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true), CreateTrustResolver(locality));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Null(decision.MaxOutputTokens);
        AssertEx.Equal(ReasoningDispatchReasons.CloudNoSwap, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenNoFastModelIsConfigured_KeepsTheResolvedModelAtLowWithTheFullOutputBudget()
    {
        // Every request-shape gate passes, so the only thing left is that this node names no FAST model. The turn
        // still gets the FAST ladder: same model, `low`, and the output budget that keeps the reasoning cap honest.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(expected: 4096, decision.MaxOutputTokens);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnset, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenTheSendPinsItsOwnOutputBudget_KeepsItAndSaysSo()
    {
        // A developer-gated per-send max-output-tokens is an explicit ceiling; the dispatcher's is a default, so the
        // send wins and the reason records that the FAST budget was not applied.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true, hasExplicitOutputBudget: true));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Null(decision.MaxOutputTokens);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnset + ReasoningDispatchReasons.ExplicitBudgetKeptSuffix, decision.ReasonCode);
    }

    [Test]
    public async Task Dispatch_WhenTierIsNotFast_IgnoresAnExplicitOutputBudget()
    {
        // Only FAST carries a budget, so the suffix must not appear anywhere else.
        var decision = await DispatchAsync(Request(new string('a', 300), hasExplicitOutputBudget: true));

        AssertEx.Equal(ReasoningTier.Normal, decision.Tier);
        AssertEx.Equal(ReasoningDispatchReasons.Balanced, decision.ReasonCode);
    }

    [Test]
    public async Task Dispatch_CarriesTheResolvedModelsCapabilityFlagsThroughWhenNothingIsSwapped()
    {
        var decision = await DispatchAsync(Request("thanks, noted", reasoningBudgetEnforceable: false));

        AssertEx.True(decision.SupportsThinking);
        AssertEx.False(decision.ReasoningBudgetEnforceable);
    }

    [Test]
    public async Task Dispatch_IsDeterministic()
    {
        // No clock, no randomness, no culture: the same request must produce the same decision every time.
        var request = Request("Please think it through before you answer, because the ordering of the two disposals is what actually matters.", conversationDepth: 12);

        var first = await DispatchAsync(request);
        var second = await DispatchAsync(request);

        AssertEx.Equal(first.Tier, second.Tier);
        AssertEx.Equal(first.Effort, second.Effort);
        AssertEx.Equal(first.ReasonCode, second.ReasonCode);
        AssertEx.Equal(first.MaxOutputTokens, second.MaxOutputTokens);
    }

    private static ReasoningDispatchRequest Request(string latestUserText,
        bool supportsThinking = true,
        bool reasoningBudgetEnforceable = true,
        bool allowAutoModelSwap = false,
        bool hasOrchestration = false,
        int conversationDepth = 2,
        bool hasAttachments = false,
        int offeredToolCount = 0,
        bool hasExplicitOutputBudget = false,
        bool hasSkills = false,
        bool hasResponseSchema = false,
        bool isUnattended = false)
    {
        return new ReasoningDispatchRequest(ResolvedModel,
            supportsThinking,
            reasoningBudgetEnforceable,
            allowAutoModelSwap,
            hasOrchestration,
            conversationDepth,
            latestUserText,
            hasAttachments,
            offeredToolCount,
            hasExplicitOutputBudget,
            hasSkills,
            hasResponseSchema,
            isUnattended);
    }

    private static Task<ReasoningDispatchDecision> DispatchAsync(ReasoningDispatchRequest request, IModelTrustResolver? trustResolver = null)
    {
        var sut = new DefaultReasoningEffortDispatcher(trustResolver ?? CreateTrustResolver(ModelTrustLocality.Local));
        return sut.DispatchAsync(request, CancellationToken.None);
    }

    private static IModelTrustResolver CreateTrustResolver(ModelTrustLocality locality)
    {
        var resolver = Substitute.For<IModelTrustResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(locality));
        return resolver;
    }
}
