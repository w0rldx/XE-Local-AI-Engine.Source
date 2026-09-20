namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The sandbox a <b>Development Mode</b> attempt builds, tests and lints in. Adds no members, for the same reason as
///     <see cref="IAgentSandboxRuntimeProvider" />.
/// </summary>
/// <remarks>
///     Registered as a factory resolving Development Mode's declaration through <c>SandboxProviderSelector</c>, and the only role a
///     container backend may serve, being the only workload that can declare <see cref="SandboxToolchainSource.EngineApprovedImage" /> —
///     ADR 0004 §1's narrowing as a declared need rather than a feature name. <see cref="DevelopmentSandboxOptions" /> CONSTRAINS the
///     candidate set rather than naming the provider, and an unset value inherits the agent key's constraint, so a node that never heard of
///     the option keeps its backend (<c>SandboxProviderSelector.ResolveDevelopment</c> carries the migration).
/// </remarks>
public interface IDevelopmentSandboxRuntimeProvider : ISandboxRuntimeProvider;
