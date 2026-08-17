namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     A training export commits through the SAME importer a local import does — the only difference is a trained
///     destination with lineage attached. These are the end-to-end invariants that decide whether a promoted model is
///     traceable and, for an adapter, launchable at all.
/// </summary>
public sealed class GgufTrainedCommitTests
{
    private const string MergedFileName = "tuned-merged-q4_k_m-0123456789abcdef01234567.gguf";
    private const string AdapterFileName = "tuned-adapter-f16-0123456789abcdef01234567.gguf";

    [Test]
    public async Task Commit_MergedTrainedModel_CarriesOriginTrainedAndItsDerivedFromLineage()
    {
        using var paths = new TrainedCommitPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "merged-Q4_K_M.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = new GgufModelImporter(registry, Infra.AbundantSpace(), options, TimeProvider.System);

        var prepared = await importer.PrepareAsync(new GgufImportSource(source), MergedDestination(), progress: null, CancellationToken.None);
        var receipt = await importer.CommitAsync(prepared, CancellationToken.None);

        var entry = receipt.RegistryEntry;
        AssertEx.Equal(LocalModelOrigin.Trained, entry.Origin);
        AssertEx.Equal("meta/base", entry.DerivedFromRepoId);
        AssertEx.Equal("main", entry.DerivedFromRevision);
        AssertEx.Equal("v1:dataset", entry.DerivedFromContentFingerprint);
        // A merged model is standalone: it has weights of its own, so none of the adapter member fields apply.
        AssertEx.Null(entry.AdapterFileName);
        AssertEx.Null(entry.BaseModelName);

        // The commit re-reads its own sidecar and refuses anything shape-invalid, so a successful commit already
        // proves the sidecar round-trips; re-reading it here pins that the registry entry does too.
        var rebuilt = AssertEx.NotNull(GgufAcquisitionSidecar.FromRegistryEntry(entry, paths.ModelsDirectory),
            "A committed trained entry must rebuild a shape-valid sidecar.");
        AssertEx.Equal(rebuilt.RegistryRevision, entry.RegistryRevision);
    }

    [Test]
    public async Task Commit_TrainedAdapter_MirrorsItsOwnBytesIntoTheAdapterMembersAndNamesItsBaseModel()
    {
        // The invariant the sidecar enforces: an adapter entry has no separate weight file, so its adapter member
        // fields ARE its weight fields. A commit that got this wrong would be rejected on the next integrity read.
        using var paths = new TrainedCommitPaths();
        var source = paths.WriteSource(BuildAdapterGguf(), "adapter-F16.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = new GgufModelImporter(registry, Infra.AbundantSpace(), options, TimeProvider.System);

        var prepared = await importer.PrepareAsync(new GgufImportSource(source), AdapterDestination(), progress: null, CancellationToken.None);
        var entry = (await importer.CommitAsync(prepared, CancellationToken.None)).RegistryEntry;

        AssertEx.Equal(LocalModelOrigin.Trained, entry.Origin);
        AssertEx.Equal(entry.AdapterFileName!, entry.FileName);
        AssertEx.Equal(entry.AdapterSha256!, entry.Sha256);
        AssertEx.Equal(entry.SizeBytes, entry.AdapterSizeBytes);
        AssertEx.Equal("base:Q4_K_M", entry.BaseModelName);
        _ = AssertEx.NotNull(GgufAcquisitionSidecar.FromRegistryEntry(entry, paths.ModelsDirectory),
            "A promoted adapter entry must satisfy the sidecar's adapter shape rules.");
    }

    [Test]
    public async Task Commit_PromotedAdapter_LaunchesTheBaseModelWithTheAdapterApplied()
    {
        // The end of the chain: a promoted adapter is only useful if the supervisor turns it into
        // `-m <base> --lora <adapter>`. Anything else is an entry that can never serve.
        using var paths = new TrainedCommitPaths();
        var source = paths.WriteSource(BuildAdapterGguf(), "adapter-F16.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = new GgufModelImporter(registry, Infra.AbundantSpace(), options, TimeProvider.System);
        var prepared = await importer.PrepareAsync(new GgufImportSource(source), AdapterDestination(), progress: null, CancellationToken.None);
        var entry = (await importer.CommitAsync(prepared, CancellationToken.None)).RegistryEntry;

        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(new LlamaServerProcessSupervisor.ProcessKey(entry.ModelName, ModelRole.Chat),
            "/fake/bin/llama-server",
            "/models/base.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 0,
            adapterFilePath: entry.LocalPath);

        AssertEx.Equal("/models/base.gguf", spec.Arguments[IndexOf(spec.Arguments, "-m") + 1]);
        AssertEx.Equal(entry.LocalPath, spec.Arguments[IndexOf(spec.Arguments, "--lora") + 1]);
    }

    [Test]
    public async Task Prepare_TrainedDestinationWithoutLineage_IsRejected()
    {
        // Lineage is what separates a trained entry from an import. Without it, "what was this trained on" becomes
        // unanswerable the moment the run row is deleted.
        using var paths = new TrainedCommitPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "merged-Q4_K_M.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = new GgufModelImporter(registry, Infra.AbundantSpace(), options, TimeProvider.System);

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() => importer.PrepareAsync(new GgufImportSource(source),
            MergedDestination() with
            {
                Lineage = null
            },
            progress: null,
            CancellationToken.None));
    }

    [Test]
    public async Task Prepare_ImportedDestinationCarryingLineage_IsRejected()
    {
        // The converse guard: an ordinary operator import has no training behind it, and letting it claim lineage
        // would make the registry's derived-from fields meaningless.
        using var paths = new TrainedCommitPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "merged-Q4_K_M.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = new GgufModelImporter(registry, Infra.AbundantSpace(), options, TimeProvider.System);

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() => importer.PrepareAsync(new GgufImportSource(source),
            MergedDestination() with
            {
                Origin = LocalModelOrigin.Imported
            },
            progress: null,
            CancellationToken.None));
    }

