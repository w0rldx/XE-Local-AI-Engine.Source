namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The sandbox a <b>Development Mode</b> attempt builds, tests and lints in.
///     <para>
///         Adds no members, for the same reason as <see cref="IAgentSandboxRuntimeProvider" />. It is registered as a
///         factory that resolves Development Mode's declaration through <c>SandboxProviderSelector</c>, and it remains
///         the only role a container backend may serve — under ADR 0007 because it is the only workload that can
///         declare <see cref="SandboxToolchainSource.EngineApprovedImage" />, which is ADR 0004 §1's narrowing
///         expressed as a declared need rather than as a feature name.
///     </para>
///     <para>
///         <see cref="DevelopmentSandboxOptions" /> now CONSTRAINS the candidate set rather than naming the provider,
///         and an unset value still inherits the agent key's constraint, so a node that has never heard of this option
///         keeps running Development Mode on exactly the backend it ran on before the seam existed. See
///         <c>SandboxProviderSelector.ResolveDevelopment</c> for the exact migration of the key's meaning.
///     </para>
/// </summary>
public interface IDevelopmentSandboxRuntimeProvider : ISandboxRuntimeProvider;
