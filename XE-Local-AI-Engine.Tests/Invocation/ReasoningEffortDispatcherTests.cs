namespace XE_Local_AI_Engine.Tests.Invocation;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The deterministic <c>auto</c> ladder: every signal in isolation, both hard rules, both tie-break boundaries,
///     both language phrase sets, and the swap gates. The dispatcher never fails a turn — under any refusal it falls
///     back to the resolved model at a lower effort — so every case here asserts a decision, never an exception.
/// </summary>
public sealed class ReasoningEffortDispatcherTests
{
    private const string ResolvedModel = "qwen3.8-27b";

    private const string FastModel = "qwen3-1.7b";

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
    public async Task Dispatch_WhenModelIsBinaryAndTierIsFast_CarriesNoOutputBudgetAndNeverConsultsTheTrustResolver()
    {
        // No tier caps the output any more, so the binary branch is a pure ladder mapping: it needs no locality lookup
        // and hands the send nothing to change.
        var trustResolver = CreateTrustResolver(ModelTrustLocality.Local);
        var decision = await DispatchAsync(Request("hi", supportsThinking: false), trustResolver);

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Null(decision.MaxOutputTokens);
        await trustResolver.DidNotReceive().ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
    public async Task Dispatch_WhenAFastPhraseAppearsOnlyInsideAFence_DoesNotFire()
    {
        // A pasted snippet is code the user wants looked at, not an instruction about how hard to think about it.
        // Both phrase lists therefore read the PROSE only. The message is long AND fenced, so it scores Deep; a
        // `briefly` read out of the snippet would subtract 2 and silently pull the same turn down to Normal.
        var fencedPhrase = await DispatchAsync(Request(LongText + "\n```\nlogger.LogDebug(\"briefly\");\n```\n"));

        AssertEx.Equal(ReasoningTier.Deep, fencedPhrase.Tier, "a fast phrase inside a snippet must not move the score");
        AssertEx.Equal(ReasoningDispatchReasons.CodeFence, fencedPhrase.ReasonCode);

        // The control: the same phrase in the PROSE around the same fence still counts.
        var prosePhrase = await DispatchAsync(Request(LongText + "\n```\nlogger.LogDebug(1);\n```\nAnswer briefly."));

        AssertEx.Equal(ReasoningTier.Normal, prosePhrase.Tier);
    }

    [Test]
    public async Task Dispatch_WhenAFenceIsNeverClosed_TreatsTheRestAsCode()
    {
        // An unclosed fence swallows the remainder, which is the safe direction: everything after an opener is code
        // until proven otherwise, so a phrase inside a truncated paste cannot move the score either.
        var decision = await DispatchAsync(Request(LongText + "\n```\nassert(quick answer == 1);"));

        AssertEx.Equal(ReasoningTier.Deep, decision.Tier);
        AssertEx.Equal(ReasoningDispatchReasons.CodeFence, decision.ReasonCode);
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
    public async Task Dispatch_WhenAPhraseAdjoinsASupplementaryPlaneLetter_IsStillEmbeddedAndDoesNotFire()
    {
        // A letter outside the BMP is a UTF-16 surrogate PAIR, and neither half is a letter on its own — so a
        // per-code-unit boundary check called every such letter a word boundary and let the vocabulary fire mid-word.
        // U+10400 is DESERET CAPITAL LETTER LONG I: a cased letter whose upper-case folding is itself, so the phrase
        // survives the fold and only the boundary rule decides the outcome. Both sides of the phrase are checked.
        //
        // These texts sit one point below Deep on the deep-conversation bonus alone, so a phrase that wrongly fires
        // adds its +2 and pushes the tier over — which is what makes the tier, not the reason, the observable here.
        const string Tail = " the power supply on the bench keeps resetting whenever the second fan spins up and nobody on the team can explain it yet";

        var leading = await DispatchAsync(Request($"\U00010400carefully{Tail}", conversationDepth: 12));
        var trailing = await DispatchAsync(Request($"carefully\U00010400{Tail}", conversationDepth: 12));

        AssertEx.Equal(ReasoningTier.Normal, leading.Tier, "the phrase is inside a longer word and must not score");
        AssertEx.Equal(ReasoningTier.Normal, trailing.Tier, "the phrase is inside a longer word and must not score");

        // The control: the same phrase, same text, standing as its own word still fires — so the fix cannot pass by
        // simply never matching anything.
        var standalone = await DispatchAsync(Request($"carefully{Tail}", conversationDepth: 12));

        AssertEx.Equal(ReasoningTier.Deep, standalone.Tier);
        AssertEx.Equal(ReasoningDispatchReasons.DeepPhrase, standalone.ReasonCode);
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
    public async Task Fast_WhenResolvedModelIsCloudOrUnresolved_NeverSwaps(ModelTrustLocality locality)
    {
        // `Unresolved` is treated exactly as `Cloud` by every gate, so the `== Local` comparison is fail-closed by
        // construction. A cloud turn keeps the ladder but never the swap.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true), CreateTrustResolver(locality));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Null(decision.MaxOutputTokens);
        AssertEx.Equal(ReasoningDispatchReasons.CloudNoSwap, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenNoFastModelIsConfigured_KeepsTheResolvedModelAtLowWithNoOutputBudget()
    {
        // Every request-shape gate passes, so the only thing left is that this node names no FAST model. The turn
        // still gets the FAST ladder: same model, `low`, and a send shaped exactly like a non-`auto` one.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Null(decision.MaxOutputTokens);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnset, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenFastModelIsExternal_NeverSwaps()
    {
        // Enforcement point 2 of the node-locality gate. A model can be uninstalled, or an external connection
        // re-declared, between the save that validated the setting and this turn — so the pair is checked again here.
        // An external server is a process the node does not own: sending this turn there is egress the upstream gate
        // never authorised.
        // Even an external connection the operator DECLARED local resolves node-local on trust alone, which is why the
        // provider is the second half of the pair: no llama.cpp process backs it, so neither the capacity gate nor the
        // liveness probe could reason about it.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: "ext:studio/qwen3-1.7b",
            fastModelLocality: ModelTrustLocality.Local,
            fastModelProviderName: "external");

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelNotLocal, decision.ReasonCode);
        AssertEx.Null(decision.CapacityReservation);
    }

    [Test]
    public async Task Fast_WhenFastModelIsCloud_NeverSwaps()
    {
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: "gpt-5.6-terra",
            fastModelLocality: ModelTrustLocality.Cloud);

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelNotLocal, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenFastModelIsNotServedByLlamaCpp_NeverSwaps()
    {
        // Node-local is not enough: an Ollama-served model is local but the swap targets a llama.cpp process, which is
        // the only provider the capacity gate and the liveness probe can reason about.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: "qwen3:1.7b",
            fastModelLocality: ModelTrustLocality.Local,
            fastModelProviderName: "ollama");

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelNotLocal, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenFastModelIsNotInstalled_NeverSwaps()
    {
        // The gate the live round found inert. `ModelTrustResolver` classifies a scheme-less id as Local whenever no
        // cloud provider is selected for it, and `LocalModelProviderResolver` routes an unmapped id to the default
        // provider, which is llamacpp — so on a node with no cloud provider configured that pair admits EVERY string.
        // Registry membership is what refuses one, and it refuses it HERE, by name, instead of at warm time under
        // `fast-model-unavailable` after a wasted swap.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: "gpt-4o-mini",
            fastModelLocality: ModelTrustLocality.Local,
            fastModelInstalled: false);

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelNotLocal, decision.ReasonCode);
        AssertEx.Null(decision.CapacityReservation);
    }

    [Test]
    public async Task Fast_WhenTheFastModelIsTheResolvedModel_NeverSwaps()
    {
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: ResolvedModel,
            fastModelLocality: ModelTrustLocality.Local);

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelIsActiveModel, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenCapacityRejects_FallsBackToSameModelLow()
    {
        // The dispatcher never fails a turn. A node with no room for a second chat process serves the FAST tier with
        // the model it already has, and the notice says why the swap did not happen.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local,
            capacityDecision: new CapacityDecision(CapacityVerdict.RejectInsufficient, "no room", OllamaEvictionWarning: false));

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelNoCapacity, decision.ReasonCode);
        AssertEx.Null(decision.CapacityReservation);
    }