    [Test]
    public async Task Prepare_TrainedAdapterOnThePublicImportPath_IsStillRejected()
    {
        // An adapter is only ever acceptable because the destination names a base model. A trained destination that
        // does NOT is a merged-model commit, and an adapter file is not one.
        using var paths = new TrainedCommitPaths();
        var source = paths.WriteSource(BuildAdapterGguf(), "adapter-F16.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = new GgufModelImporter(registry, Infra.AbundantSpace(), options, TimeProvider.System);

        var failure = await AssertEx.ThrowsAsync<GgufImportException>(() => importer.PrepareAsync(new GgufImportSource(source),
            MergedDestination(),
            progress: null,
            CancellationToken.None));

        AssertEx.Equal(GgufImportRejectionCode.UnsupportedModelType, failure.Reason);
    }

    private static GgufImportDestination MergedDestination() =>
        new("Tuned:Q4_K_M",
            "Q4_K_M",
            MergedFileName,
            MergedFileName + ".xe-model.json",
            LocalModelOrigin.Trained,
            ProjectorRelativePath: null,
            new TrainedModelLineage("meta/base", "main", "v1:dataset"));

    private static GgufImportDestination AdapterDestination() =>
        new("Tuned-Adapter:F16",
            "F16",
            AdapterFileName,
            AdapterFileName + ".xe-model.json",
            LocalModelOrigin.Trained,
            ProjectorRelativePath: null,
            new TrainedModelLineage("meta/base", "main", "v1:dataset", "base:Q4_K_M"));

    private static byte[] BuildCausalGguf() =>
        new GgufHeaderBytesBuilder()
            .WithString("general.architecture", "llama")
            .WithString("general.type", "model")
            .WithUint32("general.file_type", value: 15)
            .Build();

    // A LoRA adapter carries the base architecture and declares itself only through general.type. It has no
    // file_type, which is why the destination's own quantization has to be canonical for the commit to proceed.
    private static byte[] BuildAdapterGguf() =>
        new GgufHeaderBytesBuilder()
            .WithString("general.architecture", "llama")
            .WithString("general.type", "adapter")
            .Build();

    private static int IndexOf(IReadOnlyList<string> arguments, string flag)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], flag, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new AssertionException($"Expected flag '{flag}' in argument vector.");
    }

    private sealed class TrainedCommitPaths : IDisposable
    {
        public TrainedCommitPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), "xe-trained-commit-" + Guid.NewGuid().ToString("N"));
            ModelsDirectory = Path.Combine(Root, "models");
            SourceDirectory = Path.Combine(Root, "sources");
            _ = Directory.CreateDirectory(ModelsDirectory);
            _ = Directory.CreateDirectory(SourceDirectory);
        }

        public string Root { get; }
        public string ModelsDirectory { get; }
        public string SourceDirectory { get; }

        public string WriteSource(byte[] bytes, string fileName)
        {
            var path = Path.Combine(SourceDirectory, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort fixture cleanup; a locked file must not mask the assertion result.
            }
        }
    }
}
