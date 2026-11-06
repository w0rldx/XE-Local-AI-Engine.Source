namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The two-slot shape of a chat-role <c>llama-server</c> process: turns a user waits on use <see cref="Foreground" />,
///     post-turn work uses <see cref="Background" />.
/// </summary>
/// <remarks>
///     So post-turn work (memory extraction, distill, compaction) never evicts the chat's cached prompt or its context
///     checkpoints. Both slots share ONE unified KV pool sized window + <see cref="BackgroundHeadroomTokens" />, each capped at the
///     window, so the chat window the server reports per slot is unchanged. Why and what it costs: wiki 03,
///     "LlamaServerProcessSupervisor — process lifecycle".
/// </remarks>
public static class LlamaServerChatSlots
{
    /// <summary>The <c>--parallel</c> value of every chat-role spawn.</summary>
    public const int Count = 2;

    /// <summary>The <c>id_slot</c> every interactive and transient chat request names.</summary>
    public const int Foreground = 0;

    /// <summary>The <c>id_slot</c> every background chat request names.</summary>
    public const int Background = 1;

    /// <summary>
    ///     Tokens the unified pool holds beyond the chat window, for the background slot's prompt and output.
    /// </summary>
    /// <remarks>
    ///     Every background prompt is bounded to fit it with its capped output: fold, distill and memory extraction each cap their
    ///     prompt at <see cref="BackgroundPromptChars" /> on the model's calibrated characters per token.
    /// </remarks>
    public const int BackgroundHeadroomTokens = 8192;

    /// <summary>
    ///     The characters a background prompt, system prompt included, may hold so that it and <paramref name="maxOutputTokens" />
    ///     fit <see cref="BackgroundHeadroomTokens" /> at <paramref name="charsPerToken" />.
    /// </summary>
    public static long BackgroundPromptChars(int maxOutputTokens, int charsPerToken) =>
        Math.Max(0L, BackgroundHeadroomTokens - (long)maxOutputTokens) * Math.Max(1, charsPerToken);
}
