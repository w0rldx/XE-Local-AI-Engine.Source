namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Runs one kind of custom tool. The catalog resolves the executor for a tool's <see cref="Kind" /> and invokes it
///     with the decrypted record plus the model's JSON arguments; the executor owns config parsing, parameter
///     substitution, the kind-specific safety guards (SSRF for fetch, executable/rlimit/timeout for command), and
///     secret value-scrubbing of everything it returns. It never throws a guard failure into the caller — a blocked or
///     failed call returns a scrubbed, model-readable failure string so the run continues.
/// </summary>
internal interface ICustomToolExecutor
{
    /// <summary>The tool kind this executor serves.</summary>
    CustomToolKind Kind { get; }

    /// <summary>
    ///     Executes <paramref name="tool" /> with the model-supplied <paramref name="jsonArguments" />, returning the
    ///     secret-scrubbed, size-bounded result the model sees.
    /// </summary>
    Task<string> ExecuteAsync(CustomToolRecord tool, string jsonArguments, CancellationToken cancellationToken);
}
