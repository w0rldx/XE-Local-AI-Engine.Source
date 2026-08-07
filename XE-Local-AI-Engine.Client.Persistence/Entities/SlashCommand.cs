namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class SlashCommand
{
    public static string DescriptionColumnName(string name) => $"slash_command_description:{name}";
    public static string ActionConfigurationColumnName(string name) => $"slash_command_action_configuration:{name}";

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[]? Description { get; set; }
    public int ActionType { get; set; }
    public byte[] ActionConfiguration { get; set; } = [];
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
