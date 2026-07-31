namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     F-011's second instance, locally: the registry gated only on "does the file name parse to a quant", so a
///     downloaded speculative-decoding drafter was registered as an ordinary model and listed beside the real weights
///     it drafts for — a 0.4 GB "Q8_0 gemma-4" next to the 7.8 GB one. A rescan must give the drafter the marked quant
///     identity and the <see cref="GgufRole.Draft" /> role, without disturbing any base quant.
/// </summary>
public sealed class GgufRegistryDraftModelTests
{
    [Test]
    public async Task Rescan_RegistersAnMtpDrafterAsADraft_NotAsAnOrdinaryModel()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);

        // Both files as they land on disk: the real weights and the drafter downloaded from the same repo.
        await File.WriteAllTextAsync(dir.FilePath("gemma-4-12b-it-UD-Q4_K_XL.gguf"), "fake-gguf");
        await File.WriteAllTextAsync(dir.FilePath("mtp-gemma-4-12b-it-Q8_0.gguf"), "fake-gguf");

        using var registry = Infra.Registry(options);
        var listed = await registry.ListAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, listed.Count);

        var draft = listed.Single(entry => entry.FileName == "mtp-gemma-4-12b-it-Q8_0.gguf");
        AssertEx.Equal("MTP-Q8_0", draft.Quant);
        AssertEx.Equal(GgufRole.Draft, draft.Role);
        AssertEx.True(GgufDraftModel.IsDraftModelName(draft.ModelName), "The drafter's registry key must read as a draft.");

        var real = listed.Single(entry => entry.FileName == "gemma-4-12b-it-UD-Q4_K_XL.gguf");
        AssertEx.Equal("UD-Q4_K_XL", real.Quant);
        AssertEx.Equal(GgufRole.Unknown, real.Role);
        AssertEx.False(GgufDraftModel.IsDraftModelName(real.ModelName), "A base quant must never read as a draft.");
    }

    [Test]
    public async Task Rescan_LeavesAnMtpNamedBaseModelAlone()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);

        // From unsloth/Qwen3.6-27B-MTP-GGUF — a real chat model whose name advertises MTP layers.
        await File.WriteAllTextAsync(dir.FilePath("Qwen3.6-27B-MTP-Q6_K.gguf"), "fake-gguf");

        using var registry = Infra.Registry(options);
        var listed = await registry.ListAsync(CancellationToken.None);

        var entry = listed.Single();
        AssertEx.Equal("Q6_K", entry.Quant);
        AssertEx.Equal(GgufRole.Unknown, entry.Role);
    }
}
