namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Turns a GGUF header inspection into the architecture descriptor; <c>qwen35</c> is the one detailed architecture.</summary>
public static class ModelLabArchitectureBuilder
{
    public const int DescriptorVersion = 1;

    internal const string Attention = "attention";
    internal const string Ssm = "ssm";
    internal const string Ffn = "ffn";
    internal const string Norm = "norm";
    internal const string Mtp = "mtp";
    internal const string Other = "other";

    internal const string FullAttentionKind = "fullAttention";
    internal const string LinearAttentionKind = "linearAttention";
    internal const string MtpKind = "mtp";

    private const string BlockPrefix = "blk.";

    // Group order inside a block: the mixer first, then the FFN, the norms, the MTP head and the rest.
    private static readonly string[] ComponentOrder = [Attention, Ssm, Ffn, Norm, Mtp, Other];

    public static ModelLabArchitectureResponse Build(string modelName, GgufTensorInspection inspection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(inspection);

        if (inspection.Failure != GgufTensorReadFailure.None)
        {
            var failure = inspection.Failure.ToString();
            return new ModelLabArchitectureResponse
            {
                DescriptorVersion = DescriptorVersion,
                ModelName = modelName,
                Architecture = inspection.Architecture,
                Support = "unreadable",
                ReadFailure = string.Concat(char.ToLowerInvariant(failure[0]).ToString(), failure[1..]),
                SuggestedBaseRepo = SuggestedBaseRepo(inspection),
                Blocks = [],
                Templates = [],
                NonBlock = []
            };
        }

        var detailed = string.Equals(inspection.Architecture, ModelLabQwen35Architecture.Architecture, StringComparison.Ordinal);
        var blockTensors = new SortedDictionary<int, List<GgufTensorInfo>>();
        var nonBlockTensors = new List<GgufTensorInfo>();
        foreach (var tensor in inspection.Tensors)
        {
            if (BlockIndex(tensor.Name) is { } index)
            {
                if (!blockTensors.TryGetValue(index, out var members))
                {
                    members = [];
                    blockTensors[index] = members;
                }

                members.Add(tensor);
            }
            else
            {
                nonBlockTensors.Add(tensor);
            }
        }

        // qwen35.cpp: n_k_heads = ssm_n_group (ssm.group_count), n_v_heads = ssm_dt_rank (ssm.time_step_rank).
        bool? valueHeadsDiffer = inspection is { SsmGroupCount: { } keyHeads, SsmTimeStepRank: { } valueHeads } ? keyHeads != valueHeads : null;
        var blocks = blockTensors.Select(pair => ToBlock(pair.Key, pair.Value, detailed, valueHeadsDiffer)).ToList();
        return new ModelLabArchitectureResponse
        {
            DescriptorVersion = DescriptorVersion,
            ModelName = modelName,
            Architecture = inspection.Architecture,
            Support = detailed ? "detailed" : "overview",
            ReadFailure = null,
            SuggestedBaseRepo = SuggestedBaseRepo(inspection),
            Blocks = blocks,
            Templates = detailed ? ModelLabQwen35Architecture.TemplatesFor(blocks.Select(static block => block.Kind)) : [],
            NonBlock = NonBlockGroups(nonBlockTensors, detailed)
        };
    }

    /// <summary>The source repo, else the first base model, when its URL is exactly <c>https://huggingface.co/owner/name</c>.</summary>
    internal static string? SuggestedBaseRepo(GgufTensorInspection inspection) =>
        RepoIdOrNull(inspection.SourceRepoUrl) ?? RepoIdOrNull(inspection.BaseModelRepoUrl);

    private static string? RepoIdOrNull(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.UserInfo.Length > 0)
        {
            return null;
        }

