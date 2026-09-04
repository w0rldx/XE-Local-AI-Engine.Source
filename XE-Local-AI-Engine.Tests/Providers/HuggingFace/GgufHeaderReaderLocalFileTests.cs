namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="GgufHeaderReader.ReadHeaderFromFileAsync" />: reads the GGUF header from a local file and extracts
///     <c>context_length</c>, returning all-null metadata (never throwing) for a missing / empty / non-GGUF file. The
///     HTTP client is wired over a throwing handler to prove the local-file path never touches the network.
/// </summary>
public sealed class GgufHeaderReaderLocalFileTests
{
    [Test]
    public async Task ReadHeaderFromFile_ExtractsContextLength_FromQwen2Header()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("qwen2-0.5b-Q4_K_M.gguf");
        // Qwen2.5-0.5B-Instruct advertises qwen2.context_length = 32768 (a u32 keyed by general.architecture=qwen2).
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen2")
                     .WithUint32("qwen2.context_length", value: 32768)
                     .Build();
        await File.WriteAllBytesAsync(path, header);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Equal("qwen2", metadata.Architecture!);
        AssertEx.Equal(expected: 32768L, metadata.ContextLength!.Value);
    }

    [Test]
    public async Task ReadHeaderFromFile_ExtractsChatTemplate_AfterTheVocabArray()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("qwen2-0.5b-tooltemplate.gguf");
        // tokenizer.chat_template usually lands AFTER the large tokenizer.ggml.tokens vocab array, so emit a
        // representative array between the early metadata and the template to prove the grow loop reaches it.
        const string template = "{% for tool in tools %}{{ tool }}{% endfor %}<|im_start|>";
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen2")
                     .WithUint32("qwen2.context_length", value: 32768)
                     .WithStringArray("tokenizer.ggml.tokens", BuildVocab(2048))
                     .WithString("tokenizer.chat_template", template)
                     .Build();
        await File.WriteAllBytesAsync(path, header);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Equal(template, metadata.ChatTemplate!);
    }

    [Test]
    public async Task ReadHeaderFromFile_ExtractsExpertCount_AndFlagsMoe()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("qwen3moe-Q4_K_M.gguf");
        // A Mixture-of-Experts GGUF advertises <arch>.expert_count (a u32) — e.g. Qwen3-MoE writes 8 experts.
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen3moe")
                     .WithUint32("qwen3moe.context_length", value: 32768)
                     .WithUint32("qwen3moe.expert_count", value: 8)
                     .Build();
        await File.WriteAllBytesAsync(path, header);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Equal(expected: 8L, metadata.ExpertCount!.Value);
        AssertEx.True(metadata.IsMoe);
    }

    [Test]
    public async Task ReadHeaderFromFile_DenseModel_NoExpertCount_IsNotMoe()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("qwen2-0.5b-dense.gguf");
        // A dense model omits the expert_count key entirely.
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen2")
                     .WithUint32("qwen2.context_length", value: 32768)
                     .Build();
        await File.WriteAllBytesAsync(path, header);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Null(metadata.ExpertCount);
        AssertEx.False(metadata.IsMoe);
    }

    [Test]
    public async Task ReadHeaderFromFile_ExtractsAttentionKeyValueLength_AndSlidingWindow_Gemma3()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("gemma3-12b-Q4_K_M.gguf");
        // Gemma3 writes explicit attention.key_length / value_length (256) and attention.sliding_window (1024). The 5:1
        // local:global layer pattern (stride 6) is resolved from the per-architecture default when the header omits it.
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "gemma3")
                     .WithUint32("gemma3.block_count", value: 48)
                     .WithUint32("gemma3.attention.head_count", value: 16)
                     .WithUint32("gemma3.attention.head_count_kv", value: 8)
                     .WithUint32("gemma3.attention.key_length", value: 256)
                     .WithUint32("gemma3.attention.value_length", value: 256)
                     .WithUint32("gemma3.attention.sliding_window", value: 1024)
                     .WithUint32("gemma3.context_length", value: 131072)
                     .Build();
        await File.WriteAllBytesAsync(path, header);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Equal(expected: 256L, metadata.AttentionKeyLength!.Value);
        AssertEx.Equal(expected: 256L, metadata.AttentionValueLength!.Value);
        AssertEx.Equal(expected: 1024L, metadata.SlidingWindow!.Value);
        AssertEx.Equal(expected: 6L, metadata.SlidingWindowPattern!.Value); // gemma3 arch default (5:1 local:global)
    }

    [Test]
    public async Task ReadHeaderFromFile_ExplicitSlidingWindowPattern_OverridesArchDefault()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("gemma3-custom.gguf");
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "gemma3")
                     .WithUint32("gemma3.attention.sliding_window", value: 512)
                     .WithUint32("gemma3.attention.sliding_window_pattern", value: 4)
                     .Build();
        await File.WriteAllBytesAsync(path, header);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Equal(expected: 4L, metadata.SlidingWindowPattern!.Value); // explicit header key wins over the arch default
    }

    [Test]
    public async Task ReadHeaderFromFile_DenseQwen3_ExplicitKeyValue_NoSlidingWindow()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("qwen35-dense.gguf");
        // Qwen3 is dense (no sliding window) but DOES write explicit key/value length 128 — its head_dim is decoupled
        // from the embedding width, which is exactly why the derived head_dim under-estimates its KV cache.
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen35")
                     .WithUint32("qwen35.attention.key_length", value: 128)
                     .WithUint32("qwen35.attention.value_length", value: 128)
                     .Build();
        await File.WriteAllBytesAsync(path, header);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Equal(expected: 128L, metadata.AttentionKeyLength!.Value);
        AssertEx.Equal(expected: 128L, metadata.AttentionValueLength!.Value);
        AssertEx.Null(metadata.SlidingWindow);
        AssertEx.Null(metadata.SlidingWindowPattern); // no window key AND qwen35 has no interleaved-SWA arch default
    }

    [Test]
    public async Task ReadHeaderFromFile_ExtractsMlaKeyValueLengths_DeepSeek2()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("deepseek-v2-lite-Q4_K_M.gguf");
        // deepseek2 writes BOTH attention.key_length_mla (kv_lora_rank 512 + rope.dimension_count 64 = 576) and
        // attention.value_length_mla (512) beside the ordinary explicit lengths. Both keys present and positive is
        // llama.cpp's is_mla() test, and the reader must carry them without touching the ordinary pair.
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "deepseek2")
                     .WithUint32("deepseek2.block_count", value: 27)
                     .WithUint32("deepseek2.attention.head_count", value: 16)
                     .WithUint32("deepseek2.attention.head_count_kv", value: 16)
                     .WithUint32("deepseek2.attention.key_length", value: 192)
                     .WithUint32("deepseek2.attention.value_length", value: 128)
                     .WithUint32("deepseek2.attention.key_length_mla", value: 576)
                     .WithUint32("deepseek2.attention.value_length_mla", value: 512)
                     .Build();
        await File.WriteAllBytesAsync(path, header);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Equal(expected: 576L, metadata.AttentionKeyLengthMla!.Value);
        AssertEx.Equal(expected: 512L, metadata.AttentionValueLengthMla!.Value);
        AssertEx.Equal(expected: 192L, metadata.AttentionKeyLength!.Value);
        AssertEx.Equal(expected: 128L, metadata.AttentionValueLength!.Value);
    }

    [Test]
    public async Task ReadHeaderFromFile_NonMlaModel_LeavesTheMlaLengthsNull()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("qwen35-non-mla.gguf");
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen35")
                     .WithUint32("qwen35.attention.key_length", value: 128)
                     .WithUint32("qwen35.attention.value_length", value: 128)
                     .Build();
        await File.WriteAllBytesAsync(path, header);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Null(metadata.AttentionKeyLengthMla);
        AssertEx.Null(metadata.AttentionValueLengthMla);
    }

    private static string[] BuildVocab(int count)
    {
        var tokens = new string[count];
        for (var i = 0; i < count; i++)
        {
            tokens[i] = $"token_{i}";
        }

        return tokens;
    }

    [Test]
    public async Task ReadHeaderFromFile_NonGgufFile_ReturnsEmpty()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("not-a-model.gguf");
        await File.WriteAllBytesAsync(path, "this is plainly not a GGUF header"u8.ToArray());
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Null(metadata.ContextLength);
        AssertEx.Null(metadata.Architecture);
    }

    [Test]
    public async Task ReadHeaderFromFile_EmptyFile_ReturnsEmpty()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("empty.gguf");
        await File.WriteAllBytesAsync(path, []);
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Null(metadata.ContextLength);
    }

    [Test]
    public async Task ReadHeaderFromFile_MissingFile_ReturnsEmpty_DoesNotThrow()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("never-downloaded.gguf");
        var reader = NewReader();

        var metadata = await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);

        AssertEx.Null(metadata.ContextLength);
        AssertEx.Null(metadata.Architecture);
    }

    private static GgufHeaderReader NewReader()
    {
        var options = new HuggingFaceOptions();
#pragma warning disable CA2000 // The returned reader retains this client; its throwing in-memory handler owns no sockets, and disposing it here would invalidate the reader.
        var http = new HttpClient(new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) =>
            throw new InvalidOperationException("A local header read must never touch the HTTP client.")));
#pragma warning restore CA2000
        return new GgufHeaderReader(http, options, NullLogger<GgufHeaderReader>.Instance);
    }
}
