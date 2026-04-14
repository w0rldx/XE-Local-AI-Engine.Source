namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Events;

public sealed class ToolCallResultReceivedEventArgs : EventArgs
{
    public ToolCallResultReceivedEventArgs(ToolCallResultEvent toolCallResult)
    {
        ToolCallResult = toolCallResult ?? throw new ArgumentNullException(nameof(toolCallResult));
    }

    public ToolCallResultEvent ToolCallResult { get; }
}
