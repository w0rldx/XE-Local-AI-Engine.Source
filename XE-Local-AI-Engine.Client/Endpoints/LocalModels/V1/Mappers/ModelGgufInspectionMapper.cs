namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Maps a <see cref="GgufTensorInspection" /> onto the wire response, grouping <c>blk.&lt;n&gt;.*</c> tensors per block.</summary>
internal static class ModelGgufInspectionMapper
{
    private const string BlockPrefix = "blk.";

    public static ModelGgufInspectionResponse ToResponse(this GgufTensorInspection inspection, string modelName)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        var blocks = new SortedDictionary<int, List<ModelGgufTensorDto>>();
        var nonBlock = new List<ModelGgufTensorDto>();
        foreach (var tensor in inspection.Tensors)
        {
            var dto = ToDto(tensor);
            if (BlockIndex(tensor.Name) is { } index)
            {
                if (!blocks.TryGetValue(index, out var members))
                {
                    members = [];
                    blocks[index] = members;
                }

                members.Add(dto);
            }
            else
            {
                nonBlock.Add(dto);
            }
        }

        return new ModelGgufInspectionResponse
        {
            ModelName = modelName,
            ReadFailure = inspection.Failure == GgufTensorReadFailure.None ? null : JsonNamingPolicyCamel(inspection.Failure),
            GgufVersion = inspection.Version is { } version ? (int)Math.Min(version, int.MaxValue) : null,
            Architecture = inspection.Architecture,
            DisplayName = inspection.ModelName,
            ParameterCount = inspection.ParamCount,
            FileType = inspection.FileType,
            BlockCount = inspection.BlockCount,
            AttentionHeadCount = inspection.AttentionHeadCount,
            AttentionHeadCountKv = inspection.AttentionHeadCountKv,
            EmbeddingLength = inspection.EmbeddingLength,
            ContextLength = inspection.ContextLength,
            ExpertCount = inspection.ExpertCount,
            ChatTemplate = inspection.ChatTemplate,
            SplitCount = inspection.SplitCount,
            TensorCount = inspection.TensorCount,
            TotalTensorBytes = SumOrNull(inspection.Tensors.Select(static tensor => tensor.ByteSize)),
            HeaderBytes = inspection.HeaderBytes,
            FileSizeBytes = inspection.FileSizeBytes,
            Blocks =
            [
                .. blocks.Select(static pair => new ModelGgufTensorBlockDto
                {
                    Index = pair.Key,
                    ByteSize = SumOrNull(pair.Value.Select(static tensor => tensor.ByteSize)),
                    Tensors = pair.Value
                })
            ],
            NonBlockTensors = nonBlock
        };
    }

    private static ModelGgufTensorDto ToDto(GgufTensorInfo tensor)
    {
        return new ModelGgufTensorDto
        {
            Name = tensor.Name,
            Shape = tensor.Dimensions,
            GgmlType = tensor.GgmlTypeName,
            GgmlTypeId = (int)Math.Min(tensor.GgmlType, int.MaxValue),
            Offset = tensor.Offset,
            ByteSize = tensor.ByteSize
        };
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

    private static long? SumOrNull(IEnumerable<long?> sizes)
    {
        var total = 0L;
        foreach (var size in sizes)
        {
            if (size is not { } value)
            {
                return null;
            }

            total = checked(total + value);
        }

        return total;
    }

    private static string JsonNamingPolicyCamel(GgufTensorReadFailure failure)
    {
        var name = failure.ToString();
        return string.Concat(char.ToLowerInvariant(name[0]).ToString(), name[1..]);
    }
}
