namespace XE_Local_AI_Engine.Client.Hosting;

/// <summary>
///     Result of <see cref="Program.CreateAppAsync" />: the built app, or a null app plus the process exit code
///     when a CLI early-exit path handled the invocation.
/// </summary>
public sealed class ProgramStartResult
{
    public required WebApplication? App { get; init; }

    public required int ExitCode { get; init; }
}
