namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Conservative character-count token estimator: ~1 token per four characters plus a small
///     fixed per-message framing overhead. It never calls the provider, so it is deterministic and allocation-light on
///     the streaming hot path. It intentionally over- rather than under-estimates (the framing overhead and the coarse
///     divisor bias upward) so the budgeter trims early rather than overrunning the launched context window. A
///     provider-accurate implementation of <see cref="ITokenEstimator" /> can replace this later without touching the
///     budgeting policy.
/// </summary>
/// <remarks>
///     Per-message script-category profiles are memoized by message instance in a <see cref="ConditionalWeakTable{TKey,TValue}" />
///     (no leak — the entry dies with the message). The budgeter re-estimates the same history across the two outer
///     growth points and every inner tool-loop round, and the same <see cref="ChatMessage" /> instances flow through all
///     of them (the runner appends but never mutates), so the memo turns repeated full-content scans into dictionary
///     lookups. Correct only because a <see cref="ChatMessage" /> is immutable-after-construction on these paths
///     (truncation produces a NEW instance). The final division is not memoized, so a later per-model calibration affects
///     the same message instance without rescanning content.
/// </remarks>
public sealed class HeuristicTokenEstimator : ITokenEstimator
{
    private static readonly ConditionalWeakTable<ChatMessage, TokenCharacterProfile> PerMessageCharacterProfileCache = new();
    private readonly ITokenEstimatorCalibrationStore _calibrationStore;

    // GPT/LLaMA-family byte-pair tokenizers average roughly four characters per token for English prose; using a
    // divisor of four (rather than a larger one) keeps the estimate conservative for code and non-English text where
    // tokens are shorter.
    // Every message carries role/delimiter framing the character count alone misses; a small fixed floor keeps a
    // near-empty message (e.g. a bare tool acknowledgement) from being counted as zero-cost.
    private const int PerMessageOverheadTokens = 4;

    // ponytail: flat per-image charge. llama.cpp vision costs a few hundred to ~1-2k tokens per image depending on
    // resolution and the projector's patch grid; a fixed conservative heuristic keeps images from being counted as
    // zero-cost (which would let a vision turn silently overrun the window). Upgrade path: derive from the mmproj patch
    // grid if per-image accuracy ever matters. Mirrored in ProviderMessageTokenEstimator (AI.Agent) — change both.
    private const int EstimatedTokensPerImage = 512;

    public HeuristicTokenEstimator(ITokenEstimatorCalibrationStore? calibrationStore = null)
    {
        _calibrationStore = calibrationStore ?? new TokenEstimatorCalibrationStore();
    }

    public int EstimateTokens(ChatMessage message)
    {
        return EstimateTokens(message, modelName: null);
    }

    public int EstimateTokens(ChatMessage message, string? modelName)
    {
        return EstimateTokensWithDivisor(message, ResolveDivisor(modelName));
    }

    public int ResolveDivisor(string? modelName)
    {
        return _calibrationStore.ResolveDivisor(modelName);
    }

    public double ResolveObservedCorrection(string? modelName)
    {
        return _calibrationStore.ResolveObservedCorrection(modelName);
    }

    public int EstimateTokensWithDivisor(ChatMessage message, int charsPerToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var divisor = Math.Clamp(charsPerToken,
            TokenEstimatorCalibrationStore.MinimumCharsPerToken,
            TokenEstimatorCalibrationStore.MaximumCharsPerToken);
        var profile = PerMessageCharacterProfileCache.GetValue(message, ComputeMessageCharacterProfile);
        return (profile.WeightedLength(divisor) / divisor) + PerMessageOverheadTokens + EstimateImageTokens(message);
    }

    // Fixed-cost charge for any image (DataContent) parts, added post-division because the per-image cost is not a
    // character count. Counting is cheap (a message carries few parts), so it stays outside the char-profile memo.
    private static int EstimateImageTokens(ChatMessage message)
    {
        var images = 0;
        foreach (var content in message.Contents)
        {
            if (content is DataContent data && data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                images++;
            }
        }

        return images * EstimatedTokensPerImage;
    }

    private static TokenCharacterProfile ComputeMessageCharacterProfile(ChatMessage message)
    {
        var profile = new TokenCharacterProfile();
        foreach (var content in message.Contents)
        {
            AddContent(profile, content);
        }

        return profile;
    }

    public int EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        return EstimateTokens(messages, modelName: null);
    }

    public int EstimateTokens(IReadOnlyList<ChatMessage> messages, string? modelName)
    {
        return EstimateTokensWithDivisor(messages, ResolveDivisor(modelName));
    }

    public int EstimateTokensWithDivisor(IReadOnlyList<ChatMessage> messages, int charsPerToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var total = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            total += EstimateTokensWithDivisor(messages[index], charsPerToken);
        }

        return total;
    }

    private static void AddContent(TokenCharacterProfile profile, AIContent content)
    {
        switch (content)
        {
            case TextContent text:
                profile.Add(text.Text);
                break;
            case TextReasoningContent reasoning:
                profile.Add(reasoning.Text);
                break;
            case FunctionCallContent call:
                profile.Add(call.Name);
                if (call.Arguments is { } arguments)
                {
                    foreach (var argument in arguments)
                    {
                        profile.Add(argument.Key);
                        profile.Add(argument.Value?.ToString());
                    }
                }

                break;
            case FunctionResultContent result:
                profile.Add(result.Result?.ToString());
                break;
            case DataContent:
                // Binary payload (e.g. an image): never char-count its bytes/data-URI — it is charged a fixed
                // per-image token estimate separately (see EstimateImageTokens).
                break;
            default:
                profile.Add(content.ToString());
                break;
        }
    }
}
