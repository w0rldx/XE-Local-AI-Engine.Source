namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using System.Text.Json;

/// <summary>One mapped event before the buffer mints its sequence. Plumbing between the pure half and the appending half.</summary>
internal sealed class IntegrationStreamEventDraft
{
    public required string Type { get; init; }

    public required string? ContentType { get; init; }

    public required JsonElement? Payload { get; init; }
}
