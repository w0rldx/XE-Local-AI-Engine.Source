namespace XE_Local_AI_Engine.Providers.Ollama;

using OllamaSharp.Models;

/// <summary>
///     Reads the name Ollama reports for an installed model. The list endpoint fills <c>model</c> on current daemons
///     and only <c>name</c> on older ones, so every surface that keys on the name must read it identically: the
///     catalog builds its classification dictionary with this key and the picker looks the entry up with it, and a
///     drift between the two would silently miss every lookup rather than fail.
/// </summary>
internal static class OllamaModelName
{
    /// <summary>Returns the model's reported name, or an empty string when the daemon reported neither field.</summary>
    public static string ReadModelName(this Model model) =>
        !string.IsNullOrWhiteSpace(model?.ModelName)
            ? model.ModelName
            : model?.Name ?? string.Empty;
}
