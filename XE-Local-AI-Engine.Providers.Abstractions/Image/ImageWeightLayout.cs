namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

using System.Text.RegularExpressions;

/// <summary>
///     Recognizes weight files stable-diffusion.cpp cannot load as the diffusion part: Diffusers-layout components and
///     shard members. The one predicate both the repo inspection and the download boundary apply.
/// </summary>
/// <remarks>
///     A Diffusers repo splits the model into per-component folders (<c>transformer/diffusion_pytorch_model.safetensors</c>,
///     often sharded, described by <c>config.json</c>/<c>model_index.json</c>). sd-server reads one self-describing checkpoint
///     and exits with <c>get sd version from file failed</c> on such a component, after a multi-gigabyte download.
/// </remarks>
public static partial class ImageWeightLayout
{
    /// <summary>Machine-readable reason carried on an inspected repo file that cannot be the diffusion part.</summary>
    public const string DiffusersLayoutUnsupportedCode = "diffusers_layout_unsupported";

    /// <summary>Operator-facing text for <see cref="DiffusersLayoutUnsupportedCode" />.</summary>
    public const string DiffusersLayoutUnsupportedMessage =
        "This repository stores the model in Diffusers layout, which the local image runtime cannot load. Pick a single-file GGUF or safetensors checkpoint.";

    private const string SafetensorsExtension = ".safetensors";

    /// <summary>
    ///     Whether <paramref name="fileName" /> cannot be the diffusion part. Passing every repo file name adds the rule for a
    ///     <c>model*.safetensors</c> beside a <c>config.json</c>; without it only the name rules apply.
    /// </summary>
    public static bool IsUnloadableDiffusionFile(string fileName, IReadOnlyCollection<string>? repoFileNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var path = fileName.Replace(oldChar: '\\', newChar: '/');
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        if (IsShardMember(path) || IsSafetensorsStartingWith(leaf, "diffusion_pytorch_model"))
        {
            return true;
        }

        if (repoFileNames is null || !IsSafetensorsStartingWith(leaf, "model"))
        {
            return false;
        }

        var siblingConfig = path.Length == leaf.Length ? "config.json" : $"{path[..^leaf.Length]}config.json";
        return repoFileNames.Any(name => name.Equals("model_index.json", StringComparison.OrdinalIgnoreCase)
                                         || name.Replace(oldChar: '\\', newChar: '/').Equals(siblingConfig, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Matches llama.cpp's <c>-00001-of-00003.gguf</c> splits and the Hugging Face <c>-00001-of-00003.safetensors</c>
    ///     convention. A part is ONE file end-to-end, so a shard member would install an unloadable fragment.
    /// </summary>
    public static bool IsShardMember(string fileName)
    {
        return ShardSuffixRegex().IsMatch(fileName);
    }

    private static bool IsSafetensorsStartingWith(string leaf, string prefix)
    {
        return leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && leaf.EndsWith(SafetensorsExtension, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.(?:gguf|safetensors)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex ShardSuffixRegex();
}
