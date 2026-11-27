namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Testing.Git;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The process runner and doubles shared by the Development workspace, coder, validation and review suites.</summary>
internal static class DevelopmentTestDoubles
{
    /// <summary>
    ///     Runs <paramref name="executable" /> without the inherited git environment <see cref="TestGitEnvironment" />
    ///     drops, and returns its exit code and both streams; the caller decides what a failure means.
    /// </summary>
    public static async Task<CommandResult> RunProcessAsync(string workingDirectory, string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        TestGitEnvironment.Isolate(startInfo);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdout,
            StandardError = await stderr
        };
    }

    public static void EnsureSuccess(CommandResult result) =>
        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);

    internal sealed record CommandResult
    {
        public required int ExitCode { get; init; }

        public required string StandardOutput { get; init; }

        public required string StandardError { get; init; }
    }

    /// <summary>Fails any attempt to build a cloud context: the fixtures using it run local-only attempts.</summary>
    internal sealed class UnexpectedCloudContextService : IDevelopmentCloudAttemptContextService
    {
        public Task<DevelopmentCloudAttemptContext> CreateAsync(DevelopmentExecutionSnapshot snapshot,
            IReadOnlyList<DevelopmentCloudContextExcerpt> excerpts,
            IReadOnlyList<Guid>? inputArtifactIds = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A local-only development fixture must not build a cloud context.");
    }
}
