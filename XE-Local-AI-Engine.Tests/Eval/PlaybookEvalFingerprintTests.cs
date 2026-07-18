namespace XE_Local_AI_Engine.Tests.Eval;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Fingerprint composition tests for the model-IDENTITY term (RAG-03). The fingerprint keys on the eval model's
///     resolved weight identity in addition to its name, so a model updated under the SAME name (an Ollama / llama.cpp
///     weight swap) moves the fingerprint and can no longer authorize a promotion recorded against the old weights.
/// </summary>
public sealed class PlaybookEvalFingerprintTests
{
    private static readonly Guid ActionId = Guid.NewGuid();
    private const string Instructions = "Base instructions.";
    private const string ModelName = "eval-model";

    private static readonly IReadOnlyList<PlaybookActionRecord> NoActions = [];
    private static readonly IReadOnlyList<GoldenConversationRecord> NoGolden = [];

    [Test]
    public void Compute_SameNameDifferentIdentity_ProducesDifferentFingerprint()
    {
        // Same model NAME, different weight IDENTITY (a same-name re-download/re-pull). The fingerprint must differ so a
        // pass recorded against the old weights cannot authorize a promotion after the swap.
        var before = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, ModelName, "gguf-sha256:aaaa");
        var after = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, ModelName, "gguf-sha256:bbbb");

        AssertEx.NotEqual(before, after);
    }

    [Test]
    public void Compute_SameNameSameIdentity_IsStable()
    {
        // No swap: identical name AND identity recompute to the SAME fingerprint, so a verified eval still matches at
        // promote time.
        var first = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, ModelName, "gguf-sha256:aaaa");
        var second = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, ModelName, "gguf-sha256:aaaa");

        AssertEx.Equal(first, second);
    }

    [Test]
    public void Compute_UnverifiedIdentity_IsDistinctFromAnyVerifiedIdentityOfSameName()
    {
        // An unresolvable identity (the "unverified" sentinel) must never collide with a verified identity of the same
        // name — a verified pass and an identity-unverifiable run are different states.
        var unverified = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, ModelName, EvalModelIdentity.UnverifiedToken);
        var verified = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, ModelName, "gguf-sha256:aaaa");

        AssertEx.NotEqual(unverified, verified);
    }

    [Test]
    public void Compute_TwoUnverifiedRuns_AreStableToEachOther()
    {
        // Two identity-unverifiable runs of the same name still match each other (the sentinel is deterministic), so the
        // gate does not spuriously block when identity simply cannot be resolved at either point.
        var first = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, ModelName, EvalModelIdentity.UnverifiedToken);
        var second = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, ModelName, EvalModelIdentity.UnverifiedToken);

        AssertEx.Equal(first, second);
    }

    [Test]
    public void Compute_DifferentNameSameIdentity_ProducesDifferentFingerprint()
    {
        // The name term is retained alongside identity: a re-point to a different model name still moves the fingerprint
        // even if the (coincidental) identity token were equal.
        var a = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, "model-a", "gguf-sha256:aaaa");
        var b = PlaybookEvalFingerprint.Compute(ActionId, 1, Instructions, NoActions, NoGolden, "model-b", "gguf-sha256:aaaa");

        AssertEx.NotEqual(a, b);
    }
}
