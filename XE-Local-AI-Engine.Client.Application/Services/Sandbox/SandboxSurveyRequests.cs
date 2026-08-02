namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     A request to list the regular files under one sandbox directory.
///     <para>
///         Every bound is the caller's, because the caller is the one that knows what its model can usefully read:
///         these surveys have no defaults of their own to fall back on, and a provider inventing one would silently
///         change what a tool returns.
///     </para>
/// </summary>
public sealed record SandboxListFilesRequest
{
    /// <summary>The sandbox-absolute directory to survey. The provider confines it exactly as it confines a read.</summary>
    public required string DirectoryPath { get; init; }

    /// <summary>The emitted-entry ceiling. The survey stops once it is reached rather than listing and truncating.</summary>
    public required int MaxEntries { get; init; }

    /// <summary>
    ///     An optional glob matched against the file NAME only — never the path, and never used to skip a directory, so
    ///     a match deep in a non-matching directory is still found.
    /// </summary>
    public string? NameGlob { get; init; }
}

/// <summary>A request to search the non-binary regular files under one sandbox directory.</summary>
public sealed record SandboxSearchTextRequest
{
    /// <summary>The sandbox-absolute directory to survey. The provider confines it exactly as it confines a read.</summary>
    public required string DirectoryPath { get; init; }

    /// <summary>
    ///     The pattern. Literal by default; a regular expression only when <see cref="IsRegex" /> is set, and even then
    ///     it runs under a per-line timeout because the value is model-supplied.
    /// </summary>
    public required string Pattern { get; init; }

    /// <summary>Whether <see cref="Pattern" /> is a regular expression rather than a literal string.</summary>
    public bool IsRegex { get; init; }

    /// <summary>The emitted-match ceiling.</summary>
    public required int MaxMatches { get; init; }

    /// <summary>
    ///     A second ceiling, on total emitted bytes. Both are needed: a match count alone does not bound the output of a
    ///     file whose lines are enormous.
    /// </summary>
    public required int MaxOutputBytes { get; init; }
}
