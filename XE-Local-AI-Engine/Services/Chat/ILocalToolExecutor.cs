namespace XE_Local_AI_Engine.Services.Chat;

using System.Text.Json;

public interface ILocalToolExecutor
{
    Task<string> ExecuteAsync(string toolName, JsonElement arguments);
    IReadOnlyDictionary<string, string> GetAvailableTools();
}
