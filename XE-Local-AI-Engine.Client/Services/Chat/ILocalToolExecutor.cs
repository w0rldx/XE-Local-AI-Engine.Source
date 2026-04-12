namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Text.Json;

public interface ILocalToolExecutor
{
    Task<string> ExecuteAsync(string toolName, JsonElement arguments);
    IReadOnlyDictionary<string, string> GetAvailableTools();
}
