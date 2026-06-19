namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     A running llama-server process endpoint for a <c>(model, role)</c> pair, as returned by
///     <see cref="ILlamaServerProcessSupervisor.EnsureRunningAsync" />.
/// </summary>
/// <param name="ModelName">Model the process serves.</param>
/// <param name="Role">Role the process serves (chat vs embedding).</param>
/// <param name="BaseAddress">
///     The localhost OpenAI-compatible base URL (for example <c>http://127.0.0.1:18100/v1</c>) the MEAI OpenAI
///     adapter points at. Bound to <c>127.0.0.1</c> only.
/// </param>
public sealed record LlamaServerEndpoint(string ModelName, ModelRole Role, Uri BaseAddress);
