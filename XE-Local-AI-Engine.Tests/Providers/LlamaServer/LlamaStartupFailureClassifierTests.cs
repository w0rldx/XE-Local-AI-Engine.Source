namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LlamaStartupFailureClassifierTests
{
    [Test]
    public void Classify_OutOfMemory_DistinguishesItFromCompatibility()
    {
        AssertEx.Equal(LlamaStartupFailureKind.OutOfMemory,
            LlamaStartupFailureClassifier.Classify(["ggml_cuda: failed to allocate: out of memory"]));
    }

    [Test]
    public void Classify_KvOrFlashAttentionFailure_WinsOverGenericAllocationText()
    {
        AssertEx.Equal(LlamaStartupFailureKind.KvOrFlashAttentionIncompatible,
            LlamaStartupFailureClassifier.Classify(["flash attention KV cache type is unsupported; failed to allocate"]));
    }

    [Test]
    public void Classify_UnrelatedStartupFailure_IsOther()
    {
        AssertEx.Equal(LlamaStartupFailureKind.Other,
            LlamaStartupFailureClassifier.Classify(["model architecture is unsupported"]));
    }
}
