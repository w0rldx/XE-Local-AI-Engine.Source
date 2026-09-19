namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     A running llama-server process endpoint for a <c>(model, role)</c> pair, as returned by
///     <see cref="ILlamaServerProcessSupervisor.EnsureRunningAsync" />.
/// </summary>
public sealed record LlamaServerEndpoint
{
    /// <summary>Model the process serves.</summary>
    public required string ModelName { get; init; }

    /// <summary>Role the process serves (chat vs embedding).</summary>
    public required ModelRole Role { get; init; }

    /// <summary>
    ///     The localhost OpenAI-compatible base URL (for example <c>http://127.0.0.1:18100/v1</c>) the MEAI OpenAI
    ///     adapter points at. Bound to <c>127.0.0.1</c> only.
    /// </summary>
    public required Uri BaseAddress { get; init; }
}
