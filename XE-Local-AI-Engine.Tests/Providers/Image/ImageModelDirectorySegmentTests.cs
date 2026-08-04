namespace XE_Local_AI_Engine.Tests.Providers.Image;

using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins that two distinct model names can never share a weights directory.
///     <para>
///         The readable half of the segment is a lossy sanitization — every character outside <c>[A-Za-z0-9._-]</c>
///         collapses to <c>_</c> — so <c>owner/model</c> and <c>owner_model</c> both reduce to <c>owner_model</c>. That
///         is not cosmetic: a shared directory means the second install overwrites the first's weights whenever their
///         file-sets share a leaf name (and they usually do — <c>vae.safetensors</c>, <c>model.safetensors</c>), and
///         deleting either model removes the file the other still points at.
///     </para>
/// </summary>
public sealed class ImageModelDirectorySegmentTests
{
    [Test]
    [Arguments("owner/model", "owner_model")]
    [Arguments("owner model", "owner_model")]
    [Arguments("owner:model", "owner/model")]
    [Arguments("a/b", "a-b")]
    public async Task SafeModelDirectorySegment_ForNamesThatSanitizeAlike_StillDiffers(string first, string second)
    {
        var firstSegment = HuggingFaceImageModelStore.SafeModelDirectorySegment(first);
        var secondSegment = HuggingFaceImageModelStore.SafeModelDirectorySegment(second);

        AssertEx.NotEqual(firstSegment, secondSegment);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    [Test]
    public async Task SafeModelDirectorySegment_IsStableForTheSameName()
    {
        AssertEx.Equal(HuggingFaceImageModelStore.SafeModelDirectorySegment("qwen-image"),
            HuggingFaceImageModelStore.SafeModelDirectorySegment("qwen-image"));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    [Test]
    [Arguments("owner/model")]
    [Arguments("../../etc/passwd")]
    [Arguments("a\\b")]
    [Arguments("///")]
    [Arguments("")]
    public async Task SafeModelDirectorySegment_IsAlwaysASingleSafeSegment(string modelName)
    {
        var segment = HuggingFaceImageModelStore.SafeModelDirectorySegment(modelName);

        AssertEx.True(segment.Length > 0, "The segment must never be empty — it names a real directory.");
        AssertEx.False(segment.Contains('/', StringComparison.Ordinal), "The segment must not contain a path separator.");
        AssertEx.False(segment.Contains('\\', StringComparison.Ordinal), "The segment must not contain a path separator.");
        AssertEx.False(segment.Contains("..", StringComparison.Ordinal), "The segment must not contain a traversal sequence.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // An unnamed model still needs a directory: the readable half degrades to a constant, and the hash is what keeps
    // two differently-unprintable names apart.
    [Test]
    public async Task SafeModelDirectorySegment_ForDistinctUnprintableNames_StillDiffers()
    {
        AssertEx.NotEqual(HuggingFaceImageModelStore.SafeModelDirectorySegment("///"),
            HuggingFaceImageModelStore.SafeModelDirectorySegment("???"));
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
