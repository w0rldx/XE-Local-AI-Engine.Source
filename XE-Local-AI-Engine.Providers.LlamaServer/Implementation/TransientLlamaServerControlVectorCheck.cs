namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Refuses a control vector that does not match its GGUF, because <c>llama-server</c> itself would not.</summary>
/// <remarks>
///     A vector of the wrong size or layer, or a missing or unreadable file, leaves the server starting, reporting ready and serving
///     unsteered output, with at most one log line and sometimes none (measured on build 12014). A "steered" arm would then be a
///     second baseline, so the file's tensor, layer, size, layout and values are checked here, before a process exists.
/// </remarks>
internal static class TransientLlamaServerControlVectorCheck
{
    private const string TensorPrefix = "direction.";
    private const uint GgmlTypeF32 = 0;
    private const int GgufDataAlignment = 32;
    private const double SmallestNormalFloat = 1.17549435E-38;

    /// <summary>The checked vector, or <see langword="null" /> for a request without one.</summary>
    /// <exception cref="LlamaRuntimeException">The runtime or the model cannot apply this vector. The message is operator-facing.</exception>
    public static async Task<TransientLlamaServerControlVector?> CheckOrNullAsync(TransientLlamaServerEvaluationRequest request,
        LlamaServerCapabilityManifest manifest,
        IGgufMetadataReader reader,
        CancellationToken ct)
    {
        if (request.ControlVectorPath is null)
        {
            return null;
        }

        if (!manifest.SupportsOption(LlamaServerManagedFlags.ControlVectorScaled))
        {
            throw new LlamaRuntimeException("The selected llama.cpp runtime does not support control vectors.");
        }

        // The check would resolve a relative path against this host's directory, and the server against the binary's.
        if (!Path.IsPathFullyQualified(request.ControlVectorPath))
        {
            throw new LlamaRuntimeException("The control vector path must be absolute.");
        }

        // llama-server splits this argument on both characters.
        if (request.ControlVectorPath.AsSpan().IndexOfAny(':', ',') >= 0)
        {
            throw new LlamaRuntimeException("The control vector path cannot contain ':' or ','.");
        }

        // llama-server parses the scale as a float and rejects what does not fit; zero would apply nothing and look like a steered arm.
        var magnitude = Math.Abs(request.ControlVectorScale);
        if (!(magnitude is >= SmallestNormalFloat and <= float.MaxValue))
        {
            throw new LlamaRuntimeException("The control vector scale must be a non-zero number within float range.");
        }

        var vector = await reader.ReadTensorsAsync(request.ControlVectorPath, ct).ConfigureAwait(false);
        if (vector.Failure != GgufTensorReadFailure.None)
        {
            throw new LlamaRuntimeException("The control vector file could not be read as a GGUF file.");
        }

        var tensor = SingleDirectionTensorOrThrow(vector);
        var layer = int.Parse(tensor.Name.AsSpan(TensorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture);
        var dataStart = (vector.HeaderBytes + GgufDataAlignment - 1) / GgufDataAlignment * GgufDataAlignment;
        if (vector.FileSizeBytes < dataStart + tensor.ByteSize)
        {
            throw new LlamaRuntimeException("The control vector file is truncated.");
        }

        var model = await reader.ReadTensorsAsync(request.ModelFilePath, ct).ConfigureAwait(false);
        if (model.Failure != GgufTensorReadFailure.None || model.BlockCount is not { } blockCount || model.EmbeddingLength is not { } embeddingLength)
        {
            throw new LlamaRuntimeException("The model's header could not be read, so the control vector cannot be checked against it.");
        }

        // llama.cpp sets up vector tensors only for the trunk: an MTP block counts in block_count, but a vector there is dropped with no error.
        var steerableBlocks = blockCount - (model.NextnPredictLayers ?? 0);
        if (layer < 1 || layer >= steerableBlocks)
        {
            throw new LlamaRuntimeException(string.Create(CultureInfo.InvariantCulture,
                $"The control vector targets layer {layer}, and this model can be steered only at layers 1 to {steerableBlocks - 1}."));
        }

        if (tensor.ElementCount != embeddingLength)
        {
            throw new LlamaRuntimeException(string.Create(CultureInfo.InvariantCulture,
                $"The control vector has {tensor.ElementCount} elements, and this model's embedding size is {embeddingLength}."));
        }

        await EnsureValuesCanSteerAsync(request.ControlVectorPath, dataStart, tensor.ElementCount * sizeof(float), ct).ConfigureAwait(false);

        var file = await FileSha256.ComputeWithLengthAsync(request.ControlVectorPath, ct).ConfigureAwait(false);
        return new TransientLlamaServerControlVector
        {
            Layer = layer,
            ElementCount = tensor.ElementCount,
            SizeBytes = file.SizeBytes,
            Sha256 = file.Sha256,
            Scale = request.ControlVectorScale
        };
    }

    /// <summary>Whether the file still holds what was checked; a vector the server may have read differently must not pass as the checked one.</summary>
    public static async Task<bool> IsUnchangedAsync(string path, TransientLlamaServerControlVector checkedVector, CancellationToken ct)
    {
        try
        {
            var file = await FileSha256.ComputeWithLengthAsync(path, ct).ConfigureAwait(false);
            return string.Equals(file.Sha256, checkedVector.Sha256, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public static string FormatArgument(string path, double scale) =>
        string.Create(CultureInfo.InvariantCulture, $"{path}:{scale:R}");

    /// <summary>All-zero data applies nothing, and a NaN turns every activation it touches into NaN, while the server still reports ready.</summary>
    private static async Task EnsureValuesCanSteerAsync(string path, long dataStart, long byteSize, CancellationToken ct)
    {
        var bytes = new byte[byteSize];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true))
        {
            stream.Seek(dataStart, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        }

        var values = MemoryMarshal.Cast<byte, float>(bytes);
        var allZero = true;
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
            {
                throw new LlamaRuntimeException("The control vector holds values that are not finite.");
            }

            allZero &= value == 0f;
        }

        if (allZero)
        {
            throw new LlamaRuntimeException("The control vector is all zero, so it would change nothing.");
        }
    }

    private static GgufTensorInfo SingleDirectionTensorOrThrow(GgufTensorInspection vector)
    {
        if (vector.Tensors is [{ Dimensions.Count: 1, GgmlType: GgmlTypeF32, Offset: 0 } tensor]
            && vector.Alignment is null or GgufDataAlignment
            && tensor.Name.StartsWith(TensorPrefix, StringComparison.Ordinal)
            && int.TryParse(tensor.Name.AsSpan(TensorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return tensor;
        }

        throw new LlamaRuntimeException("The control vector file must hold exactly one 1-D F32 tensor named direction.N, at offset 0 with the default alignment.");
    }
}
