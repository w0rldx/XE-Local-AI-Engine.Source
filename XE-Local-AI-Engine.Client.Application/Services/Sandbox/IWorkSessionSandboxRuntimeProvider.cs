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
///         It declares <see cref="SandboxWorkloads.WorkSession" />, which is AgentHome's declaration, so it resolves
///         the same backend and the same instance. Like that role it cannot receive a container backend: the
///         declaration names <see cref="SandboxToolchainSource.HostToolchain" />, no container backend supplies one,
///         and <c>DockerSandboxRuntimeProvider</c> does not implement this interface either.
///     </para>
/// </summary>
public interface IWorkSessionSandboxRuntimeProvider : ISandboxRuntimeProvider;
