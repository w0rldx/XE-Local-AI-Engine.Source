namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>One provider-owned quantization detection order shared by preview, execution, and recovery.</summary>
internal static class GgufQuantDetector
{
    public static string? Detect(string fileName, GgufStrictHeaderParser.StrictHeader header)
    {
        return GgufQuantParser.TryParse(Path.GetFileName(fileName))
               ?? GgufStrictHeaderParser.ResolveQuantization(header);
    }

    public static bool IsCanonical(string? quantization)
    {
        return !string.IsNullOrWhiteSpace(quantization)
               && string.Equals(GgufQuantParser.TryParse($"model-{quantization}.gguf"), quantization, StringComparison.Ordinal);
    }
}
