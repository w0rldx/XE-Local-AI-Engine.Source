namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

/// <summary>The model the runtime currently reports as active/loaded.</summary>
/// <param name="Name">Normalized active-model name, or <c>null</c> when none is loaded.</param>
/// <param name="ExpiresAt">When the loaded model is scheduled for eviction, when reported.</param>
internal sealed record ActiveModelInfo(string? Name, DateTimeOffset? ExpiresAt)
{
    /// <summary>Sentinel representing "no active model".</summary>
    public static ActiveModelInfo None { get; } = new(null, null);
}
