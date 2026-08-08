namespace XE_Local_AI_Engine.Client.Services.Coder;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Confines a model-supplied (workspace-relative) path to the in-sandbox coder workspace root
///     <see cref="AgentHomeGit.WorkspaceSelectedRoot" /> (<c>/agent-home/workspace/selected</c>). It is the sibling of
///     <see cref="Workspace.Implementation.HostPathSafety" /> for the in-sandbox side: where <c>HostPathSafety</c>
///     guards the host→sandbox copy, this guard fails closed on any model path that would escape the workspace root
///     before that path ever reaches <see cref="Sandbox.ISandboxRuntimeProvider.ReadFileAsync" /> or is composed into a
///     non-chrooted <see cref="Sandbox.ISandboxRuntimeProvider.ExecuteAsync" /> command.
///     <para>
///         The guard works on the sandbox path namespace (a Linux-style, forward-slash, jail-relative space), not on
///         host paths, so it canonicalizes lexically: it rejects control characters, the Windows <c>\\?\</c>/<c>\\.\</c>
///         extended/device prefixes, any absolute path, and any path that — after collapsing <c>.</c>/<c>..</c>
///         segments — leaves the workspace root. The provider's own <c>ResolveJailPath</c> +
///         <c>EnsureNoSymlinkComponentsUnderJail</c> remain the second line of defense for the reads that flow through
///         <c>ReadFileAsync</c>; this guard closes the <c>ExecuteAsync</c> arg-injection hole the provider does not.
///     </para>
/// </summary>
internal static class WorkspacePathGuard
{
    /// <summary>The sandbox-absolute workspace root every confined path resolves at or beneath.</summary>
    public const string WorkspaceRoot = AgentHomeGit.WorkspaceSelectedRoot;

    // The sandbox path namespace is always Linux-style forward-slash (it mirrors AgentHomeGit.WorkspaceSelectedRoot,
    // an in-sandbox path), independent of the worker host OS — so this is NOT a host Path.DirectorySeparatorChar.
    private const char SandboxSeparator = '/';

    /// <summary>
    ///     Confines <paramref name="modelPath" /> (a workspace-relative path, or <see langword="null" />/empty for the
    ///     workspace root) to a subpath of <see cref="WorkspaceRoot" />. Returns <see cref="ConfinedPath.Confined" />
    ///     carrying the normalized workspace-relative path and the sandbox-absolute path, or
    ///     <see cref="ConfinedPath.Rejected" /> with a model-facing reason on any escape attempt.
    /// </summary>
    public static ConfinedPath Confine(string? modelPath)
    {
        // A null/empty/whitespace path means "the workspace root" — confined to the root with an empty relative path.
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return ConfinedPath.Confined(string.Empty, WorkspaceRoot);
        }

        if (modelPath.Any(char.IsControl))
        {
            return ConfinedPath.Rejected("the path contains control characters and was rejected.");
        }

        // Fail closed for Windows extended-length / device namespaces that bypass normal normalization.
        if (modelPath.StartsWith(@"\\?\", StringComparison.Ordinal) || modelPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return ConfinedPath.Rejected("extended/device paths are not allowed.");
        }

        // Normalize separators to the sandbox (Linux) convention so a back-slash segment cannot smuggle a traversal.
        var normalized = modelPath.Replace('\\', '/');

        // Reject any absolute path outright: the model may only address workspace-relative subpaths. A Windows
        // drive-qualified path (e.g. "C:/x") is absolute too.
        if (normalized.StartsWith('/') || IsWindowsDriveQualified(normalized))
        {
            return ConfinedPath.Rejected("absolute paths are not allowed; supply a workspace-relative path.");
        }

        // Collapse '.' and '..' segments lexically. Any '..' that pops above the root escapes the workspace and is
        // rejected (never silently clamped), matching the provider's fail-closed jail posture.
        var resolvedSegments = new List<string>();
        foreach (var segment in normalized.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    // Empty (a doubled slash or trailing slash) and '.' are no-ops.
                    continue;
                case "..":
                    if (resolvedSegments.Count == 0)
                    {
                        return ConfinedPath.Rejected("the path traverses above the workspace root and was rejected.");
                    }

                    resolvedSegments.RemoveAt(resolvedSegments.Count - 1);
                    continue;
                default:
                    resolvedSegments.Add(segment);
                    continue;
            }
        }

        var relativePath = string.Join(SandboxSeparator, resolvedSegments);
        var sandboxPath = relativePath.Length == 0
            ? WorkspaceRoot
            : WorkspaceRoot + SandboxSeparator + relativePath;

        return ConfinedPath.Confined(relativePath, sandboxPath);
    }

    private static bool IsWindowsDriveQualified(string path)
    {
        // "C:/..." or "C:..." — a drive letter followed by a colon is an absolute/drive-relative Windows path.
        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    }
}

/// <summary>
///     Result of a <see cref="WorkspacePathGuard.Confine" /> call. <see cref="IsConfined" /> distinguishes a confined
///     path (carrying the workspace-relative and sandbox-absolute forms) from a rejection (carrying a model-facing
///     reason, never host detail).
/// </summary>
internal readonly record struct ConfinedPath
{
    private ConfinedPath(bool isConfined, string relativePath, string sandboxPath, string? rejectionReason)
    {
        IsConfined = isConfined;
        RelativePath = relativePath;
        SandboxPath = sandboxPath;
        RejectionReason = rejectionReason;
    }

    /// <summary>Whether the path was confined (<see langword="true" />) or rejected (<see langword="false" />).</summary>
    public bool IsConfined { get; }

    /// <summary>The normalized workspace-relative path (empty for the workspace root). Meaningful only when confined.</summary>
    public string RelativePath { get; }

    /// <summary>The sandbox-absolute path under <see cref="WorkspacePathGuard.WorkspaceRoot" />. Meaningful only when confined.</summary>
    public string SandboxPath { get; }

    /// <summary>The model-facing rejection reason. Non-null only when not confined.</summary>
    public string? RejectionReason { get; }

    public static ConfinedPath Confined(string relativePath, string sandboxPath)
    {
        return new ConfinedPath(isConfined: true, relativePath, sandboxPath, rejectionReason: null);
    }

    public static ConfinedPath Rejected(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ConfinedPath(isConfined: false, relativePath: string.Empty, sandboxPath: string.Empty, reason);
    }
}
