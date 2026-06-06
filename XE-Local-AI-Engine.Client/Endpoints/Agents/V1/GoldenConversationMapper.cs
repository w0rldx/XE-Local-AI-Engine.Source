namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Maps between the golden-conversation transport DTOs and the Application/Persistence types. This is the sole point
///     in the Client project that references the golden record/source member names, so it is the only file that needs
///     adjustment if those names change. Keeping the Persistence/Persistence.Entities dependency here (rather than in the
///     DTO file) stops the ORM layer from leaking across the transport boundary while preserving the wire shape: the
///     <see cref="GoldenConversationResponse.Source" /> discriminator is already projected to a plain lowercase string.
/// </summary>
internal static class GoldenConversationMapper
{
    // Web defaults so the persisted/serialized golden JSON is camelCase, matching the runner's parsing + the client (CA1869).
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static GoldenConversationResponse ToResponse(this GoldenConversationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new GoldenConversationResponse
        {
            Id = record.Id,
            AgentDefinitionId = record.AgentDefinitionId,
            Title = record.Title,
            InputTurns = DeserializeTurns(record.InputTurns),
            Assertion = DeserializeAssertion(record.Assertion),
            Rubric = record.Rubric,
            Enabled = record.Enabled,
            Source = record.Source switch
            {
                GoldenConversationSource.Harvested => "harvested",
                _ => "manual"
            },
            SourceMessageId = record.SourceMessageId,
            SourceConversationId = record.SourceConversationId,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }

    public static GoldenHarvestResponse ToResponse(this GoldenHarvestOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new GoldenHarvestResponse(outcome.ThumbsUpScanned,
            outcome.CreatedCount,
            outcome.DuplicateCount,
            outcome.SkippedCount);
    }

    public static GoldenConversationCreateInput ToInput(this CreateGoldenConversationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inputTurnsJson = JsonSerializer.Serialize(request.InputTurns, SerializerOptions);
        var assertionJson = request.Assertion is null
            ? null
            : JsonSerializer.Serialize(request.Assertion, SerializerOptions);

        return new GoldenConversationCreateInput(request.AgentDefinitionId,
            request.Title,
            inputTurnsJson,
            assertionJson,
            request.Rubric,
            request.Enabled);
    }

    private static IReadOnlyList<GoldenTurnDto> DeserializeTurns(string inputTurnsJson)
    {
        if (string.IsNullOrWhiteSpace(inputTurnsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<GoldenTurnDto>>(inputTurnsJson, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            // A malformed turns column must not 500 the list endpoint — degrade to an empty conversation.
            return [];
        }
    }

    private static GoldenAssertionDto? DeserializeAssertion(string? assertionJson)
    {
        if (string.IsNullOrWhiteSpace(assertionJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GoldenAssertionDto>(assertionJson, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
