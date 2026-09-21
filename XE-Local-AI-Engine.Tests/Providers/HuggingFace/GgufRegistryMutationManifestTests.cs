namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     The alias-set mutation paths on an unreadable manifest: the mutation must FAIL and leave the file byte-identical.
/// </summary>
/// <remarks>
///     The read path self-heals a corrupt manifest by rescanning the models directory, which is safe because it only
///     serves a view. The mutation paths write the loaded entries straight back, so the same recovery there would
///     persist "the rows this build could parse" and silently delete every row it could not — the whole registry for a
///     manifest a newer build wrote. Deserialization failure is therefore surfaced, not recovered.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class GgufRegistryMutationManifestTests
{
    private const string TruncatedObject = "{ \"Models\": [ { \"ModelName\": ";
    private const string MissingRequiredMember = """{ "Models": [ { "RepoId": "bartowski/Demo-Model-GGUF", "FileName": "Demo-Model-Q4_K_M.gguf" } ] }""";

    [Test]
    [Arguments("")]
    [Arguments(TruncatedObject)]
    [Arguments(MissingRequiredMember)]
    public async Task RemoveAliasSet_OnAnUnreadableManifest_FailsAndLeavesItUntouched(string manifestContent)
    {
        using var dir = new Infra.TempModelsDir();
        using var registry = Infra.Registry(Infra.Options(dir.Path));
        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");
        var manifestPath = Path.Combine(dir.Path, "index.json");
        await File.WriteAllTextAsync(manifestPath, manifestContent);

        _ = await AssertEx.ThrowsAsync<IOException>(() =>
            registry.RemoveAliasSetIfMatchAsync([Entry(filePath)], CancellationToken.None));

        AssertEx.Equal(manifestContent, await File.ReadAllTextAsync(manifestPath));
    }

    [Test]
    [Arguments("")]
    [Arguments(TruncatedObject)]
    [Arguments(MissingRequiredMember)]
    public async Task RestoreAliasSet_OnAnUnreadableManifest_FailsAndLeavesItUntouched(string manifestContent)
    {
        using var dir = new Infra.TempModelsDir();
        using var registry = Infra.Registry(Infra.Options(dir.Path));
        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");
        var manifestPath = Path.Combine(dir.Path, "index.json");
        await File.WriteAllTextAsync(manifestPath, manifestContent);

        _ = await AssertEx.ThrowsAsync<IOException>(() =>
            registry.RestoreAliasSetIfMatchAsync([Entry(filePath)], CancellationToken.None));

        AssertEx.Equal(manifestContent, await File.ReadAllTextAsync(manifestPath));
    }

    [Test]
    public async Task RemoveAliasSet_OnAMissingManifest_TreatsItAsEmptyRatherThanAFailure()
    {
        // The absent-manifest case stays a normal "nothing matched" answer: there is no row to lose, so the caller's
        // superseded path is the right outcome, and this is the negative control for the three failures above.
        using var dir = new Infra.TempModelsDir();
        using var registry = Infra.Registry(Infra.Options(dir.Path));
        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");

        var removed = await registry.RemoveAliasSetIfMatchAsync([Entry(filePath)], CancellationToken.None);

        AssertEx.Null(removed);
        AssertEx.False(File.Exists(Path.Combine(dir.Path, "index.json")), "a no-match mutation must not create a manifest");
    }

    private static GgufModelRegistryEntry Entry(string filePath)
    {
        return new GgufModelRegistryEntry
        {
            ModelName = Infra.ModelName,
            RepoId = Infra.RepoId,
            FileName = Infra.FileName,
            Quant = Infra.Quant,
            LocalPath = filePath,
            SizeBytes = 9,
            Sha256 = "verified-sha256",
            SourceRevision = "abc123",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat
        };
    }
}
