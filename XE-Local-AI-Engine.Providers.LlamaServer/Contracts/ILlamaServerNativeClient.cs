namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using Microsoft.Extensions.AI;

/// <summary>
///     The llama-server HTTP routes the node calls outside the OpenAI-compatible chat surface: <c>/metrics</c>,
///     <c>/props</c>, <c>/tokenize</c>, <c>/v1/messages/count_tokens</c>, and the pooled <c>embeddings</c>/<c>rerank</c> roles the benchmark harness
///     measures.
/// </summary>
/// <remarks>
///     Protocol only: routes, request bodies and response parsing. What to measure, when to calibrate and what counts
///     as a passing smoke test stay with the callers, as do their timeouts and error mapping: every method passes the
///     caller's token through and lets transport and parse failures propagate unchanged.
/// </remarks>
public interface ILlamaServerNativeClient
{
    /// <summary>Scrapes the Prometheus text at the server root's <c>/metrics</c> (served only with <c>--metrics</c>).</summary>
    Task<string> GetMetricsTextAsync(Uri baseAddress, CancellationToken ct);

    /// <summary>
    ///     Reads the server root's <c>/props</c>, or <see langword="null" /> when it answered with a non-success status.
    /// </summary>
    Task<LlamaServerProps?> GetPropsAsync(Uri baseAddress, CancellationToken ct);

    /// <summary>
    ///     POSTs <paramref name="content" /> to the server root's <c>/tokenize</c> over a transport that follows no
    ///     redirect and uses no ambient proxy, and reports what came back without judging it.
    /// </summary>
    /// <remarks>
    ///     The body is read only when the caller asks, through <see cref="LlamaServerTokenizeResponse.ReadTokenCountAsync" />;
    ///     a malformed body reads as no token count.
    /// </remarks>
    Task<LlamaServerTokenizeResponse> TokenizeAsync(Uri baseAddress, string content, CancellationToken ct);

    /// <summary>
    ///     POSTs a one-turn conversation (<paramref name="systemPrompt" /> + <paramref name="userMessage" />, offering
    ///     <paramref name="tools" />) to the server root's <c>/v1/messages/count_tokens</c>, which renders it through the model's own
    ///     chat template and counts the result, over the same guarded transport as <see cref="TokenizeAsync" />.
    /// </summary>
    /// <remarks>
    ///     Only each tool's name, description and JSON schema are sent. Verified on the pinned build (b10201, 8f4646a) with
    ///     <c>--jinja</c>: the count equals <c>/tokenize</c> over the <c>/apply-template</c> prompt, tools included. Read the count
    ///     through <see cref="LlamaServerTokenizeResponse.ReadTokenCountAsync" />.
    /// </remarks>
    Task<LlamaServerTokenizeResponse> CountPromptTokensAsync(Uri baseAddress,
        string systemPrompt,
        string userMessage,
        IReadOnlyList<AIFunctionDeclaration> tools,
        CancellationToken ct);

    /// <summary>
    ///     Asks <c>/v1/chat/completions/control</c> to end the reasoning block of the streaming completion
    ///     <paramref name="completionId" />, which must have been sent with <c>reasoning_control: true</c>.
    /// </summary>
    /// <remarks>
    ///     Body <c>{"id", "action": "reasoning_end"}</c>, over the same guarded transport as <see cref="TokenizeAsync" />. A non-success status or a body without <c>success: true</c> reads as a failed result carrying the server's
    ///     message; the server answers that way for an unknown or unarmed id. Transport failures propagate.
    /// </remarks>
    Task<LlamaServerReasoningControlResult> EndReasoningAsync(Uri baseAddress, string completionId, CancellationToken ct);

    /// <summary>
    ///     POSTs <paramref name="inputs" /> to <c>{baseAddress}/embeddings</c> and returns one vector per input, in
    ///     input order.
    /// </summary>
    /// <exception cref="InvalidDataException">The response did not carry exactly one vector per input index.</exception>
    Task<IReadOnlyList<IReadOnlyList<double>>> PostEmbeddingsAsync(Uri baseAddress,
        string modelName,
        IReadOnlyList<string> inputs,
        CancellationToken ct);

    /// <summary>
    ///     POSTs <paramref name="documents" /> to <c>{baseAddress}/rerank</c> and returns one relevance score per
    ///     document, in document order.
    /// </summary>
    /// <exception cref="InvalidDataException">The response did not carry exactly one score per document index.</exception>
    Task<IReadOnlyList<double>> PostRerankAsync(Uri baseAddress,
        string query,
        IReadOnlyList<string> documents,
        CancellationToken ct);
}
