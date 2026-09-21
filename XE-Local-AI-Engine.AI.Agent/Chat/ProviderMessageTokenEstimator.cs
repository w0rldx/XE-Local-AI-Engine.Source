namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Conservative, allocation-light token estimator for the provider-boundary budget middleware: ~1 token per
///     <see cref="CharsPerToken" /> weighted characters plus a fixed per-message framing overhead, never calling the
///     provider.
/// </summary>
/// <remarks>
///     The AI.Agent-layer twin of <c>HeuristicTokenEstimator</c> in the application layer; the two sit in separate
///     assemblies by the layer arrow (Application → AI.Agent), so a change to divisor selection or the per-image charge
///     here MUST be mirrored there, and vice versa. Script-category weighting is shared through
///     <see cref="TokenCharacterProfile" />; profiles are memoized per instance in a
///     <see cref="ConditionalWeakTable{TKey,TValue}" />. See docs/wiki/04-agent-mode.md ("The token estimator").
/// </remarks>
internal static class ProviderMessageTokenEstimator
{
    private static readonly ConditionalWeakTable<ChatMessage, TokenCharacterProfile> PerMessageCharacterProfileCache = new();

    private static readonly ConditionalWeakTable<AITool, TokenCharacterProfile> PerToolCharacterProfileCache = new();

    private const int CharsPerToken = TokenEstimatorCalibrationStore.DefaultCharsPerToken;
    private const int PerMessageOverheadTokens = 4;

    // simplified: flat per-image charge — llama.cpp vision costs a few hundred to ~1-2k tokens per image by resolution and
    // patch grid, and zero-cost would overrun the window. Mirrored in HeuristicTokenEstimator (Application) — change both.
    private const int EstimatedTokensPerImage = 512;

    public static int EstimateTokens(ChatMessage message, int charsPerToken = CharsPerToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var divisor = ClampDivisor(charsPerToken);
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

    public static int EstimateTokens(IReadOnlyList<ChatMessage> messages, int charsPerToken = CharsPerToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var divisor = ClampDivisor(charsPerToken);
        var total = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            total += EstimateTokens(messages[index], divisor);
        }

        return total;
    }

    /// <summary>Weighted-character count of a free-text span, treated as a token estimate for instructions / system prompt.</summary>
    public static int EstimateTokens(string? text, int charsPerToken = CharsPerToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var divisor = ClampDivisor(charsPerToken);
        var profile = new TokenCharacterProfile();
        profile.Add(text);
        return (profile.WeightedLength(divisor) / divisor) + PerMessageOverheadTokens;
    }

    /// <summary>
    ///     Conservative token estimate for the tool definitions serialized into the request, on the same weighted-char
    ///     divisor and per-item framing overhead as message content.
    /// </summary>
    /// <remarks>
    ///     Each tool's name, description and JSON schema counts against the input window; ignored entirely they
    ///     under-count the round and let an over-window request through. Memoized by tool instance because the tool list
    ///     is immutable for the life of an invocation while this hop runs on EVERY round — without it each round paid a
    ///     full <c>GetRawText()</c> materialization plus a char scan of every schema. The divisor is applied outside the
    ///     memo, so a per-model calibration change re-divides without rescanning.
    /// </remarks>
    public static int EstimateTools(IEnumerable<AITool>? tools, int charsPerToken = CharsPerToken)
    {
        if (tools is null)
        {
            return 0;
        }

        var divisor = ClampDivisor(charsPerToken);
        var total = 0;
        foreach (var tool in tools)
        {
            var profile = PerToolCharacterProfileCache.GetValue(tool, ComputeToolCharacterProfile);
            total += (profile.WeightedLength(divisor) / divisor) + PerMessageOverheadTokens;
        }

        return total;
    }

    private static TokenCharacterProfile ComputeToolCharacterProfile(AITool tool)
    {
        var profile = new TokenCharacterProfile();
        profile.Add(tool.Name);
        profile.Add(tool.Description);
        if (tool is AIFunction function && function.JsonSchema.ValueKind != JsonValueKind.Undefined)
        {
            profile.Add(function.JsonSchema.GetRawText());
        }

        return profile;
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

    private static int ClampDivisor(int charsPerToken)
    {
        return Math.Clamp(charsPerToken,
            TokenEstimatorCalibrationStore.MinimumCharsPerToken,
            TokenEstimatorCalibrationStore.MaximumCharsPerToken);
    }
}
