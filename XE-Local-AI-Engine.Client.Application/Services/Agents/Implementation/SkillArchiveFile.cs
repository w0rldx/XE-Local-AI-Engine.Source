namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

/// <summary>One bundled file, named relative to its skill root — the path the model looks it up by.</summary>
internal sealed class SkillArchiveFile
{
    public required string Name { get; init; }

    public required string MediaType { get; init; }

    public required string Content { get; init; }
}
