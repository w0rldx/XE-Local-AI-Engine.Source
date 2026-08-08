namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     <see cref="IImageModelDiscovery" /> over <see cref="HfHubClient" />: searches the Hub's
///     <c>pipeline_tag=text-to-image</c> facet and lists one repo's selectable weight files with a suggested part role.
/// </summary>
/// <remarks>
///     Two rules of the GGUF lane are deliberately NOT carried over, because copying them would make image discovery
///     return almost nothing:
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>No quant token is required.</b> <c>HuggingFaceGgufDiscovery</c> drops any file whose name does not
///                 parse to a llama.cpp quant. A VAE (<c>qwen_image_vae.safetensors</c>) and a CLIP encoder
///                 (<c>clip_l.safetensors</c>) never carry one, and they are exactly the parts a multi-part install
///                 needs.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b><c>.safetensors</c> counts.</b> stable-diffusion.cpp loads both containers, and real file-sets mix
///                 them (a GGUF diffusion transformer next to a <c>.safetensors</c> VAE).
///             </description>
///         </item>
///     </list>
///     <para>
///         <b>Sharded weight files are excluded, on purpose.</b> A part is one file:
///         <see cref="ImageModelPartRequest.FileName" /> is a single string and the store downloads exactly one blob per
///         role, so a <c>-00001-of-00003</c> member would install a fragment that cannot be loaded. Rather than let a
///         picker offer a broken install, <see cref="IsShardMember" /> filters shard members out of both search and
///         inspection. Supporting them means making a part a list of files end-to-end (request, store, registry,
///         argument builder) — a change well beyond discovery.
///     </para>
/// </remarks>
internal sealed partial class HuggingFaceImageModelDiscovery : IImageModelDiscovery
{
    /// <summary>The Hub task facet that identifies a diffusion model. This is what makes the search return images.</summary>
    private const string TextToImagePipelineTag = "text-to-image";

    private readonly HfHubClient _hubClient;
    private readonly ILogger<HuggingFaceImageModelDiscovery> _logger;

