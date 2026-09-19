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

internal sealed class StableDiffusionSourceCommandResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }
}
