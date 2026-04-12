namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Runtime.InteropServices;
using System.Text.Json;

public sealed class LocalToolExecutor : ILocalToolExecutor
{
    private readonly Dictionary<string, (string Description, Func<JsonElement, Task<string>> Handler)> _tools;

    public LocalToolExecutor()
    {
        _tools = new Dictionary<string, (string, Func<JsonElement, Task<string>>)>(StringComparer.OrdinalIgnoreCase)
        {
            ["get_current_datetime"] = (
                "Returns the current UTC date and time.",
                _ => Task.FromResult(DateTime.UtcNow.ToString("o"))
            ),
            ["get_system_info"] = (
                "Returns OS, machine name, processor count, and available memory.",
                _ =>
                {
                    var info = new
                    {
                        OS = RuntimeInformation.OSDescription,
                        Environment.MachineName,
                        Environment.ProcessorCount,
                        MemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)
                    };
                    return Task.FromResult(JsonSerializer.Serialize(info));
                }
            )
        };
    }

    public Task<string> ExecuteAsync(string toolName, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (!_tools.TryGetValue(toolName, out var entry))
        {
            return Task.FromResult($"Unknown tool: {toolName}");
        }

        return entry.Handler(arguments);
    }

    public IReadOnlyDictionary<string, string> GetAvailableTools()
    {
        return _tools.ToDictionary(kvp => kvp.Key,
            kvp => kvp.Value.Description,
            StringComparer.OrdinalIgnoreCase);
    }
}
