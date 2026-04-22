namespace XE_Local_AI_Engine.Client.Models;

using XE_Local_AI_Engine.Client.Models.Enums;

public sealed record AllowedToolDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required ToolLocation Location { get; init; }

    public string? ParameterSchema { get; init; }
}
