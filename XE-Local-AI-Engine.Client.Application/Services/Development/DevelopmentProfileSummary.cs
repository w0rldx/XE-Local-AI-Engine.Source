namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.Json;

/// <summary>
///     The publicly projectable facts about a project's stored command profile: which code-owned profile it is, what it
///     builds, and the digest that identifies the exact command set.
///     <para>
///         Deliberately not the whole profile blob. The endpoint layer cannot see this assembly's internals, so a
///         projection is needed either way — and this one is chosen to carry nothing host-identifying. The build target
///         is repository-relative by construction (<c>NormalizeTarget</c> confines it before it can reach an argument
///         vector), whereas the full profile's argument vectors would put executable names and the whole materialized
///         command line on the wire for no operator benefit.
///     </para>
/// </summary>
public sealed class DevelopmentProfileSummary
{
    public required string ProfileId { get; init; }

    public required string? BuildTarget { get; init; }

    public required string Digest { get; init; }

    /// <summary>
    ///     Projects a stored profile, or null when the project has none or the stored bytes are unreadable.
    ///     <para>
    ///         Deliberately lenient, unlike <c>DevelopmentCommandProfileCatalog.ResolveStored</c>. That method is the
    ///         execution gate and must reject a profile the catalog no longer honours; this one only labels a row in a
    ///         list. Making a read-only projection strict would take the whole project list down the moment the
    ///         code-owned catalog drifted, which hides the drift behind an outage instead of surfacing it at the point
    ///         where it actually matters — starting an attempt.
    ///     </para>
    /// </summary>
    public static DevelopmentProfileSummary? TryFrom(string? storedCommandProfileJson)
    {
        if (string.IsNullOrWhiteSpace(storedCommandProfileJson))
        {
            return null;
        }

        try
        {
            var profile = DevelopmentCommandProfile.FromCanonicalJson(storedCommandProfileJson);
            return new DevelopmentProfileSummary { ProfileId = profile.ProfileId, BuildTarget = profile.BuildTarget, Digest = profile.ComputeDigest() };
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException
                                              or JsonException
                                              or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
///     What profile detection proposes for a registered repository, before the operator confirms it. The public
///     counterpart of the internal detection record, for the confirmation step that crosses the API boundary.
/// </summary>
public sealed class DevelopmentProfileDetectionResult
{
    /// <summary>The code-owned profile id the repository looks like.</summary>
    public required string ProfileId { get; init; }

    /// <summary>The repository-relative solution or project file, null for <c>generic-git</c>.</summary>
    public required string? BuildTarget { get; init; }

    /// <summary>Every build target found, so the operator can choose a different one.</summary>
    public required IReadOnlyList<string> Candidates { get; init; }
}