        // A dataset or a space has the same two-segment shape under its own prefix, and is no model.
        var repoId = uri.AbsolutePath.Trim('/');
        return HuggingFaceRepoId.IsValid(repoId) && !repoId.StartsWith("datasets/", StringComparison.Ordinal) && !repoId.StartsWith("spaces/", StringComparison.Ordinal)
            ? repoId
            : null;
    }

    // First match wins: an MTP block also carries full attention, and a linear block also carries attn_qkv, which
    // outside a linear block is full attention fused into one tensor.
    internal static string BlockKind(IReadOnlyList<GgufTensorInfo> tensors)
    {
        if (tensors.Any(static tensor => Stem(tensor.Name) == "nextn"))
        {
            return MtpKind;
        }

        if (tensors.Any(static tensor => Stem(tensor.Name).StartsWith("ssm_", StringComparison.Ordinal)))
        {
            return LinearAttentionKind;
        }

        return tensors.Any(static tensor => Stem(tensor.Name) is "attn_q" or "attn_qkv") ? FullAttentionKind : Other;
    }

    // By the name after "blk.N.". In a linear block attn_qkv and attn_gate feed the delta net, so they belong to it.
    internal static string ComponentOf(string ggufName, string blockKind)
    {
        var stem = Stem(ggufName);
        if (stem == "nextn")
        {
            return Mtp;
        }

        if (stem.StartsWith("ssm_", StringComparison.Ordinal) || (blockKind == LinearAttentionKind && stem is ("attn_qkv" or "attn_gate")))
        {
            return Ssm;
        }

        if (stem.EndsWith("_norm", StringComparison.Ordinal) && stem is not ("attn_q_norm" or "attn_k_norm"))
        {
            return Norm;
        }

        if (stem.StartsWith("attn_", StringComparison.Ordinal))
        {
            return Attention;
        }

        return stem.StartsWith("ffn_", StringComparison.Ordinal) ? Ffn : Other;
    }

    private static ModelLabArchitectureBlock ToBlock(int index, List<GgufTensorInfo> tensors, bool detailed, bool? valueHeadsDiffer)
    {
        var kind = BlockKind(tensors);
        var groups = tensors
                     .GroupBy(tensor => ComponentOf(tensor.Name, kind), StringComparer.Ordinal)
                     .OrderBy(static group => Array.IndexOf(ComponentOrder, group.Key))
                     .Select(group => new ModelLabArchitectureGroup
                     {
                         ComponentId = string.Create(CultureInfo.InvariantCulture, $"blk.{index}.{group.Key}"),
                         Component = group.Key,
                         Provenance = "read",
                         // The MTP block has no HF decoder layer, so none of its tensors gets a layer-indexed HF name.
                         Tensors = [.. group.Select(tensor => ToTensor(tensor, detailed && kind != MtpKind ? ModelLabQwen35Architecture.BlockHfName(tensor.Name, index, valueHeadsDiffer) : null))]
                     })
                     .ToList();

        return new ModelLabArchitectureBlock
        {
            Index = index,
            Kind = kind,
            ByteSize = SumOrNull(tensors),
            Groups = groups
        };
    }

    private static List<ModelLabArchitectureGroup> NonBlockGroups(List<GgufTensorInfo> tensors, bool detailed) =>
    [
        .. tensors
           .GroupBy(static tensor => tensor.Name switch
           {
               "token_embd.weight" => "embedding",
               "output_norm.weight" => "output_norm",
               "output.weight" => "output",
               _ => Other
           }, StringComparer.Ordinal)
           .Select(group => new ModelLabArchitectureGroup
           {
               ComponentId = group.Key,
               Component = group.Key == "output_norm" ? Norm : group.Key,
               Provenance = "read",
               Tensors = [.. group.Select(tensor => ToTensor(tensor, detailed ? ModelLabQwen35Architecture.NonBlockHfName(tensor.Name) : null))]
           })
    ];

    private static ModelLabArchitectureTensor ToTensor(GgufTensorInfo tensor, ModelLabQwen35Architecture.HfTensor? hf) =>
        new()
        {
            GgufName = tensor.Name,
            HfName = hf?.Name,
            Shape = tensor.Dimensions,
            GgmlType = tensor.GgmlTypeName,
            ByteSize = tensor.ByteSize,
            ValueRelation = hf is null ? null : ValueRelation(tensor, hf)
        };

    // A permutation outlives quantization (the converter permutes first), so "reordered" is kept for any stored type.
    private static string? ValueRelation(GgufTensorInfo tensor, ModelLabQwen35Architecture.HfTensor hf)
    {
        if (hf.Relation is null or ModelLabQwen35Architecture.Reordered)
        {
            return hf.Relation;
        }

        return tensor.GgmlTypeName is "F32" or "F16" or "BF16" or "F64" ? hf.Relation : "quantized";
    }

    // "blk.12.attn_q.weight" → 12; anything else (token_embd.weight, output.weight) is not a block tensor.
    private static int? BlockIndex(string name)
    {
        if (!name.StartsWith(BlockPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = name.IndexOf('.', BlockPrefix.Length);
        var digits = end < 0 ? name.AsSpan(BlockPrefix.Length) : name.AsSpan(BlockPrefix.Length, end - BlockPrefix.Length);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : null;
    }

    // "blk.12.attn_q_norm.weight" → "attn_q_norm"; "blk.64.nextn.eh_proj.weight" → "nextn".
    private static string Stem(string name)
    {
        var start = name.IndexOf('.', BlockPrefix.Length) + 1;
        if (start == 0)
        {
            return string.Empty;
        }

        var end = name.IndexOf('.', start);
        return end < 0 ? name[start..] : name[start..end];
    }

    private static long? SumOrNull(List<GgufTensorInfo> tensors) =>
        tensors.Any(static tensor => tensor.ByteSize is null) ? null : tensors.Sum(static tensor => tensor.ByteSize!.Value);
}
