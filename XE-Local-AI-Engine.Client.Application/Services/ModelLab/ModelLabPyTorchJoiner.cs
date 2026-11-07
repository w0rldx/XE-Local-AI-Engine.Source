namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;

/// <summary>Links a GGUF descriptor's groups and tensors to PyTorch modules. A pure function; anything a rule does not match stays unlinked.</summary>
/// <remarks>
///     Block N is layer N only when the trunk counts match, the preflight's rule. A tensor with a Hugging Face name links through its path
///     inside the block, because the name's prefix (<c>model.language_model.layers.N.</c>) is not the module's (<c>model.layers.N.</c>). A
///     group with no such name links to the block children its component's names match; outside the blocks the same holds by last segment.
/// </remarks>
public static class ModelLabPyTorchJoiner
{
    public static ModelLabPyTorchJoin Join(ModelLabArchitectureResponse descriptor, ModelLabPyTorchIndex index)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(index);

        var components = new List<ModelLabPyTorchComponentLink>();
        var tensors = new List<ModelLabPyTorchTensorLink>();
        foreach (var group in descriptor.NonBlock)
        {
            AddLink(components, group.ComponentId, index.Outside.Where(module => OutsideMatches(group.Component, LastSegment(module.Path))).Select(static module => module.Path));
        }

        var trunk = descriptor.Blocks.Where(static block => block.Kind != ModelLabArchitectureBuilder.MtpKind).ToList();
        string? reason = null;
        if (descriptor.Support == "unreadable")
        {
            reason = "ggufUnreadable";
        }
        else if (trunk.Count != index.Blocks.Count)
        {
            reason = "blockCountMismatch";
        }
        else
        {
            var byIndex = index.Blocks.ToDictionary(static block => block.Index);
            foreach (var block in trunk)
            {
                if (byIndex.TryGetValue(block.Index, out var layer))
                {
                    JoinBlock(block, layer, components, tensors);
                }
            }
        }

        return new ModelLabPyTorchJoin
        {
            BlocksJoined = reason is null,
            Reason = reason,
            GgufTrunkBlockCount = trunk.Count,
            PyTorchBlockCount = index.Blocks.Count,
            Components = components,
            Tensors = tensors
        };
    }

    /// <summary><c>model.language_model.layers.3.mlp.gate_proj.weight</c> → <c>mlp.gate_proj</c>; a parameter of the block itself → empty; another layer → null.</summary>
    internal static string? InBlockModuleOrNull(string? hfName, int blockIndex)
    {
        if (hfName is null)
        {
            return null;
        }

        var marker = string.Create(CultureInfo.InvariantCulture, $"layers.{blockIndex}.");
        int start;
        if (hfName.StartsWith(marker, StringComparison.Ordinal))
        {
            start = marker.Length;
        }
        else
        {
            var at = hfName.IndexOf("." + marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            start = at + 1 + marker.Length;
        }

        var rest = hfName[start..];
        var last = rest.LastIndexOf('.');
        return last < 0 ? string.Empty : rest[..last];
    }

    private static void JoinBlock(ModelLabArchitectureBlock block, ModelLabPyTorchBlock layer, List<ModelLabPyTorchComponentLink> components, List<ModelLabPyTorchTensorLink> tensors)
    {
        var modules = layer.Modules.Select(static module => module.Path).ToHashSet(StringComparer.Ordinal);
        var prefix = layer.Path + ".";
        foreach (var group in block.Groups)
        {
            if (!group.Tensors.Any(static tensor => tensor.HfName is not null))
            {
                AddLink(components, group.ComponentId, modules.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)
                                                                             && !path.AsSpan(prefix.Length).Contains('.')
                                                                             && ChildMatches(group.Component, path[prefix.Length..])).Order(StringComparer.Ordinal));
                continue;
            }

            var children = new List<string>();
            foreach (var tensor in group.Tensors)
            {
                if (InBlockModuleOrNull(tensor.HfName, block.Index) is not { } inBlock)
                {
                    continue;
                }

                var path = inBlock.Length == 0 ? layer.Path : prefix + inBlock;
                if (!modules.Contains(path))
                {
                    continue;
                }

                tensors.Add(new ModelLabPyTorchTensorLink
                {
                    GgufName = tensor.GgufName,
                    ModulePath = path
                });
                var dot = inBlock.IndexOf('.', StringComparison.Ordinal);
                children.Add(dot < 0 ? path : prefix + inBlock[..dot]);
            }

            AddLink(components, group.ComponentId, children);
        }
    }

    private static bool ChildMatches(string component, string name) =>
        component switch
        {
            ModelLabArchitectureBuilder.Attention => name is "self_attn" or "attn" or "attention",
            ModelLabArchitectureBuilder.Ssm => name is "linear_attn" or "mixer" or "mamba" or "ssm",
            ModelLabArchitectureBuilder.Ffn => name is "mlp" or "feed_forward" or "moe" or "block_sparse_moe",
            ModelLabArchitectureBuilder.Norm => name.Contains("norm", StringComparison.Ordinal),
            _ => false
        };

    private static bool OutsideMatches(string component, string name) =>
        component switch
        {
            "embedding" => name is "embed_tokens" or "wte" or "word_embeddings" or "embeddings" or "tok_embeddings",
            ModelLabArchitectureBuilder.Norm => name is "norm" or "norm_f" or "ln_f" or "final_layernorm" or "final_norm",
            "output" => name is "lm_head" or "embed_out" or "output",
            _ => false
        };

    private static void AddLink(List<ModelLabPyTorchComponentLink> components, string componentId, IEnumerable<string> paths)
    {
        var distinct = paths.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 0)
        {
            components.Add(new ModelLabPyTorchComponentLink
            {
                ComponentId = componentId,
                ModulePaths = distinct
            });
        }
    }

    private static string LastSegment(string path) =>
        path[(path.LastIndexOf('.') + 1)..];
}
