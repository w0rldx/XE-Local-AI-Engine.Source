namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Resolves the operator's per-model EXTRA <c>llama-server</c> command-line arguments, the developer and advanced
///     experimentation override, for one spawn.
/// </summary>
/// <remarks>
///     The tokens are appended AFTER the built launch spec, so a later scalar flag wins (llama.cpp is last-wins) and
///     the operator overrides a bundled tuning default for ONE model. <strong>Dependency inversion:</strong> defined
///     here so the supervisor never depends on <c>Client.Application</c>, which holds the real implementation that
///     strips the reserved process-contract flags. Implementations MUST NOT throw: a bad or unavailable override
///     degrades to "no extra args", mirroring <see cref="IInferenceProfileResolver" />.
/// </remarks>
public interface ILlamaServerExtraLaunchArgumentsResolver
{
    /// <summary>
    ///     Returns the sanitized extra argument tokens for <paramref name="modelName" /> in <paramref name="role" />, or
    ///     an empty list when the model has no override. Reserved process-contract flags (model path / host / port) are
    ///     already stripped by the implementation. Never throws.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveAsync(string modelName, ModelRole role, CancellationToken ct);
}
