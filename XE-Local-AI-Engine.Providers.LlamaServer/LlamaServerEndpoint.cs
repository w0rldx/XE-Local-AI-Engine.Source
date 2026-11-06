namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     A running llama-server process endpoint for a <c>(model, role)</c> pair, as returned by
///     <see cref="Contracts.ILlamaServerProcessSupervisor.EnsureRunningAsync(string, ModelRole, CancellationToken)" />.
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

    /// <summary>
    ///     The <c>--parallel</c> slot count the process was launched with, read from its final argument vector; 1 for a
    ///     process the node did not launch (an external endpoint), so a request never names a slot that may not exist.
    /// </summary>
    public int ParallelSlots { get; init; } = 1;
}
