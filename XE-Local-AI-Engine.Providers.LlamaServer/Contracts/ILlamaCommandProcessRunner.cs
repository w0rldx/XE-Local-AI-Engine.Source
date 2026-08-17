namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Result of a bounded, read-only llama.cpp command probe.</summary>
internal sealed record LlamaCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
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
