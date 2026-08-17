namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

internal interface IStableDiffusionSourceCommandRunner
{
    Task<StableDiffusionSourceCommandResult> RunAsync(string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        TimeSpan timeout,
        bool captureOutput,
        CancellationToken ct);
}

internal sealed record StableDiffusionSourceCommandResult(int ExitCode, string StandardOutput, string StandardError);
