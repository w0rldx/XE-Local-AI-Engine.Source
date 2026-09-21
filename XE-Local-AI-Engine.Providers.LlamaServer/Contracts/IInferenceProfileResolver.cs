namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Resolves the llama-server launch arguments for a <c>(model, role, backend)</c> spawn — the seam through which a
///     persisted inference profile reaches the supervisor's single launch-spec builder.
/// </summary>
/// <remarks>
///     <strong>Dependency-inversion boundary:</strong> this interface is DEFINED here in <c>Providers.LlamaServer</c>
///     so the supervisor depends only on its own contract and never on <c>Client.Application</c>, preserving the
///     one-way <c>Application → Providers</c> arrow. The real DB-backed implementation, which reads frozen and explored
///     profiles and runs invalidation, lives in <c>Client.Application</c> and is DI-injected over
///     <see cref="DefaultInferenceProfileResolver" />, which always explores so the supervisor self-satisfies.
/// </remarks>
public interface IInferenceProfileResolver
{
    /// <summary>
    ///     Returns the launch arguments to spawn <paramref name="modelName" /> in <paramref name="role" /> on the
    ///     resolved <paramref name="backend" />: a frozen/explored profile's replay args, or
    ///     <see cref="ResolvedLaunchArguments.Explore" /> when no usable profile exists.
    /// </summary>
    Task<ResolvedLaunchArguments> ResolveAsync(string modelName, ModelRole role, GpuVariant backend, CancellationToken ct);
}
