namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Resolves the operator's per-model EXTRA <c>llama-server</c> command-line arguments — the developer/advanced
///     experimentation override — for one spawn. These tokens are appended AFTER the supervisor's built launch spec, so a
///     later occurrence of a scalar flag (llama.cpp is last-wins) lets the operator override a bundled tuning default
///     (<c>-c</c>, <c>-ngl</c>, sampling, RoPE, …) for one model without touching any other.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Dependency-inversion boundary.</strong> This interface is DEFINED here in
///         <c>Providers.LlamaServer</c> so the supervisor depends only on its own contract and never on
///         <c>Client.Application</c> (preserving the one-way <c>Application → Providers</c> arrow). The real DB-backed
///         implementation — which reads the per-model override and strips the reserved process-contract flags — lives in
///         <c>Client.Application</c> and is DI-injected over the default.
///     </para>
///     <para>
///         <see cref="EmptyLlamaServerExtraLaunchArgumentsResolver" /> ships in this project and always returns an empty
///         list, so a provider-only host (or a test) spawns byte-for-byte as before until the Application implementation
///         replaces it via DI registration order.
///     </para>
///     <para>
///         Implementations MUST NOT throw: the cold spawn path degrades a bad/unavailable override to "no extra args",
///         never an exception out of the supervisor's spawn — mirroring <see cref="IInferenceProfileResolver" />.
///     </para>
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
