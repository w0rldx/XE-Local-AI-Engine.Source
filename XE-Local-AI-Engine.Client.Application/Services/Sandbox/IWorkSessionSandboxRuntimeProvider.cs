namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The sandbox a <b>work session</b> would execute in. Adds no members and, deliberately, has no v1 consumer: none of the four state
///     tools needs a jail.
/// </summary>
/// <remarks>
///     It exists so that when a session tool does need to execute something, the role is already there and the provider choice is a
///     per-feature property of the type system rather than of a registration someone has to keep correct. It declares
///     <see cref="SandboxWorkloads.WorkSession" />, which is AgentHome's declaration, so it resolves the same backend and instance, and
///     like that role cannot receive a container backend: the declaration names <see cref="SandboxToolchainSource.HostToolchain" />, and
///     <c>DockerSandboxRuntimeProvider</c> does not implement this interface either.
/// </remarks>
public interface IWorkSessionSandboxRuntimeProvider : ISandboxRuntimeProvider;
