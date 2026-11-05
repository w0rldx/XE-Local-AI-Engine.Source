namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Shared serializer settings for every training JSON payload. Web defaults + string enums, matching the wire DTOs.</summary>
public static class TrainingJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
