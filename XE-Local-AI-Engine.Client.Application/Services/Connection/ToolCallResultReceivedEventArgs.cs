namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models.Events;

/// <summary>
///     Event payload for tool call result received notifications.
/// </summary>
public sealed class ToolCallResultReceivedEventArgs : EventArgs
{
    public ToolCallResultReceivedEventArgs(ToolCallResultEvent toolCallResult)
    {
        ToolCallResult = toolCallResult ?? throw new ArgumentNullException(nameof(toolCallResult));
    }

    public ToolCallResultEvent ToolCallResult { get; }
}
