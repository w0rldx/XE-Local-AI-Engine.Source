namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The sandbox a <b>work session</b> would execute in.
///     <para>
///         Adds no members, and — deliberately — has no consumer in v1: none of the four state tools needs a jail, and
///         the sessions that ship (General and Research) read the knowledge base rather than running commands. It exists
///         so that when a session tool does need to execute something, the role already exists and the provider choice
///         is a per-feature property of the type system rather than of a registration someone has to keep correct.
///     </para>
///     <para>
///         It reuses <see cref="IAgentSandboxRuntimeProvider" />'s implementations — the deterministic fake and the
///         jailed process provider — and, like that role, structurally cannot receive the container provider: ADR 0004
///         permits Docker for Development Mode execution only, so <c>DockerSandboxRuntimeProvider</c> does not implement
///         this interface and wiring it here would be a compile error rather than a review catch.
///     </para>
/// </summary>
public interface IWorkSessionSandboxRuntimeProvider : ISandboxRuntimeProvider;