    [Test]
    [Arguments(false, true, ReasoningDispatchReasons.ModelPinned)]
    [Arguments(true, false, ReasoningDispatchReasons.ModelPinned)]
    public async Task Fast_WhenAllowAutoModelSwapIsFalse_NeverSwaps(bool userPicked, bool agentPinned, string expectedReason)
    {
        // Both shapes the provenance covers — an explicit user pick and an honoured agent pin — arrive here as the
        // same single false flag, because the runtime package keeps the EFFECTIVE model and not how it was chosen.
        // The gate must refuse before any node-side lookup, so a configured fast model changes nothing.
        AssertEx.True(userPicked || agentPinned, "each argument set is one of the two pinned shapes");

        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: false),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local);

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(expectedReason, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenSwapAdmitted_CarriesTheReservationAndTheFastModelsOwnCapabilities()
    {
        // An Allow verdict books the fast model's bytes and one loaded-process slot; the RUNNER owns releasing that
        // reservation at turn end (InvocationRunnerTests pins the disposal order). The capability flags are re-resolved
        // for the REPLACEMENT: a stale ReasoningBudgetEnforceable sends a budget the new model 400s on.
        using var reservation = new StubDisposable();
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local,
            capacityDecision: new CapacityDecision(CapacityVerdict.Allow, "ok", OllamaEvictionWarning: false, reservation),
            fastModelCapabilities: new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: false, IsCloud: false)
            {
                ReasoningBudgetEnforceable = false
            });

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal(FastModel, decision.Model);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Null(decision.MaxOutputTokens);
        AssertEx.True(decision.SupportsThinking);
        AssertEx.False(decision.ReasoningBudgetEnforceable, "the replacement's own flag, not the resolved model's");
        AssertEx.Equal(ReasoningDispatchReasons.ShortTurn, decision.ReasonCode, "every gate passed, so the reason names the tier signal");
        AssertEx.Equal(reservation, decision.CapacityReservation);
        AssertEx.False(reservation.Disposed, "the dispatcher hands the reservation over; the runner releases it");
    }

    [Test]
    public async Task Fast_WhenTheSwappedModelIsBinary_SendsTheBinaryEffort()
    {
        // The replacement's ladder, not the resolved model's: a fast model with no graded thinking gets `none`.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local,
            fastModelCapabilities: new ModelCapabilitySnapshot(SupportsThinking: false, SupportsTools: false, IsCloud: false));

        AssertEx.Equal(FastModel, decision.Model);
        AssertEx.Equal("none", decision.Effort);
        AssertEx.Null(decision.MaxOutputTokens);
    }

    [Test]
    public async Task Fast_WhenQueueSameModelAndLeaseIsProfilingOwned_NeverSwaps()
    {
        // Capacity short-circuits to QueueSameModel off a running-process snapshot that does NOT filter
        // profiling-owned processes, so "already running" is not yet "can serve this turn". The lease is the interlock
        // that answers that, and a profiling-owned process would contaminate a measurement and then be torn down.
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local,
            capacityDecision: QueueSameModel(),
            leaseAcquisition: LlamaServerLeaseAcquisition.ProfilingOwned);

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnavailable, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenQueueSameModelAndLeaseIsEvicting_NeverSwaps()
    {
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local,
            capacityDecision: QueueSameModel(),
            leaseAcquisition: LlamaServerLeaseAcquisition.Evicting);

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnavailable, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenQueueSameModelAndTheProcessIsGone_NeverSwaps()
    {
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local,
            capacityDecision: QueueSameModel(),
            leaseAcquisition: LlamaServerLeaseAcquisition.NotRunning);

        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnavailable, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenQueueSameModelAndLeaseIsGranted_SwapsAndDisposesTheProbeLease()
    {
        // The probe lease is a refcount over the running process; the send takes its own, so this one is released the
        // moment its shape has been read. Holding it would keep an operator eject draining for the whole turn.
        using var probeLease = new StubInferenceLease();
        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local,
            capacityDecision: QueueSameModel(),
            leaseAcquisition: LlamaServerLeaseAcquisition.Granted(probeLease));

        AssertEx.Equal(FastModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.ShortTurn, decision.ReasonCode);
        AssertEx.True(probeLease.Disposed, "the probe lease must be released immediately");
        AssertEx.Null(decision.CapacityReservation, "a queued swap loads nothing, so there is nothing to book");
    }

    [Test]
    public async Task Fast_WhenANodeSideLookupThrows_FallsBackToSameModelLow()
    {
        // The dispatcher's contract is that it never fails a turn. A settings read, a provider lookup or a capacity
        // probe that throws means "this node cannot serve a swap right now", which is what the reason says.
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetAutoEffortFastModelNameAsync(Arg.Any<CancellationToken>())
                       .Returns<Task<string?>>(_ => throw new InvalidOperationException("settings unavailable"));

        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true), runtimeSettings: runtimeSettings);

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnavailable, decision.ReasonCode);
    }

    [Test]
    public async Task Fast_WhenTheResolvedModelsTrustLookupThrows_FallsBackToSameModelLowAndNeverFailsTheTurn()
    {
        // The FIRST swap gate is a node-side call like every other one, so it belongs inside the same fail-soft
        // boundary. Sitting outside it, a trust store that was briefly unreachable failed an otherwise serviceable
        // turn — the one thing this dispatcher's contract says it may never do.
        var trustResolver = Substitute.For<IModelTrustResolver>();
        trustResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns<Task<ModelTrustLocality>>(static _ => throw new InvalidOperationException("trust store unavailable"));

        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true), trustResolver);

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Equal(ResolvedModel, decision.Model);
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnavailable, decision.ReasonCode);
        AssertEx.Null(decision.CapacityReservation);
    }

    [Test]
    public async Task Fast_WhenTheFastModelsCapabilityLookupThrowsAfterAdmission_ReleasesTheReservationAndDegrades()
    {
        // Ownership of the reservation transfers ONLY with a returned decision. Admission has already booked the fast
        // model's bytes and a loaded-process slot, and the capability re-resolution is the last node-side call before
        // the runner receives it — so a failure there must release the booking rather than strand it for the process's
        // lifetime, and must still not fail the turn.
        using var reservation = new StubDisposable();
        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                          .Returns<Task<ModelCapabilitySnapshot>>(static _ => throw new InvalidOperationException("capability probe failed"));

        var decision = await DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local,
            capacityDecision: new CapacityDecision(CapacityVerdict.Allow, "ok", OllamaEvictionWarning: false, reservation),
            capabilityResolver: capabilityResolver);

        AssertEx.Equal(ReasoningTier.Fast, decision.Tier);
        AssertEx.Equal("low", decision.Effort);
        AssertEx.Equal(ResolvedModel, decision.Model, "the turn degrades onto the model it was authorised for");
        AssertEx.Equal(ReasoningDispatchReasons.FastModelUnavailable, decision.ReasonCode);
        AssertEx.Null(decision.CapacityReservation, "a degraded decision owns nothing");
        AssertEx.Equal(expected: 1, reservation.DisposeCount, "released exactly once — never leaked, never double-released");
    }

    [Test]
    public async Task Fast_WhenTheFastModelsCapabilityLookupIsCancelledAfterAdmission_ReleasesTheReservationAndPropagates()
    {
        // A cancellation means the TURN is terminating, so it propagates exactly as it does out of the swap ladder
        // rather than degrading into a decision nobody will send. The booking is still ours to release on the way out.
        using var reservation = new StubDisposable();
        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                          .Returns<Task<ModelCapabilitySnapshot>>(static _ => throw new OperationCanceledException());

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => DispatchAsync(Request("thanks, noted", allowAutoModelSwap: true),
            fastModelName: FastModel,
            fastModelLocality: ModelTrustLocality.Local,
            capacityDecision: new CapacityDecision(CapacityVerdict.Allow, "ok", OllamaEvictionWarning: false, reservation),
            capabilityResolver: capabilityResolver));

        AssertEx.Equal(expected: 1, reservation.DisposeCount, "released exactly once — never leaked, never double-released");
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
            hasSkills,
            hasResponseSchema,
            isUnattended);
    }

    private static Task<ReasoningDispatchDecision> DispatchAsync(ReasoningDispatchRequest request,
        IModelTrustResolver? trustResolver = null,
        string? fastModelName = null,
        string? fastModelProviderName = null,
        ModelTrustLocality? fastModelLocality = null,
        CapacityDecision? capacityDecision = null,
        LlamaServerLeaseAcquisition? leaseAcquisition = null,
        ModelCapabilitySnapshot? fastModelCapabilities = null,
        INodeRuntimeSettings? runtimeSettings = null,
        IModelCapabilityResolver? capabilityResolver = null,
        bool fastModelInstalled = true)
    {
        // The resolved model is Local unless a test says otherwise. The FAST model's locality/provider default to the
        // same answer, so a test names only the gate it is exercising.
        trustResolver ??= CreateTrustResolver(ModelTrustLocality.Local);
        if (fastModelName is not null && fastModelLocality is { } fastLocality)
        {
            trustResolver.ResolveAsync(fastModelName, Arg.Any<CancellationToken>()).Returns(Task.FromResult(fastLocality));
        }

        if (runtimeSettings is null)
        {
            runtimeSettings = Substitute.For<INodeRuntimeSettings>();
            runtimeSettings.GetAutoEffortFastModelNameAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(fastModelName));
        }

        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(fastModelProviderName ?? LlamaServerProviderConstants.ProviderName));

        // Registry membership is the load-bearing half of the locality gate: both resolvers above default an unknown
        // id to node-local llama.cpp, so without this an arbitrary string would swap. Installed by default so a test
        // names only the gate it is exercising.
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        ggufModelStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(fastModelInstalled));

        var capacityService = Substitute.For<ICapacityService>();
        capacityService.DecideAsync(Arg.Any<CapacityRequest>(), Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult(capacityDecision ?? new CapacityDecision(CapacityVerdict.Allow, "ok", OllamaEvictionWarning: false)));

        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireInferenceLease(Arg.Any<string>(), Arg.Any<ModelRole>())
                  .Returns(leaseAcquisition ?? LlamaServerLeaseAcquisition.NotRunning);

        if (capabilityResolver is null)
        {
            capabilityResolver = Substitute.For<IModelCapabilityResolver>();
            capabilityResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                              .Returns(Task.FromResult(fastModelCapabilities
                                                       ?? new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false)
                                                       {
                                                           ReasoningBudgetEnforceable = true
                                                       }));
        }

        var sut = new DefaultReasoningEffortDispatcher(trustResolver,
            runtimeSettings,
            providerResolver,
            capacityService,
            capabilityResolver,
            supervisor,
            ggufModelStore);
        return sut.DispatchAsync(request, CancellationToken.None);
    }

    private static CapacityDecision QueueSameModel() =>
        new(CapacityVerdict.QueueSameModel, "already running", OllamaEvictionWarning: false);

    private sealed class StubDisposable : IDisposable
    {
        /// <summary>Counted, not flagged: "released exactly once" is the assertion a leak fix has to make.</summary>
        public int DisposeCount { get; private set; }

        public bool Disposed => DisposeCount > 0;

        public void Dispose() =>
            DisposeCount++;
    }

    private sealed class StubInferenceLease : ILlamaServerInferenceLease
    {
        public bool Disposed { get; private set; }

        public bool WasEjected => false;

        public void Dispose() =>
            Disposed = true;
    }

    private static IModelTrustResolver CreateTrustResolver(ModelTrustLocality locality)
    {
        var resolver = Substitute.For<IModelTrustResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(locality));
        return resolver;
    }
}
