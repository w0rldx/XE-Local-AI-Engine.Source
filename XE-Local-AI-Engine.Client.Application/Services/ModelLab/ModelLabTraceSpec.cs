namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>The trace every job runs. Fixed here so no request can widen what a job measures or stores.</summary>
public static class ModelLabTraceSpec
{
    /// <summary>Separate from the trainer's: the two scripts change independently. Pinned by <c>modellablib.CONTRACT_VERSION</c>.</summary>
    public const int ContractVersion = 1;

    public const int MaxNewTokens = 128;

    public const int Seed = 1234;

    /// <summary>About four times what 128 tokens cost at three layers (measured: about 162 bytes per token per layer).</summary>
    public const int TraceByteCap = 256 * 1024;

    public const int MaxResultBytes = 1024 * 1024;

    public const int MaxPromptCharacters = 8000;

    /// <summary>Three layers spread across the stack. Quarter points, so block 0, which cannot be steered, is not one of them.</summary>
    public static IReadOnlyList<int> LayersFor(int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        return new[] { blockCount / 4, blockCount / 2, blockCount * 3 / 4 }.Distinct().ToArray();
    }
}
