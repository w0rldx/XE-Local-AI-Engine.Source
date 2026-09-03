namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="AttentionArchTag" /> decides the tag from GGUF numbers only — never from an architecture string — so
///     these cases are stated as geometry, one per row of the tag table plus the two ways a head count can be unknown.
/// </summary>
public sealed class AttentionArchTagTests
{
    [Test]
    public void Resolve_BothMlaLengthsPositive_IsMla()
    {
        // MLA wins over every other row: a deepseek2 file's ordinary head counts still look like GQA, and its sliding
        // window (if any) would otherwise claim it first.
        var shape = new GgufAttentionShape(KeyLength: 192, ValueLength: 128, SlidingWindow: 4096, SlidingWindowPattern: 4, KeyLengthMla: 576, ValueLengthMla: 512);

        AssertEx.Equal(AttentionArchTag.Mla, AttentionArchTag.Resolve(shape, headCount: 128, headCountKv: 128));
    }

    [Test]
    public void Resolve_OnlyOneMlaLengthPresent_IsNotMla()
    {
        // llama.cpp's is_mla() requires BOTH lengths; one alone is not an MLA cache and must not claim the tag.
        var shape = new GgufAttentionShape(KeyLengthMla: 576);

        AssertEx.Equal(AttentionArchTag.Mha, AttentionArchTag.Resolve(shape, headCount: 32, headCountKv: 32));
    }

    [Test]
    public void Resolve_SlidingWindowWithAPattern_IsSwa()
    {
        var shape = new GgufAttentionShape(KeyLength: 256, ValueLength: 256, SlidingWindow: 1024, SlidingWindowPattern: 6);

        AssertEx.Equal(AttentionArchTag.Swa, AttentionArchTag.Resolve(shape, headCount: 8, headCountKv: 4));
    }

    [Test]
    public void Resolve_SlidingWindowWithoutAPattern_FallsThroughToTheHeadCounts()
    {
        // A window with no global-attention stride does not describe interleaved SWA, so the head counts decide.
        var shape = new GgufAttentionShape(SlidingWindow: 1024);

        AssertEx.Equal(AttentionArchTag.Gqa, AttentionArchTag.Resolve(shape, headCount: 32, headCountKv: 8));
    }

    [Test]
    public void Resolve_FewerKvHeadsThanQueryHeads_IsGqa()
    {
        AssertEx.Equal(AttentionArchTag.Gqa, AttentionArchTag.Resolve(new GgufAttentionShape(), headCount: 32, headCountKv: 8));
    }

    [Test]
    public void Resolve_EqualHeadCounts_IsMha()
    {
        AssertEx.Equal(AttentionArchTag.Mha, AttentionArchTag.Resolve(new GgufAttentionShape(), headCount: 32, headCountKv: 32));
    }

    [Test]
    [Arguments(null, 8L)]
    [Arguments(32L, null)]
    [Arguments(null, null)]
    [Arguments(0L, 0L)]
    public void Resolve_WithAnUnknownHeadCount_IsMha(long? headCount, long? headCountKv)
    {
        // "Unknown" must not be guessed into GQA — the conservative answer is the plain shape.
        AssertEx.Equal(AttentionArchTag.Mha, AttentionArchTag.Resolve(new GgufAttentionShape(), headCount, headCountKv));
    }

    [Test]
    public void Resolve_WithNoShapeAtAll_IsDecidedByTheHeadCounts()
    {
        AssertEx.Equal(AttentionArchTag.Gqa, AttentionArchTag.Resolve(shape: null, headCount: 64, headCountKv: 8));
        AssertEx.Equal(AttentionArchTag.Mha, AttentionArchTag.Resolve(shape: null, headCount: null, headCountKv: null));
    }
}
