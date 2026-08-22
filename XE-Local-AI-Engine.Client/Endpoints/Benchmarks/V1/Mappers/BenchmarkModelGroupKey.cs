namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     How a run's model name folds into the BASE model it is a build of.
///     <para>
///         The group key used to be the model CONTENT fingerprint, which is the exact opposite of what an operator
///         comparing quants needs: two quants of one model have different content by definition, so every quant became
///         its own group and "which quant of this model is best" could never be asked. The key is now the base model,
///         so a group is one model and its rows are that model's quants — and the group's best quant is simply its
///         top-ranked row.
///     </para>
/// </summary>
internal static class BenchmarkModelGroupKey
{
    /// <summary>
    ///     The base-model key of one run. For a Hugging Face model that is the repo id with the quant tag removed and
    ///     lowercased (repo ids are case-insensitive, so the same repo referenced with two casings must not split into
    ///     two groups); for an imported or trained model it is the operator's name with the same tag removed, case
    ///     preserved because that name is the identity the operator chose.
    /// </summary>
    /// <example><c>unsloth/Qwen3.8-27B-GGUF:Q4_K_M</c> → <c>unsloth/qwen3.8-27b-gguf</c>.</example>
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification =
            "A Hugging Face repo id is canonically lowercase and this key is displayed as the group's model, not compared as a security identifier; upper-casing it would put a name no registry uses in front of the operator.")]
    public static string From(string modelName, LocalModelOrigin? origin)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        var baseModel = WithoutQuantTag(modelName);
        return origin == LocalModelOrigin.HuggingFace ? baseModel.ToLowerInvariant() : baseModel;
    }

    /// <summary>
    ///     The quant tag an operator picked, which rides on the model name after the last colon
    ///     (<c>owner/Repo-GGUF:Q4_K_M</c>). Empty when the name carries none.
    /// </summary>
    public static string QuantTag(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        var separator = modelName.LastIndexOf(':');
        return separator < 0 || separator == modelName.Length - 1 ? string.Empty : modelName[(separator + 1)..];
    }

    /// <summary>
    ///     The name without its trailing <c>:tag</c>. A Hugging Face repo id never contains a colon of its own, so the
    ///     last one is always the tag separator; a name with no colon, or one ending in a bare colon, is returned
    ///     unchanged rather than truncated to nothing.
    /// </summary>
    public static string WithoutQuantTag(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        var separator = modelName.LastIndexOf(':');
        return separator <= 0 || separator == modelName.Length - 1 ? modelName : modelName[..separator];
    }
}
