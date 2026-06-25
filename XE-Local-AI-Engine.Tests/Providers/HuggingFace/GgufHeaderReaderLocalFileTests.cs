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
#pragma warning disable CA2000
        var http = new HttpClient(new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) =>
            throw new InvalidOperationException("A local header read must never touch the HTTP client.")));
#pragma warning restore CA2000
        return new GgufHeaderReader(http, options, NullLogger<GgufHeaderReader>.Instance);
    }
}
