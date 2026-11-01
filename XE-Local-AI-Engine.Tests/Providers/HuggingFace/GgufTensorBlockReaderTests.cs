namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="GgufTensorBlockReader" /> over hand-built GGUF bytes: tensor infos parse with ggml byte sizes, and every
///     bound on the untrusted file ends the read with a typed failure instead of an exception.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GgufTensorBlockReaderTests
{
    private const uint F32 = 0;
    private const uint Q8_0 = 8;
    private const uint Q4_K = 12;

    [Test]
    public async Task Read_ParsesTensorInfos_WithShapeTypeOffsetAndByteSize()
    {
        var bytes = BaseHeader()
                    .WithTensor("token_embd.weight", [256, 100], Q4_K, offset: 0)
                    .WithTensor("blk.0.attn_norm.weight", [256], F32, offset: 14_400)
                    .WithTensor("output.weight", [256, 10], Q8_0, offset: 15_424)
                    .Build();

        var result = await ReadAsync(bytes);

        AssertEx.Equal(GgufTensorReadFailure.None, result.Failure);
        AssertEx.Equal("qwen35", result.Architecture!);
        AssertEx.Equal(expected: 32L, result.BlockCount!.Value);
        AssertEx.Equal(expected: 3L, result.TensorCount);
        AssertEx.Equal(bytes.Length, result.HeaderBytes);
        // No general.parameter_count: the count is the exact sum of element counts (25 600 + 256 + 2 560).
        AssertEx.Equal(expected: 28_416L, result.ParamCount!.Value);
        var embedding = result.Tensors[0];
        AssertEx.Equal("token_embd.weight", embedding.Name);
        AssertEx.Equal("256x100", string.Join(separator: 'x', embedding.Dimensions));
        AssertEx.Equal("Q4_K", embedding.GgmlTypeName!);
        // 100 rows of one 256-element Q4_K block (144 bytes each).
        AssertEx.Equal(expected: 14_400L, embedding.ByteSize!.Value);
        AssertEx.Equal(expected: 1_024L, result.Tensors[1].ByteSize!.Value);
        AssertEx.Equal(expected: 14_400L, result.Tensors[1].Offset);
        // 10 rows of eight 32-element Q8_0 blocks (34 bytes each).
        AssertEx.Equal(expected: 2_720L, result.Tensors[2].ByteSize!.Value);
    }

    [Test]
    public async Task Read_DeclaredParameterCount_WinsOverTheTensorSum()
    {
        var bytes = BaseHeader()
                    .WithUint64("general.parameter_count", value: 9_000)
                    .WithTensor("blk.0.attn_norm.weight", [256], F32, offset: 0)
                    .Build();

        var result = await ReadAsync(bytes);

        AssertEx.Equal(expected: 9_000L, result.ParamCount!.Value);
        AssertEx.Equal(expected: 256L, result.Tensors[0].ElementCount);
    }

    [Test]
    public async Task Read_TruncatedTensorBlock_ReturnsTruncatedFailure()
    {
        var bytes = BaseHeader()
                    .WithTensor("blk.0.attn_q.weight", [256, 256], Q4_K, offset: 0)
                    .Build();

        var result = await ReadAsync(bytes[..^5]);

        AssertEx.Equal(GgufTensorReadFailure.Truncated, result.Failure);
        AssertEx.Empty(result.Tensors);
        AssertEx.Equal("qwen35", result.Architecture!);
        AssertEx.Null(result.ParamCount);
    }

    [Test]
    public async Task Read_DeclaredTensorCountTheFileCannotHold_ReturnsTruncatedFailure()
    {
        var bytes = BaseHeader()
                    .WithTensor("blk.0.attn_q.weight", [256], F32, offset: 0)
                    .WithDeclaredTensorCount(50_000)
                    .Build();

        var result = await ReadAsync(bytes);

        AssertEx.Equal(GgufTensorReadFailure.Truncated, result.Failure);
    }

    [Test]
    public async Task Read_TensorCountAboveTheCap_ReturnsMalformedFailure()
    {
        var bytes = BaseHeader().WithDeclaredTensorCount(1UL << 40).Build();

        var result = await ReadAsync(bytes);

        AssertEx.Equal(GgufTensorReadFailure.Malformed, result.Failure);
    }

    [Test]
    public async Task Read_MoreThanEightDimensions_ReturnsMalformedFailure()
    {
        var bytes = BaseHeader()
                    .WithTensor("blk.0.weird.weight", [1, 1, 1, 1, 1, 1, 1, 1, 1], F32, offset: 0)
                    .Build();

        var result = await ReadAsync(bytes);

        AssertEx.Equal(GgufTensorReadFailure.Malformed, result.Failure);
    }

    [Test]
    public async Task Read_ElementCountOverflowingLong_ReturnsMalformedFailure()
    {
        var bytes = BaseHeader()
                    .WithTensor("blk.0.huge.weight", [1UL << 40, 1UL << 40], F32, offset: 0)
                    .Build();

        var result = await ReadAsync(bytes);

        AssertEx.Equal(GgufTensorReadFailure.Malformed, result.Failure);
    }

    [Test]
    public async Task Read_OverlongTensorName_ReturnsMalformedFailure()
    {
        var bytes = BaseHeader()
                    .WithTensor(new string('x', 300), [256], F32, offset: 0)
                    .Build();

        var result = await ReadAsync(bytes);

        AssertEx.Equal(GgufTensorReadFailure.Malformed, result.Failure);
    }

    [Test]
    public async Task Read_UnknownGgmlType_IsListedWithoutNameOrSize()
    {
        var bytes = BaseHeader()
                    .WithTensor("blk.0.future.weight", [256, 4], ggmlType: 99, offset: 0)
                    .WithTensor("blk.0.ragged.weight", [100, 4], Q4_K, offset: 4_096)
                    .Build();

        var result = await ReadAsync(bytes);

        AssertEx.Equal(GgufTensorReadFailure.None, result.Failure);
        AssertEx.Equal(expected: 99u, result.Tensors[0].GgmlType);
        AssertEx.Null(result.Tensors[0].GgmlTypeName);
        AssertEx.Null(result.Tensors[0].ByteSize);
        // A row of 100 elements does not fill a 256-element Q4_K block: known type, no honest size.
        AssertEx.Equal("Q4_K", result.Tensors[1].GgmlTypeName!);
        AssertEx.Null(result.Tensors[1].ByteSize);
    }

    [Test]
    public async Task Read_MultiMebibyteTokenizerBeforeTheTensorBlock_ReadsTemplateAndTensors()
    {
        const string template = "{% for message in messages %}{{ message.content }}{% endfor %}";
        var vocab = new string[400_000];
        for (var i = 0; i < vocab.Length; i++)
        {
            vocab[i] = $"token_{i}";
        }

        var bytes = BaseHeader()
                    .WithStringArray("tokenizer.ggml.tokens", vocab)
                    .WithString("tokenizer.chat_template", template)
                    .WithTensor("token_embd.weight", [256, 400_000], Q4_K, offset: 0)
                    .WithTensor("output_norm.weight", [256], F32, offset: 57_600_000)
                    .Build();
        AssertEx.True(bytes.Length > 4 * 1024 * 1024, "the vocab must outgrow several reader buffers.");

        var result = await ReadAsync(bytes);

        AssertEx.Equal(GgufTensorReadFailure.None, result.Failure);
        AssertEx.Equal(template, result.ChatTemplate!);
        AssertEx.Equal(expected: 2, result.Tensors.Count);
        AssertEx.Equal("output_norm.weight", result.Tensors[1].Name);
        AssertEx.Equal(bytes.Length, result.HeaderBytes);
    }

    [Test]
    public async Task Read_NonGgufBytes_ReturnsUnreadableFailure()
    {
        var result = await ReadAsync("this is plainly not a GGUF header"u8.ToArray());

        AssertEx.Equal(GgufTensorReadFailure.Unreadable, result.Failure);
        AssertEx.Empty(result.Tensors);
    }

    [Test]
    public async Task ReadFile_MissingFile_ReturnsUnreadableFailure()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();

        var result = await GgufTensorBlockReader.ReadFileAsync(dir.FilePath("never-downloaded.gguf"), CancellationToken.None);

        AssertEx.Equal(GgufTensorReadFailure.Unreadable, result.Failure);
    }

    private static GgufHeaderBytesBuilder BaseHeader()
    {
        return new GgufHeaderBytesBuilder()
               .WithString("general.architecture", "qwen35")
               .WithUint32("qwen35.block_count", value: 32);
    }

    private static async Task<GgufTensorInspection> ReadAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await GgufTensorBlockReader.ReadAsync(stream, CancellationToken.None);
    }
}
