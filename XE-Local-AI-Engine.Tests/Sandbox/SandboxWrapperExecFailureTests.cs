namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A wrapper that could not exec its command reads as the launch failure an unwrapped start reports, so Development's
///     <c>runner_not_installed</c> rule holds behind <c>setsid</c> and the bubblewrap chain. The lines are the ones each wrapper prints.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SandboxWrapperExecFailureTests
{
    [Test]
    [Arguments("bwrap: execvp dotnet: No such file or directory", 1)]
    [Arguments("setsid: failed to execute python3: No such file or directory", 127)]
    [Arguments("unshare: failed to execute python3: No such file or directory", 127)]
    [Arguments("env: 'python3': No such file or directory\nenv: use -[v]S to pass options in shebang lines", 127)]
    [Arguments("Failed to find executable dotnet: No such file or directory", 1)]
    [Arguments("bwrap: execvp dotnet: Permission denied", 1)]
    public async Task AWrapperThatCouldNotExecTheCommand_IsALaunchFailure(string standardError, int exitCode)
    {
        var executable = standardError.Contains("python3", StringComparison.Ordinal) ? "python3" : "dotnet";

        var described = SandboxWrapperExecFailure.Describe(executable, exitCode, standardOutput: string.Empty, standardError);

        AssertEx.True(AssertEx.NotNull(described).StartsWith(SandboxCommandResult.LaunchFailurePrefix, StringComparison.Ordinal), described);

        await Task.CompletedTask;
    }

    /// <summary>The command ran: its own output, another executable's name, a clean exit or any stdout keep it an ordinary result.</summary>
    [Test]
    [Arguments("dotnet", 1, "", "error: /work/src/App.cs: No such file or directory")]
    [Arguments("dotnet", 1, "", "bwrap: execvp git: No such file or directory")]
    [Arguments("dotnet", 0, "", "bwrap: execvp dotnet: No such file or directory")]
    [Arguments("dotnet", 1, "Restored.", "bwrap: execvp dotnet: No such file or directory")]
    public async Task ACommandThatRan_IsNotALaunchFailure(string executable, int exitCode, string standardOutput, string standardError)
    {
        AssertEx.Null(SandboxWrapperExecFailure.Describe(executable, exitCode, standardOutput, standardError));

        await Task.CompletedTask;
    }
}
