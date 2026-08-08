namespace XE_Local_AI_Engine.Client.Endpoints.Automation.V1;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(SlashCommandActionTypeDtoJsonConverter))]
[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "The wire discriminator has exactly one valid literal value.")]
public enum SlashCommandActionTypeDto
{
    [JsonStringEnumMemberName("sendPrompt")]
    SendPrompt = 1
}

public sealed class SlashCommandActionTypeDtoJsonConverter : JsonConverter<SlashCommandActionTypeDto>
{
    public override SlashCommandActionTypeDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("sendPrompt"u8))
        {
            return (SlashCommandActionTypeDto)0;
        }

        return SlashCommandActionTypeDto.SendPrompt;
    }

    public override void Write(Utf8JsonWriter writer, SlashCommandActionTypeDto value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value != SlashCommandActionTypeDto.SendPrompt)
        {
            throw new JsonException("The action type is not supported.");
        }

        writer.WriteStringValue("sendPrompt");
    }
}

public sealed class SlashCommandActionDto
{
    public required SlashCommandActionTypeDto Type { get; init; }
    public required string Prompt { get; init; }
}

public sealed class CreateSlashCommandRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required SlashCommandActionDto Action { get; init; }
}

public sealed class UpdateSlashCommandRequest
{
    public Guid CommandId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required SlashCommandActionDto Action { get; init; }
}

public sealed class SlashCommandByIdRequest
{
    public Guid CommandId { get; init; }
}

public sealed class SlashCommandResponse
{
    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Source { get; init; }
    public required SlashCommandActionDto Action { get; init; }
}

public sealed class ListSlashCommandsResponse
{
    public required IReadOnlyList<SlashCommandResponse> Items { get; init; }
}
