namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LlamaStartupFailureClassifierTests
{
    // Verbatim llama.cpp text. The KV-cache allocation failure is thrown from llama-kv-cache.cpp when the backend
    // refuses the cache buffer; the CUDA line is what ggml-cuda.cu prints when cudaMalloc reports out of memory.
    private const string KvCacheAllocationFailureLine =
        "0.28.114.002 E llama_kv_cache: failed to allocate buffer for kv cache";

    private const string CudaOutOfMemoryLine =
        "0.28.113.880 E ggml_backend_cuda_buffer_type_alloc_buffer: allocating 8192.00 MiB on device 0: cudaMalloc failed: out of memory";

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

    /// <summary>
    ///     Failing to allocate the KV cache is an allocation failure, not a compatibility problem — the line names the
    ///     cache as the buffer it could not get, not as a rejected type. Classifying it as a compatibility failure kept
    ///     the supervisor's context down-tier from firing on what a memory-tight box hits most often.
    /// </summary>
    [Test]
    public void Classify_KvCacheAllocationFailureAlone_IsOutOfMemory()
    {
        AssertEx.Equal(LlamaStartupFailureKind.OutOfMemory,
            LlamaStartupFailureClassifier.Classify([KvCacheAllocationFailureLine]));
    }

    /// <summary>
    ///     The shape a real KV-cache exhaustion produces: the backend reports the failed cudaMalloc, then llama.cpp
    ///     reports which buffer it was. Classifying the capture as one joined string let the second line's "kv cache"
    ///     wording outrank the first line's "out of memory".
    /// </summary>
    [Test]
    public void Classify_OutOfMemoryLineBesideKvCacheWording_IsOutOfMemory()
    {
        AssertEx.Equal(LlamaStartupFailureKind.OutOfMemory,
            LlamaStartupFailureClassifier.Classify([
                "0.28.100.417 I llama_context: n_ctx = 32768",
                CudaOutOfMemoryLine,
                KvCacheAllocationFailureLine,
                "0.28.114.100 E srv    load_model: failed to load model"
            ]));
    }

    /// <summary>Line order must not change the verdict: the reduction is over lines, not over a prefix of them.</summary>
    [Test]
    public void Classify_KvCacheWordingBeforeTheOutOfMemoryLine_IsStillOutOfMemory()
    {
        AssertEx.Equal(LlamaStartupFailureKind.OutOfMemory,
            LlamaStartupFailureClassifier.Classify([KvCacheAllocationFailureLine, CudaOutOfMemoryLine]));
    }

    /// <summary>
    ///     The genuine incompatibility case, verbatim from llama-context.cpp: a quantized V cache is rejected outright
    ///     when flash attention is off. Nothing here failed to allocate, and the safe (KV/flash-attention off) retry is
    ///     the right response rather than a smaller context.
    /// </summary>
    [Test]
    public void Classify_VCacheQuantizationRequiringFlashAttention_IsIncompatible()
    {
        AssertEx.Equal(LlamaStartupFailureKind.KvOrFlashAttentionIncompatible,
            LlamaStartupFailureClassifier.Classify([
                "0.02.310.884 I llama_context: n_ctx_per_seq = 32768",
                "0.02.311.002 E llama_context: V cache quantization requires flash_attn",
                "0.02.311.140 E srv    load_model: failed to load model"
            ]));
    }

    /// <summary>A rejected cache type is a compatibility verdict too, even though llama.cpp words it as a block-size mismatch.</summary>
    [Test]
    public void Classify_RejectedCacheType_IsIncompatible()
    {
        AssertEx.Equal(LlamaStartupFailureKind.KvOrFlashAttentionIncompatible,
            LlamaStartupFailureClassifier.Classify([
                "0.02.311.002 E llama_context: K cache type q4_0 with block size 32 does not divide n_embd_head_k=80"
            ]));
    }
}
