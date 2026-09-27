namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class LlamaPerplexityProcessResult
{
    public required int ExitCode { get; init; }

    /// <summary>
    ///     stdout and stderr interleaved, bounded to the tail. llama.cpp prints its progress and its final estimate on
    ///     stderr, so splitting the streams would throw away the only line that matters.
    /// </summary>
    public required string Output { get; init; }
}
