namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration options for security behavior.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    [Range(1, 1024)]
    public int MaxSystemPromptSizeKb { get; set; } = 100;

    [Range(1, 1024)]
    public int MaxMessageSizeKb { get; set; } = 50;

    public string AllowedModelNamePattern { get; set; } = "^[a-zA-Z0-9._:-]+$";
}
