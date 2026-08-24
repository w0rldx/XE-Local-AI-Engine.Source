namespace XE_Local_AI_Engine.Providers.Training.Implementation;

/// <summary>
///     Run-to-completion subprocess seam for the runtime installer. Exists as an interface only so the phase machine can
///     be driven end to end in tests without provisioning a real multi-gigabyte venv; production has exactly one
///     implementation.
///     <para>
///         Public alongside <see cref="UvBinaryAcquirer" />: every uv-managed venv the engine provisions runs its
///         <c>uv sync</c> through this same scrubbed, tree-killed spawn, so the compute tool's own (much smaller)
///         provision reuses it rather than growing a second subprocess path with its own environment-scrubbing rules.
///     </para>
/// </summary>
public interface ITrainingProcessRunner
{
    /// <summary>
    ///     Runs <paramref name="file" /> with <paramref name="args" /> under the scrubbed <paramref name="environment" />,
    ///     streaming stdout and stderr line-by-line into <paramref name="logSink" />, and returns the exit code.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct" /> fired; the process tree was killed first.</exception>
    /// <exception cref="TrainingRuntimeException">The process could not start, or exceeded <paramref name="timeout" />.</exception>
    Task<int> RunAsync(string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        Action<string> logSink,
        TimeSpan timeout,
        CancellationToken ct);
}
