namespace XE_Local_AI_Engine.Tests.NodeSettings;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The usage-cost rate resolver precedence (Wave 13): local runtimes (llama.cpp / Ollama) are always free; otherwise
///     an operator override for the model name wins, then the built-in default table, then zero (unknown / unpriced).
///     Model names match case-insensitively; the constructor defensively drops blank keys and negative / non-finite rates.
/// </summary>
public sealed class UsageRateResolverTests
{
    private static IUsageRateResolver WithOverrides(params (string Model, double Input, double Output)[] entries)
    {
        var models = new Dictionary<string, ModelRate>();
        foreach (var (model, input, output) in entries)
        {
            models[model] = new ModelRate
            {
                InputPer1M = input,
                OutputPer1M = output
            };
        }

        return UsageRateResolver.FromSettings(new NodeUsageRateSettings
        {
            Models = models
        });
    }

    [Test]
    public void Resolve_LocalProvider_IsFree_EvenForAPricedModel()
    {
        // gpt-5 has a default-table rate, but a local run of it is still free — the provider gate wins.
        var rate = UsageRateResolver.FromSettings(settings: null).Resolve(AgentUsageProviders.Local, "gpt-5");

        AssertEx.Equal(expected: 0d, rate.InputPer1M);
        AssertEx.Equal(expected: 0d, rate.OutputPer1M);
    }

    [Test]
    public void Resolve_OllamaProvider_IsFree()
    {
        var rate = WithOverrides(("gpt-5", 5, 5)).Resolve(AgentUsageProviders.Ollama, "gpt-5");

        AssertEx.Equal(expected: 0d, rate.InputPer1M);
        AssertEx.Equal(expected: 0d, rate.OutputPer1M);
    }

    [Test]
    public void Resolve_CloudProvider_UsesDefaultTable_WhenNoOverride()
    {
        // gpt-5 is in the built-in default table; a codex run with no operator override picks it up.
        var rate = UsageRateResolver.FromSettings(settings: null).Resolve(AgentUsageProviders.Codex, "gpt-5");

        AssertEx.Equal(expected: 1.25d, rate.InputPer1M);
        AssertEx.Equal(expected: 10d, rate.OutputPer1M);
    }

    [Test]
    public void Resolve_OperatorOverride_WinsOverDefaultTable()
    {
        var rate = WithOverrides(("gpt-5", 3, 7)).Resolve(AgentUsageProviders.Codex, "gpt-5");

        AssertEx.Equal(expected: 3d, rate.InputPer1M);
        AssertEx.Equal(expected: 7d, rate.OutputPer1M);
    }

    [Test]
    public void Resolve_UnknownModelOnCloudProvider_IsZero()
    {
        var rate = WithOverrides(("gpt-5", 3, 7)).Resolve(AgentUsageProviders.Azure, "not-in-any-table");

        AssertEx.Equal(expected: 0d, rate.InputPer1M);
        AssertEx.Equal(expected: 0d, rate.OutputPer1M);
    }

    [Test]
    public void Resolve_BlankModel_IsZero()
    {
        var rate = WithOverrides(("gpt-5", 3, 7)).Resolve(AgentUsageProviders.Codex, "   ");

        AssertEx.Equal(expected: 0d, rate.InputPer1M);
        AssertEx.Equal(expected: 0d, rate.OutputPer1M);
    }

    [Test]
    public void Resolve_ModelName_IsCaseInsensitive()
    {
        // Override keyed "GPT-5" must match a bucket model name "gpt-5" (aggregate ModelName casing is not guaranteed).
        var rate = WithOverrides(("GPT-5", 4, 8)).Resolve(AgentUsageProviders.Codex, "gpt-5");

        AssertEx.Equal(expected: 4d, rate.InputPer1M);
        AssertEx.Equal(expected: 8d, rate.OutputPer1M);
    }

    [Test]
    public void FromSettings_DropsNegativeAndNonFiniteRates_KeepsValid()
    {
        // Belt-and-suspenders at the resolver boundary (the store's Normalize is the authority): a negative and a NaN
        // entry are dropped (fall through to the default table / zero), a valid override is kept.
        var resolver = WithOverrides(("bad-negative", -1, 5),
            ("bad-nan", double.NaN, 5),
            ("good", 2, 3));

        var negative = resolver.Resolve(AgentUsageProviders.Codex, "bad-negative");
        AssertEx.Equal(expected: 0d, negative.InputPer1M);
        AssertEx.Equal(expected: 0d, negative.OutputPer1M);

        var nan = resolver.Resolve(AgentUsageProviders.Codex, "bad-nan");
        AssertEx.Equal(expected: 0d, nan.InputPer1M);
        AssertEx.Equal(expected: 0d, nan.OutputPer1M);

        var good = resolver.Resolve(AgentUsageProviders.Codex, "good");
        AssertEx.Equal(expected: 2d, good.InputPer1M);
        AssertEx.Equal(expected: 3d, good.OutputPer1M);
    }
}
