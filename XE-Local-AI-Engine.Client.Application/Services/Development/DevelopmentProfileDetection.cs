namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     What detection found in a registered repository, before the operator confirms it.
/// </summary>
public sealed class DevelopmentProfileDetection
{
    /// <summary>The code-owned profile id this repository looks like.</summary>
    public required string ProfileId { get; init; }

    /// <summary>The repository-relative solution or project file, null for <c>generic-git</c>.</summary>
    public required string? BuildTarget { get; init; }

    /// <summary>
    ///     Every build target found, so the operator can pick a different one when a repository has more than one. Bounded,
    ///     because a large repository can contain hundreds of project files and this list crosses an API boundary.
    /// </summary>
    public required IReadOnlyList<string> Candidates { get; init; }
}
