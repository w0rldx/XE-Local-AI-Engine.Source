namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     The dependency-manifest policy: an attempt may not change any file that decides what <c>restore</c> resolves.
///     <para>
///         This policy makes denying the agent-facing sandbox's egress a hardening win rather than an outage. The
///         engine warms the package cache from the <em>base commit</em>'s manifests before the agent can
///         write anything (<c>DevelopmentWorkspaceProvider</c>'s warm restore), so an attempt that changed a manifest
///         would need a resolve the sandbox can no longer perform. Rejecting the change is what keeps the warmed cache
///         a complete answer instead of an approximate one.
///     </para>
///     <para>
///         <strong>A verdict, not a <see cref="DevelopmentWorkspaceSecurityException" />, on purpose.</strong> The
///         sibling <see cref="DevelopmentTestWritePolicy" /> throws, because "delete the failing test" is an attack and
///         there is no legitimate reading of it. "Add a package" is the opposite: a perfectly reasonable task this
///         version cannot serve. Surfacing it as a verdict returns the task to
///         <c>DevelopmentTaskStatus.InProgress</c> carrying the reason, so the agent can retry without the package —
///         and the difference between "you may not" and "not yet" stays visible in the shape of the failure rather
///         than only in its wording.
///     </para>
/// </summary>
internal static class DevelopmentDependencyManifestPolicy
{
    /// <summary>How many offending paths the operator-facing detail names before it elides the rest.</summary>
    private const int MaxNamedPaths = 8;

    /// <summary>
    ///     The failing verdict for an attempt that touched a dependency manifest, or <see langword="null" /> when it
    ///     did not.
    ///     <para>
    ///         EVERY change type counts, including <c>added</c> — a repository with no
    ///         <c>Directory.Packages.props</c> gains central package management the moment one appears, which changes
    ///         resolution for the whole tree. A rename is checked against its previous path as well as its new one,
    ///         because renaming a manifest out of the set changes resolution exactly as much as editing it.
    ///     </para>
    /// </summary>
    public static DevelopmentValidationVerdict? Evaluate(DevelopmentPatchEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var offending = evidence.ChangedFiles
                                .SelectMany(static change => new[]
                                {
                                    change.Path,
                                    change.PreviousPath
                                })
                                .Where(static path => !string.IsNullOrWhiteSpace(path) && IsDependencyManifest(path))
                                .Select(static path => path!)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(static path => path, StringComparer.Ordinal)
                                .ToArray();
        if (offending.Length == 0)
        {
            return null;
        }

        return new DevelopmentValidationVerdict(false,
            DevelopmentValidationFailureCodes.DependencyManifestChanged,
            $"The attempt changed {Describe(offending)}. Development Mode restores dependencies from the base commit "
            + "before the attempt starts and runs the attempt itself with no network, so a changed dependency manifest "
            + "cannot be resolved. Make the change without adding, removing or re-pinning a dependency.");
    }

    /// <summary>
    ///     True when a repository-relative path names a dependency manifest. Paths arrive from
    ///     <c>git diff --name-status</c>, so they are repository-relative with forward slashes; the normalization
    ///     mirrors <see cref="DevelopmentCommandProfile.IsProtectedTestPath" /> so the two policies read the same
    ///     evidence the same way.
    /// </summary>
    public static bool IsDependencyManifest(string? repositoryRelativePath)
    {
        if (string.IsNullOrWhiteSpace(repositoryRelativePath))
        {
            return false;
        }

        var normalized = repositoryRelativePath.Replace('\\', '/').TrimStart('/');
        return Array.Exists(DevelopmentCommandProfileCatalog.DependencyManifestPaths,
            pattern => DevelopmentGlob.IsMatch(pattern, normalized));
    }

    /// <summary>
    ///     Names the offending paths, bounded. The detail reaches
    ///     <c>development_tasks.terminal_reason</c>, which is <c>HasMaxLength(1024)</c>, and an attempt may legally
    ///     change up to <c>MaxChangedFiles</c> paths.
    /// </summary>
    private static string Describe(IReadOnlyList<string> offending)
    {
        var named = string.Join(", ", offending.Take(MaxNamedPaths));
        return offending.Count switch
        {
            1 => $"dependency manifest '{named}'",
            _ when offending.Count <= MaxNamedPaths => $"{offending.Count} dependency manifests ({named})",
            _ => $"{offending.Count} dependency manifests ({named}, …)"
        };
    }
}
