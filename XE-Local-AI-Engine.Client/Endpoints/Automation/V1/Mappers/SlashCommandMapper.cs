namespace XE_Local_AI_Engine.Client.Endpoints.Automation.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Automation;

internal static class SlashCommandMapper
{
    public static SlashCommandInput ToInput(this CreateSlashCommandRequest request) => ToInput(request.Name, request.Description, request.Action);
    public static SlashCommandInput ToInput(this UpdateSlashCommandRequest request) => ToInput(request.Name, request.Description, request.Action);
    public static SlashCommandResponse ToResponse(this SlashCommandCatalogItem item) => new()
    {
        Id = item.Id, Name = item.Name, Description = item.Description, Source = item.Source,
        Action = new SlashCommandActionDto { Type = SlashCommandActionTypeDto.SendPrompt, Prompt = item.Prompt }
    };

    private static SlashCommandInput ToInput(string? name, string? description, SlashCommandActionDto? action)
    {
        if (action is null)
        {
            throw new SlashCommandValidationException("Action is required.");
        }
        if (action.Type != SlashCommandActionTypeDto.SendPrompt)
        {
            throw new SlashCommandValidationException("Only the sendPrompt action is supported.");
        }
        return new SlashCommandInput(name ?? string.Empty, description, SlashCommandActionType.SendPrompt, action.Prompt ?? string.Empty);
    }
}
