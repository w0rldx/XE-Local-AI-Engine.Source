namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     One entry in <c>changed-files.json</c> (AgentHome plan §9.1). Serialized with web/camelCase naming so the keys
///     are <c>selectedFolderId</c>, <c>alias</c>, <c>relativePath</c>, <c>changeType</c>. <see cref="RelativePath" /> is
///     folder-relative (the <c>&lt;alias&gt;/</c> prefix is stripped) and never a host path.
/// </summary>
internal sealed record ChangedFileEntry
{
    /// <summary>The selected-folder id the changed file belongs to (mapped from its alias).</summary>
    public required string SelectedFolderId { get; init; }

    /// <summary>The selected-folder alias the changed file belongs to.</summary>
    public required string Alias { get; init; }

    /// <summary>The path relative to the selected folder (no alias prefix, no host path).</summary>
    public required string RelativePath { get; init; }

    /// <summary>The git change type: added, modified, deleted, renamed, copied, typechanged, unmerged, or unknown.</summary>
    public required string ChangeType { get; init; }
}
