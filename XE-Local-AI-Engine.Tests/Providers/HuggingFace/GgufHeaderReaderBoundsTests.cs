namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What <see cref="GgufHeaderReader" /> does with a header it cannot finish: the keys before the fault survive, nothing
///     throws, and a remote read stops growing at its hard cap. A local file read and a remote range read share the parse.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GgufHeaderReaderBoundsTests
{
    private const uint TypeString = 8;
    private const uint TypeArray = 9;
    private const uint TypeUnknown = 13;
    private const long MiB = 1024 * 1024;
    private const string StringLengthIntMaxValueHex = "FFFFFF7F00000000";

    [Test]
    public async Task ReadHeaderFromFile_UnknownValueType_KeepsTheKeysBeforeIt()
    {
        var bytes = EarlierKeys()
                    .WithRawValue("unknown", TypeUnknown, [])
                    .WithUint32("qwen35.block_count", value: 32)
                    .Build();

        var metadata = await ReadFileAsync(bytes);

        AssertEarlierKeysKept(metadata);
        AssertEx.Null(metadata.BlockCount);
    }

    [Test]
    public async Task ReadHeaderFromFile_ArrayLengthLargerThanTheFile_KeepsTheKeysBeforeIt()
    {
        // UINT8 elements, a length of ulong.MaxValue, and no element bytes.
        var array = Convert.FromHexString("00000000" + "FFFFFFFFFFFFFFFF");
        var bytes = EarlierKeys().WithRawValue("tokens", TypeArray, array).Build();

        var metadata = await ReadFileAsync(bytes);

        AssertEarlierKeysKept(metadata);
    }

    [Test]
    public async Task ReadHeader_NeverFinishingParse_DoublesTheRangeAndStopsAtTheHardCap()
    {
        var header = EarlierKeys().WithRawValue("unknown", TypeUnknown, []).Build();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            // A full range each time, so only the cap can stop the loop.
            var body = new byte[request.Headers.Range!.Ranges.Single().To!.Value + 1];
            header.CopyTo(body, index: 0);
            return new HttpResponseMessage
            {
                Content = new ByteArrayContent(body)
            };
        });

        var metadata = await ReadRemoteAsync(handler, probeBytes: 20 * MiB);

        AssertEarlierKeysKept(metadata);
        AssertEx.Equal($"bytes=0-{(20 * MiB) - 1},bytes=0-{(40 * MiB) - 1},bytes=0-{(64 * MiB) - 1}",
            string.Join(separator: ',', handler.Requests.Select(static request => request.Range)));
    }

    /// <summary>A server that ignores Range answers 200 with the whole file, which can be many gigabytes: the read takes what it asked for and stops.</summary>
    [Test]
    public async Task ReadHeader_ServerIgnoringTheRange_ReadsNoMoreThanItAskedFor()
    {
        var body = new byte[3 * MiB];
        EarlierKeys().Build().CopyTo(body, index: 0);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage
        {
            Content = new StreamContent(new CappedBodyStream(body, MiB))
        });

        var metadata = await ReadRemoteAsync(handler, probeBytes: MiB);

        AssertEarlierKeysKept(metadata);
        AssertEx.Equal(expected: 1, handler.CallCount);
    }

    /// <summary>A connection dropped inside the body costs this file's metadata, and must not escape to fail the whole discovery.</summary>
    [Test]
    public async Task ReadHeader_ServerDroppingTheBody_ReturnsEmptyMetadata()
    {
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage
        {
            Content = new StreamContent(new DroppedBodyStream())
        });

        var metadata = await ReadRemoteAsync(handler, probeBytes: MiB);

        AssertEx.Equal(GgufHeaderMetadata.Empty, metadata);
    }

    [Test]
    public async Task ReadHeaderFromFile_NestedArray_StopsThere_KeepingTheKeysBeforeIt()
    {
        var bytes = EarlierKeys()
                    .WithNestedArray("nested")
                    .WithUint32("qwen35.block_count", value: 32)
                    .Build();

        var metadata = await ReadFileAsync(bytes);

        AssertEarlierKeysKept(metadata);
        AssertEx.Null(metadata.BlockCount);
    }

    [Test]
    public async Task ReadHeader_NestedArray_StopsThere_KeepingTheKeysBeforeIt()
    {
        var bytes = EarlierKeys()
                    .WithNestedArray("nested")
                    .WithUint32("qwen35.block_count", value: 32)
                    .Build();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage
        {
            Content = new ByteArrayContent(bytes)
        });

        var metadata = await ReadRemoteAsync(handler, probeBytes: MiB);

        AssertEarlierKeysKept(metadata);
        AssertEx.Null(metadata.BlockCount);
    }

    [Test]
    public async Task ReadHeader_NestedArrayAfterTheArchitectureBlock_ReturnsPartial()
    {
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen35")
                     .WithUint32("qwen35.block_count", value: 32)
                     .WithUint32("qwen35.embedding_length", value: 2560)
                     .WithUint32("qwen35.attention.head_count", value: 16)
                     .WithUint32("qwen35.attention.head_count_kv", value: 4)
                     .WithString("tokenizer.ggml.model", "gpt2")
                     .WithNestedArray("nested")
                     .Build();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
        {
            // A full first range, so the parse itself must report that it stopped short.
            var body = new byte[MiB];
            header.CopyTo(body, index: 0);
            return new HttpResponseMessage
            {
                Content = new ByteArrayContent(body)
            };
        });

        var metadata = await ReadRemoteAsync(handler, probeBytes: MiB);

        AssertEx.True(metadata.IsPartial);
        AssertEx.Equal(expected: 32L, metadata.BlockCount!.Value);
        AssertEx.Equal(expected: 1, handler.CallCount);
    }

    [Test]
    public async Task ReadHeaderFromFile_StringLengthIntMaxValue_KeepsTheKeysBeforeIt()
    {
        var bytes = EarlierKeys().WithRawValue("text", TypeString, Convert.FromHexString(StringLengthIntMaxValueHex)).Build();

        var metadata = await ReadFileAsync(bytes);

        AssertEarlierKeysKept(metadata);
    }

    [Test]
    public async Task ReadHeader_StringLengthIntMaxValue_KeepsTheKeysBeforeIt()
    {
        var bytes = EarlierKeys().WithRawValue("text", TypeString, Convert.FromHexString(StringLengthIntMaxValueHex)).Build();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage
        {
            Content = new ByteArrayContent(bytes)
        });

        var metadata = await ReadRemoteAsync(handler, probeBytes: MiB);

        AssertEarlierKeysKept(metadata);
    }

    [Test]
    [Arguments(0u, "C8", 200L)]
    [Arguments(1u, "C8", 200L)]
    [Arguments(7u, "01", 1L)]
    [Arguments(2u, "FEFF", 65_534L)]
    [Arguments(3u, "FEFF", -2L)]
    [Arguments(4u, "FEFFFFFF", 4_294_967_294L)]
    [Arguments(5u, "FEFFFFFF", -2L)]
    [Arguments(6u, "00108044", 1024L)]
    [Arguments(10u, "FFFFFFFFFFFFFFFF", long.MaxValue)]
    [Arguments(11u, "FEFFFFFFFFFFFFFF", -2L)]
    [Arguments(12u, "0000000000029040", 1024L)]
    public async Task EveryScalarType_ReadsTheSameThroughTheHeaderAndTensorReaders(uint type, string valueHex, long expected)
    {
        var bytes = new GgufHeaderBytesBuilder()
                    .WithString("general.architecture", "qwen35")
                    .WithRawValue("qwen35.context_length", type, Convert.FromHexString(valueHex))
                    .Build();

        var header = await ReadFileAsync(bytes);
        await using var stream = new MemoryStream(bytes, writable: false);
        var tensors = await GgufTensorBlockReader.ReadAsync(stream, CancellationToken.None);

        AssertEx.Equal(GgufTensorReadFailure.None, tensors.Failure);
        AssertEx.Equal("qwen35", header.Architecture!);
        AssertEx.Equal("qwen35", tensors.Architecture!);
        AssertEx.Equal(expected, header.ContextLength!.Value);
        AssertEx.Equal(expected, tensors.ContextLength!.Value);
    }

    private static GgufHeaderBytesBuilder EarlierKeys()
    {
        return new GgufHeaderBytesBuilder()
               .WithString("general.architecture", "qwen35")
               .WithUint32("qwen35.context_length", value: 32_768);
    }

    private static void AssertEarlierKeysKept(GgufHeaderMetadata metadata)
    {
        AssertEx.Equal("qwen35", metadata.Architecture!);
        AssertEx.Equal(expected: 32_768L, metadata.ContextLength!.Value);
    }

    private static async Task<GgufHeaderMetadata> ReadFileAsync(byte[] bytes)
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("model.gguf");
        await File.WriteAllBytesAsync(path, bytes);
        var reader = GgufStoreTestInfrastructure.HeaderReader(new HuggingFaceOptions());
        return await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);
    }

    private static async Task<GgufHeaderMetadata> ReadRemoteAsync(HttpMessageHandler handler, long probeBytes)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        var options = new HuggingFaceOptions
        {
            HeaderProbeBytes = probeBytes
        };
        var reader = new GgufHeaderReader(http, GgufStoreTestInfrastructure.NoTokenStore(), options, NullLogger<GgufHeaderReader>.Instance, TimeProvider.System);
        return await reader.ReadHeaderAsync("org/repo", "model.gguf", "main", CancellationToken.None);
    }

    /// <summary>Fails the read instead of answering it once a read would go past the cap.</summary>
    private sealed class DroppedBodyStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("The response ended prematurely.");
    }

    private sealed class CappedBodyStream : MemoryStream
    {
        private readonly long _cap;

        public CappedBodyStream(byte[] body, long cap)
            : base(body, writable: false)
        {
            _cap = cap;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position + buffer.Length > _cap
                ? throw new InvalidOperationException($"The body was read past the {_cap} bytes that were asked for.")
                : base.ReadAsync(buffer, cancellationToken);
    }
}
