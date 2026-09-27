namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Runs one <c>llama-perplexity</c> child process to completion and returns what it printed.
/// </summary>
/// <remarks>
///     A seam of its own rather than the training spawner: that one is Linux-only and scrubs a Python environment this
///     has no use for, while every platform that ships a llama.cpp runtime ships this tool. It exists mainly so the
///     output parser can be tested against captured real output instead of a 27B model.
/// </remarks>
public interface ILlamaPerplexityRunner
{
    /// <exception cref="LlamaRuntimeException">The process could not be started.</exception>
    Task<LlamaPerplexityProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
