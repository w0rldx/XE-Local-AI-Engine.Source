namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

/// <summary>One discovered skill folder: its <c>SKILL.md</c>, the bundled files kept, the scripts refused and the files ignored.</summary>
internal sealed class SkillArchiveFolder
{
    /// <summary>Last segment of <see cref="RootPath" />; empty when <c>SKILL.md</c> sits at the archive root.</summary>
    public required string DirectoryName { get; init; }

    /// <summary>Skill-root prefix inside the archive, with a trailing slash (empty at the archive root).</summary>
    public required string RootPath { get; init; }

    public required string SkillMarkdown { get; init; }

    public required IReadOnlyList<SkillArchiveFile> Files { get; init; }

    public required IReadOnlyList<string> RefusedScripts { get; init; }

    /// <summary>Relative paths whose extension is outside the resource allowlist. Raw: the service applies the name guard before listing them.</summary>
    public required IReadOnlyList<string> IgnoredFiles { get; init; }

    /// <summary>The folder carried more bundled files than the per-skill cap; the excess was never inflated.</summary>
    public bool ResourceLimitExceeded { get; init; }
}