    public HuggingFaceImageModelDiscovery(HfHubClient hubClient, ILogger<HuggingFaceImageModelDiscovery> logger)
    {
        ArgumentNullException.ThrowIfNull(hubClient);
        ArgumentNullException.ThrowIfNull(logger);

        _hubClient = hubClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageRepoSummary>> SearchAsync(ImageModelSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var models = await _hubClient.ListModelsAsync(new HfHubClient.HubListQuery
            {
                PipelineTag = TextToImagePipelineTag,
                Filter = query.GgufOnly ? "gguf" : null,
                Sort = MapSort(query.Sort),
                Limit = query.Limit,
                SearchText = query.SearchText
            },
            ct).ConfigureAwait(false);

        var summaries = new List<ImageRepoSummary>(models.Count);
        foreach (var model in models)
        {
            // The listing carries filenames only (no sizes), which is enough to answer "does this repo hold anything
            // installable at all" — the same question the GGUF lane's HasUsableGguf answers.
            var hasUsableWeights = model.FileNames.Any(IsUsableWeightFile);
            if (!hasUsableWeights)
            {
                continue;
            }

            summaries.Add(new ImageRepoSummary(model.RepoId,
                model.IsGated,
                model.Downloads,
                model.Likes,
                model.LastModified,
                model.License,
                HasUsableWeights: true,
                GgufPublisherTrust.IsTrustedPublisher(model.RepoId)));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<ImageRepoDetail> InspectRepoAsync(string repoId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        var detail = await _hubClient.GetRepoAsync(repoId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return new ImageRepoDetail(repoId, IsGated: false, License: null, []);
        }

        var files = new List<ImageRepoFile>();
        foreach (var file in detail.Files)
        {
            if (!IsUsableWeightFile(file.FileName))
            {
                _logger.LogDebug("Skipping a non-installable file during image repo inspection.");
                continue;
            }

            files.Add(new ImageRepoFile(file.FileName,
                ResolveFormat(file.FileName),
                file.SizeBytes,
                file.Sha256,
                SuggestRole(file.FileName)));
        }

        return new ImageRepoDetail(detail.RepoId, detail.IsGated, detail.License, files);
    }

    private static string MapSort(ImageModelSearchSort sort)
    {
        return sort switch
        {
            ImageModelSearchSort.Downloads => "downloads",
            ImageModelSearchSort.Likes => "likes",
            ImageModelSearchSort.LastModified => "lastModified",
            _ => "trendingScore"
        };
    }

    /// <summary>
    ///     Guesses which part role a repo file fills from its name. Ordered most-specific-first: the vision tower is
    ///     checked before the LLM it belongs to, and both before the generic diffusion fallback. A wrong guess costs the
    ///     operator one dropdown change; the backend re-validates whatever is actually submitted.
    /// </summary>
    internal static ImageModelPartRole SuggestRole(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // Match on the WHOLE relative path: repos routinely carry the role in a directory rather than the leaf
        // ("split_files/vae/qwen_image_vae.safetensors", "VAE/Qwen_Image-VAE.safetensors", "text_encoders/clip_l...").
        var path = fileName.Replace(oldChar: '\\', newChar: '/');

        if (Contains(path, "mmproj") || Contains(path, "llm_vision") || Contains(path, "vision_tower"))
        {
            return ImageModelPartRole.LlmVision;
        }

        if (Contains(path, "vae") || IsFluxAutoencoderLeaf(path))
        {
            return ImageModelPartRole.Vae;
        }

        if (Contains(path, "clip_g") || Contains(path, "clip-g"))
        {
            return ImageModelPartRole.ClipG;
        }

        if (Contains(path, "clip_l") || Contains(path, "clip-l"))
        {
            return ImageModelPartRole.ClipL;
        }

        if (Contains(path, "t5"))
        {
            return ImageModelPartRole.T5;
        }

        // Qwen-Image conditions on a full Qwen2.5-VL language model rather than a CLIP/T5 encoder, so anything that
        // looks like a VL/text-encoder LLM rides the Llm role (sd-server's --llm).
        if (Contains(path, "qwen2.5-vl") || Contains(path, "qwen_2.5_vl") || Contains(path, "qwen2_5_vl") || Contains(path, "text_encoder"))
        {
            return ImageModelPartRole.Llm;
        }

        return ImageModelPartRole.Diffusion;
    }

    private static bool Contains(string haystack, string needle)
    {
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    // FLUX ships its autoencoder as a bare "ae" file rather than anything containing "vae": second-state's repo alone
    // carries "ae.safetensors" AND "ae-f16.gguf", and both are VAEs. Matched on the leaf's stem so it cannot collide
    // with an unrelated file that merely happens to contain the letters "ae" (e.g. "flux1-schnell-Q4_0.gguf").
    private static bool IsFluxAutoencoderLeaf(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.Equals("ae", StringComparison.OrdinalIgnoreCase)
               || stem.StartsWith("ae-", StringComparison.OrdinalIgnoreCase)
               || stem.StartsWith("ae_", StringComparison.OrdinalIgnoreCase);
    }

    private static ImageWeightFormat ResolveFormat(string fileName)
    {
        return fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? ImageWeightFormat.Gguf : ImageWeightFormat.Safetensors;
    }

    // A file is installable when it is a weight container, is not a multi-file shard member, and its repo-supplied
    // path cannot escape the models directory. The path check is the same guard the store re-applies before writing —
    // filtering here just keeps an unusable name from ever reaching a picker.
    private static bool IsUsableWeightFile(string fileName)
    {
        return IsWeightFileName(fileName) && !IsShardMember(fileName) && GgufFilePath.IsSafeRelativePath(fileName);
    }

    private static bool IsWeightFileName(string fileName)
    {
        return fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);
    }

    // The two shard conventions a large diffusion quant ships under: llama.cpp's "-00001-of-00003.gguf" splits and
    // the HF "-00001-of-00003.safetensors" convention. See the type remarks for why these are excluded rather than
    // grouped the way HuggingFaceGgufDiscovery.GroupShards does.
    [GeneratedRegex(@"-\d{5}-of-\d{5}\.(?:gguf|safetensors)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex ShardSuffixRegex();

    internal static bool IsShardMember(string fileName)
    {
        return ShardSuffixRegex().IsMatch(fileName);
    }
}
