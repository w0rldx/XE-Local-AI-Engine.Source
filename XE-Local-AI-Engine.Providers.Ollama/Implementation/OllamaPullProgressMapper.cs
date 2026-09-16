namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Maps OllamaSharp's pull response onto the provider-neutral <see cref="PullProgress" />. Shared by the two pull
///     surfaces — <see cref="OllamaLocalModelProvider.PullModelAsync" /> (progress-callback shape) and
///     <see cref="OllamaModelService.PullModelAsync" /> (async-stream shape) — so one mapping serves both.
/// </summary>
internal static class OllamaPullProgressMapper
{
    internal static PullProgress ToPullProgress(string modelName, PullModelResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new PullProgress
        {
            ModelName = modelName,
            Status = response.Status ?? string.Empty,
            TotalBytes = response.Total,
            CompletedBytes = response.Completed
        };
    }
}
