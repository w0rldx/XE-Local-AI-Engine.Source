namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;

/// <summary>Writes throwaway GGUF model headers and control-vector files in the layout <c>common_control_vector_load_one</c> reads.</summary>
internal sealed class ControlVectorFiles : IDisposable
{
    private const uint GgmlF32 = 0;
    private const uint GgmlF16 = 1;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("xe-control-vector-");

    public static IGgufMetadataReader RealGgufReader()
    {
#pragma warning disable CA2000 // The reader retains this client; its throwing in-memory handler owns no sockets.
        var http = new HttpClient(new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) =>
            throw new InvalidOperationException("A local GGUF read must never touch the HTTP client.")));
#pragma warning restore CA2000
        return new GgufMetadataReader(new GgufHeaderReader(http, GgufStoreTestInfrastructure.NoTokenStore(), new HuggingFaceOptions(), NullLogger<GgufHeaderReader>.Instance, TimeProvider.System),
            NullLogger<GgufMetadataReader>.Instance);
    }

    public string Model(long embeddingLength, long blockCount, uint nextnPredictLayers = 0, bool nextnAsUint64 = false)
    {
        var builder = new GgufHeaderBytesBuilder()
                      .WithString("general.architecture", "qwen35")
                      .WithUint32("qwen35.block_count", (uint)blockCount)
                      .WithUint32("qwen35.embedding_length", (uint)embeddingLength);
        if (nextnPredictLayers > 0)
        {
            builder = nextnAsUint64
                ? builder.WithUint64("qwen35.nextn_predict_layers", nextnPredictLayers)
                : builder.WithUint32("qwen35.nextn_predict_layers", nextnPredictLayers);
        }

        return Write(builder.Build());
    }

    public string Vector(string tensorName, int elements, float fill = 0.5f) =>
        Write(Pad(new GgufHeaderBytesBuilder()
                  .WithString("general.architecture", "controlvector")
                  .WithTensor(tensorName, [(ulong)elements], GgmlF32, offset: 0)
                  .Build()),
            Data(elements, fill));

    /// <summary>One file per refusal the harness must make, for a model of <paramref name="embeddingLength" />.</summary>
    public string BadVector(string kind, int embeddingLength) =>
        kind switch
        {
            "wrong-size" => Vector("direction.15", 512),
            "other-model-size" => Vector("direction.15", 4096),
            "layer-equals-block-count" => Vector("direction.24", embeddingLength),
            "layer-beyond-model" => Vector("direction.99", embeddingLength),
            "layer-zero" => Vector("direction.0", embeddingLength),
            "garbage" => Write("not a gguf at all"u8.ToArray()),
            "missing" => Path.Combine(_directory.FullName, "missing.gguf"),
            "empty-path" => string.Empty,
            "truncated" => Write(Pad(new GgufHeaderBytesBuilder()
                                     .WithString("general.architecture", "controlvector")
                                     .WithTensor("direction.15", [(ulong)embeddingLength], GgmlF32, offset: 0)
                                     .Build()),
                Data(embeddingLength - 1, 0.5f)),
            "two-tensors" => Write(Pad(new GgufHeaderBytesBuilder()
                                       .WithString("general.architecture", "controlvector")
                                       .WithTensor("direction.14", [(ulong)embeddingLength], GgmlF32, offset: 0)
                                       .WithTensor("direction.15", [(ulong)embeddingLength], GgmlF32, offset: (ulong)embeddingLength * 4)
                                       .Build()),
                Data(embeddingLength * 2, 0.5f)),
            "not-f32" => Write(Pad(new GgufHeaderBytesBuilder()
                                   .WithString("general.architecture", "controlvector")
                                   .WithTensor("direction.15", [(ulong)embeddingLength], GgmlF16, offset: 0)
                                   .Build()),
                Data(embeddingLength, 0.5f)),
            "two-dimensions" => Write(Pad(new GgufHeaderBytesBuilder()
                                          .WithString("general.architecture", "controlvector")
                                          .WithTensor("direction.15", [(ulong)embeddingLength, 1], GgmlF32, offset: 0)
                                          .Build()),
                Data(embeddingLength, 0.5f)),
            "non-zero-offset" => Write(Pad(new GgufHeaderBytesBuilder()
                                           .WithString("general.architecture", "controlvector")
                                           .WithTensor("direction.15", [(ulong)embeddingLength], GgmlF32, offset: 32)
                                           .Build()),
                new byte[32],
                Data(embeddingLength, 0.5f)),
            "custom-alignment" => Write(Pad(new GgufHeaderBytesBuilder()
                                            .WithString("general.architecture", "controlvector")
                                            .WithUint32("general.alignment", 64)
                                            .WithTensor("direction.15", [(ulong)embeddingLength], GgmlF32, offset: 0)
                                            .Build()),
                new byte[64],
                Data(embeddingLength, 0.5f)),
            "wrong-name" => Vector("blk.15.attn_norm.weight", embeddingLength),
            "all-zero" => Vector("direction.15", embeddingLength, fill: 0f),
            "has-nan" => WithLastValue(Vector("direction.15", embeddingLength), float.NaN),
            "has-infinity" => WithLastValue(Vector("direction.15", embeddingLength), float.PositiveInfinity),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown bad-vector case.")
        };

    public void Dispose() => _directory.Delete(recursive: true);

    private static string WithLastValue(string path, float value)
    {
        var bytes = File.ReadAllBytes(path);
        BitConverter.TryWriteBytes(bytes.AsSpan(bytes.Length - sizeof(float)), value);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Write(params byte[][] parts)
    {
        var path = Path.Combine(_directory.FullName, Guid.NewGuid().ToString("N") + ".gguf");
        File.WriteAllBytes(path, [.. parts.SelectMany(static part => part)]);
        return path;
    }

    private static byte[] Pad(byte[] header) => [.. header, .. new byte[(32 - (header.Length % 32)) % 32]];

    private static byte[] Data(int elements, float fill)
    {
        var values = new float[elements];
        Array.Fill(values, fill);
        return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    }
}
