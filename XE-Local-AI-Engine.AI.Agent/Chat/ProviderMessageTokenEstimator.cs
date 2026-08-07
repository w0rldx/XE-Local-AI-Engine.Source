namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Conservative, allocation-light token estimator for the provider-boundary budget middleware: ~1 token per
///     <see cref="CharsPerToken" /> weighted characters plus a small fixed per-message framing overhead, never calling
///     the provider. Non-ASCII characters are weighted <see cref="NonAsciiCharWeight" />× because byte-pair tokenizers
///     emit far more tokens per character for CJK / structured / emoji content than the chars/4 English heuristic
///     assumes — the plain divisor badly UNDER-counts there, which would let an over-window round through.
///     <para>
///         This is the AI.Agent-layer twin of <c>HeuristicTokenEstimator</c> in the application layer (which the outer
///         budgeter uses). The two live in separate assemblies by the layer arrow (Application → AI.Agent), so the
///         entry points remain intentionally mirrored: a change to divisor selection here MUST be mirrored there, and
///         vice versa. Script-category weighting is shared through <see cref="TokenCharacterProfile" />.
///     </para>
///     <para>
///         AUD4-16: per-message script-category profiles are memoized by message instance in a
///         <see cref="ConditionalWeakTable{TKey,TValue}" /> (no leak — the entry dies with the message). This hop
///         re-estimates the full message list on EVERY inner tool-loop round, and those rounds reuse the same
///         <see cref="ChatMessage" /> instances (the function-invocation loop appends but never mutates prior messages),
///         so the memo collapses repeated full-content scans to dictionary lookups. Correct only because a
///         <see cref="ChatMessage" /> is immutable-after-construction on these paths; the memoized value equals a fresh
///         computation. The final division is deliberately not memoized, so a later per-model calibration affects the
///         same message instance without rescanning its content.
///     </para>
/// </summary>
internal static class ProviderMessageTokenEstimator
{
    private static readonly ConditionalWeakTable<ChatMessage, TokenCharacterProfile> PerMessageCharacterProfileCache = new();

    private static readonly ConditionalWeakTable<AITool, TokenCharacterProfile> PerToolCharacterProfileCache = new();

    private const int CharsPerToken = TokenEstimatorCalibrationStore.DefaultCharsPerToken;
    private const int PerMessageOverheadTokens = 4;

    public static int EstimateTokens(ChatMessage message, int charsPerToken = CharsPerToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var divisor = ClampDivisor(charsPerToken);
        var profile = PerMessageCharacterProfileCache.GetValue(message, ComputeMessageCharacterProfile);
        return (profile.WeightedLength(divisor) / divisor) + PerMessageOverheadTokens;
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
    ///     Conservative token estimate for the tool definitions serialized into the request: each tool's name,
    ///     description and JSON schema all count against the input window, so a tool-heavy agent must reserve room for
    ///     them. Ignored entirely, they under-count the round and let an over-window request through. Uses the same
    ///     weighted-char divisor and per-item framing overhead as message content.
    ///     <para>
    ///         Memoized by tool instance in the same shape as the per-message memo, because the tool list is strictly
    ///         immutable for the life of an invocation (the agent's <c>ChatOptions.Tools</c> is built once by the
    ///         invocation factory and the function-invocation loop re-sends the SAME instances every round) while this
    ///         hop runs on EVERY round. Without it each round paid a full <c>GetRawText()</c> materialization plus a
    ///         char scan of every tool's schema. Correct for the same reason the message memo is: an
    ///         <see cref="AIFunction" />'s name, description and schema do not change after construction, so the
    ///         memoized profile equals a fresh computation. The divisor is applied outside the memo so a per-model
    ///         calibration change re-divides without rescanning.
    ///     </para>
    /// </summary>
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
