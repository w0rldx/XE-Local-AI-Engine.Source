namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The sandbox a <b>Development Mode</b> attempt builds, tests and lints in.
///     <para>
///         Adds no members, for the same reason as <see cref="IAgentSandboxRuntimeProvider" />: plan decision D2 makes
///         provider selection per feature, and a role-scoped marker turns that decision into something the compiler
///         enforces. This is the only role a container provider may serve, because ADR 0004 permits Docker for
///         Development Mode build/test/lint execution only.
///     </para>
///     <para>
///         Selection is configuration-bound through <see cref="DevelopmentSandboxOptions" />. An unset value falls back
///         to whatever the agent role resolved, so a node that has never heard of this option keeps running Development
///         Mode on exactly the provider it ran on before the seam existed.
///     </para>
/// </summary>
public interface IDevelopmentSandboxRuntimeProvider : ISandboxRuntimeProvider;
