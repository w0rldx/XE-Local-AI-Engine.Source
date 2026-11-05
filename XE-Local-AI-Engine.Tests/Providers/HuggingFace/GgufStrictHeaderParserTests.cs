namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Buffers.Binary;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="GgufStrictHeaderParser" /> over hand-built GGUF bytes: what the import path keeps of a key-value section,
///     and how <see cref="GgufImportInspector.Classify" /> reads a header the parser could not finish.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GgufStrictHeaderParserTests
{
    private const uint TypeFloat32 = 6;
    private const uint TypeString = 8;
    private const uint TypeFloat64 = 12;
    private const uint TypeUnknown = 13;

    [Test]
    public async Task Read_TruncatedKeyValue_IsIncompleteKeepingTheKeysBeforeIt_AndClassifiesInvalid()
    {
        var bytes = new GgufHeaderBytesBuilder()
                    .WithString("general.architecture", "qwen35")
                    .WithUint32("qwen35.block_count", value: 32)
                    .Build();

        var header = await ReadAsync(bytes[..^3]);

        AssertEx.False(header.IsComplete);
        AssertEx.Equal(expected: 3u, header.Version!.Value);
        AssertEx.Equal("qwen35", header.GetString("general.architecture")!);
        AssertEx.False(header.Values.ContainsKey("qwen35.block_count"));
        AssertEx.Contains(Classify(header).Rejections, GgufImportRejectionCode.InvalidGguf);
    }

    [Test]
    [Arguments(1u)]
    [Arguments(4u)]
    public async Task Classify_VersionOutsideTwoToThree_RejectsAsUnsupported(uint version)
    {
        var header = await ReadAsync(WithVersion(new GgufHeaderBytesBuilder().Build(), version));

        AssertEx.True(header.IsComplete);
        AssertEx.Equal(version, header.Version!.Value);
        AssertEx.Contains(Classify(header).Rejections, GgufImportRejectionCode.UnsupportedVersion);
    }

    [Test]
    [Arguments(2u)]
    [Arguments(3u)]
    public async Task Classify_SupportedVersion_IsNotRejectedForItsVersion(uint version)
    {
        var header = await ReadAsync(WithVersion(new GgufHeaderBytesBuilder().Build(), version));

        AssertEx.False(Classify(header).Rejections.Contains(GgufImportRejectionCode.UnsupportedVersion));
    }

    [Test]
    public async Task Read_BelowTheFixedHeaderSize_IsInvalid()
    {
        var header = await ReadAsync(new GgufHeaderBytesBuilder().Build()[..23]);

        AssertEx.True(ReferenceEquals(GgufStrictHeaderParser.StrictHeader.Invalid, header));
        AssertEx.Contains(Classify(header).Rejections, GgufImportRejectionCode.InvalidGguf);
    }

    [Test]
    public async Task Read_WrongMagic_IsInvalid()
    {
        var header = await ReadAsync(new byte[24]);

        AssertEx.True(ReferenceEquals(GgufStrictHeaderParser.StrictHeader.Invalid, header));
    }

    [Test]
    [Arguments(0u, "C8", 200L)]
    [Arguments(1u, "C8", 200L)]
    [Arguments(7u, "01", 1L)]
    [Arguments(2u, "FEFF", 65_534L)]
    [Arguments(3u, "FEFF", -2L)]
    [Arguments(4u, "FEFFFFFF", 4_294_967_294L)]
    [Arguments(5u, "FEFFFFFF", -2L)]
    [Arguments(10u, "FEFFFFFFFFFFFF7F", long.MaxValue - 1)]
    [Arguments(10u, "FFFFFFFFFFFFFF7F", long.MaxValue)]
    [Arguments(10u, "0000000000000080", long.MaxValue)]
    [Arguments(10u, "FFFFFFFFFFFFFFFF", long.MaxValue)]
    [Arguments(11u, "FEFFFFFFFFFFFFFF", -2L)]
    public async Task Read_IntegerValue_IsKeptAsLong_ClampingUint64AtLongMax(uint type, string valueHex, long expected)
    {
        var header = await ReadAsync(new GgufHeaderBytesBuilder().WithRawValue("key", type, Convert.FromHexString(valueHex)).Build());

        AssertEx.True(header.IsComplete);
        AssertEx.True(header.TryGetInt64("key", out var value));
        AssertEx.Equal(expected, value);
    }

    [Test]
    public async Task Read_FloatValues_AreConsumedAndNotKept()
    {
        var bytes = new GgufHeaderBytesBuilder()
                    .WithRawValue("float32", TypeFloat32, new byte[4])
                    .WithRawValue("float64", TypeFloat64, new byte[8])
                    .WithUint32("after", value: 7)
                    .Build();

        var header = await ReadAsync(bytes);

        AssertEx.True(header.IsComplete);
        AssertEx.False(header.Values.ContainsKey("float32"));
        AssertEx.False(header.Values.ContainsKey("float64"));
        AssertEx.True(header.TryGetInt64("after", out var after));
        AssertEx.Equal(expected: 7L, after);
    }

    [Test]
    public async Task Read_IntegerThenFloatOnOneKey_KeepsTheInteger()
    {
        var bytes = new GgufHeaderBytesBuilder()
                    .WithUint32("key", value: 7)
                    .WithRawValue("key", TypeFloat32, new byte[4])
                    .Build();

        var header = await ReadAsync(bytes);

        AssertEx.True(header.IsComplete);
        AssertEx.True(header.TryGetInt64("key", out var value));
        AssertEx.Equal(expected: 7L, value);
    }

    [Test]
    public async Task Read_StringArray_IsConsumedAndNotKept()
    {
        var bytes = new GgufHeaderBytesBuilder()
                    .WithStringArray("tokens", ["a", "b"])
                    .WithString("after", "kept")
                    .Build();

        var header = await ReadAsync(bytes);

        AssertEx.True(header.IsComplete);
        AssertEx.False(header.Values.ContainsKey("tokens"));
        AssertEx.Equal("kept", header.GetString("after")!);
    }

    [Test]
    public async Task Read_UnknownValueType_IsIncompleteKeepingTheKeysBeforeIt()
    {
        var bytes = new GgufHeaderBytesBuilder()
                    .WithString("general.architecture", "qwen35")
                    .WithRawValue("unknown", TypeUnknown, [])
                    .WithUint32("after", value: 7)
                    .Build();

        var header = await ReadAsync(bytes);

        AssertEx.False(header.IsComplete);
        AssertEx.Equal("qwen35", header.GetString("general.architecture")!);
        AssertEx.False(header.Values.ContainsKey("after"));
        AssertEx.Contains(Classify(header).Rejections, GgufImportRejectionCode.InvalidGguf);
    }

    [Test]
    public async Task Read_NestedArray_IsIncompleteKeepingTheKeysBeforeIt_AndClassifiesInvalid()
    {
        var bytes = new GgufHeaderBytesBuilder()
                    .WithString("general.architecture", "qwen35")
                    .WithNestedArray("nested")
                    .WithUint32("after", value: 7)
                    .Build();

        var header = await ReadAsync(bytes);

        AssertEx.False(header.IsComplete);
        AssertEx.Equal("qwen35", header.GetString("general.architecture")!);
        AssertEx.False(header.Values.ContainsKey("after"));
        AssertEx.Contains(Classify(header).Rejections, GgufImportRejectionCode.InvalidGguf);
    }

    [Test]
    public async Task Read_StringLengthIntMaxValue_IsIncompleteKeepingTheKeysBeforeIt()
    {
        var bytes = new GgufHeaderBytesBuilder()
                    .WithString("general.architecture", "qwen35")
                    .WithRawValue("text", TypeString, Convert.FromHexString("FFFFFF7F00000000"))
                    .Build();

        var header = await ReadAsync(bytes);

        AssertEx.False(header.IsComplete);
        AssertEx.Equal("qwen35", header.GetString("general.architecture")!);
    }

    private static byte[] WithVersion(byte[] bytes, uint version)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start: 4), version);
        return bytes;
    }

    private static GgufImportInspection Classify(GgufStrictHeaderParser.StrictHeader header)
    {
        return GgufImportInspector.Classify("model.gguf", size: 1024, header);
    }

    private static async Task<GgufStrictHeaderParser.StrictHeader> ReadAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await GgufStrictHeaderParser.ReadAsync(stream, CancellationToken.None);
    }
}
