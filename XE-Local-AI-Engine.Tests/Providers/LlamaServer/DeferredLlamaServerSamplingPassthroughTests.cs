namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The developer-gated per-send sampling overrides must actually reach llama.cpp. The MEAI OpenAI adapter maps only
///     Temperature/TopP/penalties/MaxOutputTokens/Seed/StopSequences — <see cref="ChatOptions.TopK" /> has no OpenAI
///     counterpart and unrecognised <see cref="ChatOptions.AdditionalProperties" /> are dropped — so
///     <see cref="DeferredLlamaServerChatClient.ApplySamplingPassthrough" /> patches the four missing knobs onto the body
///     via <c>ChatCompletionOptions.Patch</c>. These tests run the REAL MEAI OpenAI pipeline over a request-capturing
///     transport (<see cref="LlamaGrammarToolOffer.CaptureWireBodyAsync" />) and grade the serialized bytes, not a
///     reimplementation of the adapter.
/// </summary>
public sealed class DeferredLlamaServerSamplingPassthroughTests
{
    [Test]
    public async Task ApplySamplingPassthrough_KnobsSet_WritesAllFourAsTopLevelWireFields()
    {
        var options = new ChatOptions
        {
            TopK = 55,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [SamplingOptionKeys.MinP] = 0.07f,
                [SamplingOptionKeys.RepeatPenalty] = 1.25f,
                [SamplingOptionKeys.RepeatLastN] = -1,
                // num_ctx must NEVER ride the llama.cpp wire — the server's window is fixed at launch.
                [SamplingOptionKeys.NumCtx] = 8192
            }
        };

        // Baseline: the SAME options straight through the adapter carry none of the four — this is the defect the
        // passthrough exists to fix, so it also proves the assertions below are load-bearing.
        var unpatchedBody = await LlamaGrammarToolOffer.CaptureWireBodyAsync(options, CancellationToken.None);
        using (var unpatched = JsonDocument.Parse(unpatchedBody))
        {
            foreach (var field in new[] { "top_k", SamplingOptionKeys.MinP, SamplingOptionKeys.RepeatPenalty, SamplingOptionKeys.RepeatLastN })
            {
                AssertEx.False(unpatched.RootElement.TryGetProperty(field, out _),
                    $"the MEAI OpenAI adapter is expected to drop {field}; if it maps it now the passthrough is redundant.");
            }
        }

        var body = await LlamaGrammarToolOffer.CaptureWireBodyAsync(
            AssertEx.NotNull(DeferredLlamaServerChatClient.ApplySamplingPassthrough(options)),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        AssertEx.Equal(expected: 55, root.GetProperty("top_k").GetInt32());
        AssertEx.Equal(expected: 0.07f, root.GetProperty("min_p").GetSingle());
        AssertEx.Equal(expected: 1.25f, root.GetProperty("repeat_penalty").GetSingle());
        AssertEx.Equal(expected: -1, root.GetProperty("repeat_last_n").GetInt32());
        AssertEx.False(root.TryGetProperty("num_ctx", out _),
            "num_ctx is not honoured per request by llama-server and must never be patched onto the body.");
    }

    [Test]
    public async Task ApplySamplingPassthrough_NoKnobsSet_ReturnsOptionsUnchangedAndBodyCarriesNoSamplingFields()
    {
        // Temperature IS mapped by the adapter, so its presence proves the request was built normally while the
        // unmapped knobs stay absent.
        var options = new ChatOptions
        {
            Temperature = 0.4f
        };

        var passthrough = DeferredLlamaServerChatClient.ApplySamplingPassthrough(options);
        AssertEx.True(ReferenceEquals(options, passthrough),
            "with no unmapped sampling knob set the options must be returned unchanged (byte-identical path).");

        var body = await LlamaGrammarToolOffer.CaptureWireBodyAsync(AssertEx.NotNull(passthrough),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        AssertEx.False(root.TryGetProperty("top_k", out _), "an unset TopK must not appear on the wire.");
        AssertEx.False(root.TryGetProperty("min_p", out _), "an unset min_p must not appear on the wire.");
        AssertEx.False(root.TryGetProperty("repeat_penalty", out _), "an unset repeat_penalty must not appear on the wire.");
        AssertEx.False(root.TryGetProperty("repeat_last_n", out _), "an unset repeat_last_n must not appear on the wire.");
    }

    [Test]
    public async Task ApplySamplingPassthrough_ComposesWithPriorRawRepresentationFactory()
    {
        // The real composition this ships with: reasoning-off sets chat_template_kwargs via its own factory, and the
        // sampling pass must add to it rather than replace it.
        var options = new ChatOptions
        {
            TopK = 12,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.DisableThinkingMarkerKey] = true,
                [SamplingOptionKeys.MinP] = 0.02f
            }
        };

        var patched = DeferredLlamaServerChatClient.ApplySamplingPassthrough(
            DeferredLlamaServerChatClient.ApplyThinkingSwitch(options));

        var body = await LlamaGrammarToolOffer.CaptureWireBodyAsync(AssertEx.NotNull(patched),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        AssertEx.Equal(expected: 12, root.GetProperty("top_k").GetInt32());
        AssertEx.Equal(expected: 0.02f, root.GetProperty("min_p").GetSingle());
        AssertEx.Equal(expected: false,
            root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }
}
