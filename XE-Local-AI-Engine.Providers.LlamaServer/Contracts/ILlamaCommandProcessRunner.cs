namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Result of a bounded, read-only llama.cpp command probe.</summary>
internal sealed class LlamaCommandResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }

    /// <summary>Both redirected streams, preserving diagnostics written to stderr by llama.cpp.</summary>
    public string CombinedOutput => string.Concat(StandardOutput, "\n", StandardError);
}

/// <summary>Test seam for short-lived llama.cpp capability commands.</summary>
internal interface ILlamaCommandProcessRunner
{
    /// <summary>Runs the resolved executable with <paramref name="arguments" /> under a bounded timeout.</summary>
    Task<LlamaCommandResult?> RunAsync(string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct);
}
