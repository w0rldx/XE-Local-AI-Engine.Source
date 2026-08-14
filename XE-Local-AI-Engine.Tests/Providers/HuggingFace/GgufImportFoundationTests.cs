namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

public sealed class GgufImportFoundationTests
{
    [Test]
    public void LocalModelOrigin_UsesOnlyExactLowercaseJsonValues()
    {
        AssertEx.Equal("\"huggingface\"", JsonSerializer.Serialize(LocalModelOrigin.HuggingFace));
        AssertEx.Equal("\"imported\"", JsonSerializer.Serialize(LocalModelOrigin.Imported));
        AssertEx.Equal(LocalModelOrigin.Imported, JsonSerializer.Deserialize<LocalModelOrigin>("\"imported\""));
        AssertEx.Throws<JsonException>(() => JsonSerializer.Deserialize<LocalModelOrigin>("\"Imported\""));
        AssertEx.Throws<JsonException>(() => JsonSerializer.Deserialize<LocalModelOrigin>("\"unknown\""));
    }

    [Test]
    public void Fingerprints_MatchGoldenVectors_AndRejectNoncanonicalMembers()
    {
        const string hash = "0000000000000000000000000000000000000000000000000000000000000000";
        AssertEx.Equal($"sha256:{hash}:4", GgufMemberFingerprint.Compute(hash, sizeBytes: 4));
        AssertEx.True(GgufMemberFingerprint.IsCanonical($"sha256:{hash}:4"));
        AssertEx.False(GgufMemberFingerprint.IsCanonical($"sha256:{hash.ToUpperInvariant()}:4"));
        AssertEx.False(GgufMemberFingerprint.IsCanonical($"sha256:{hash}:04"));

        var aggregate = GgufModelContentFingerprint.ComputeV1([
            new GgufModelContentMember("models/demo.gguf",
                InstalledModelPhysicalMemberRole.Weight,
                4,
                hash,
                ["Demo:Q4_K_M"])
        ]);
        AssertEx.Equal("v1:8905fc570b8816cccfd71335b65c2bd8997e13f4d6eaf0ab511f0b770eb9f256", aggregate);
    }

    [Test]
    public void RegistryRevision_MatchesGoldenVector_AndIgnoresTimestampAndToken()
    {
        var entry = GoldenEntry();
        const string expected = "v1:2c3638368aed92e8104a1b83063ca5dc99519bb0b44e64b2e29fb84eb15bfe2a";
        AssertEx.Equal(expected, GgufRegistryRevision.ComputeV1(entry));
        AssertEx.Equal(expected, GgufRegistryRevision.ComputeV1(entry with
        {
            DownloadedAtUtc = DateTimeOffset.UnixEpoch.AddYears(10),
            RegistryRevision = "v1:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        }));
        AssertEx.NotEqual(expected, GgufRegistryRevision.ComputeV1(entry with { RepoId = "other/repo" }));
        AssertEx.NotEqual(expected, GgufRegistryRevision.ComputeV1(entry with { Origin = LocalModelOrigin.HuggingFace }));
    }

    [Test]
    public async Task Inspector_AcceptsSupportedCausalModel_WithoutLeakingSourcePath()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "operator-secret.gguf");
        var inspector = new GgufImportInspector(Infra.Options(paths.ModelsDirectory));

        var result = await inspector.InspectAsync(new GgufImportSource(source), CancellationToken.None);

        AssertEx.True(result.IsAccepted);
        AssertEx.Equal(GgufImportWorkload.CausalChat, result.Workload);
        AssertEx.Equal("llama", result.Architecture);
        AssertEx.Equal("Q4_K_M", result.DetectedQuantization);
        AssertEx.Equal("operator-secret.gguf", result.SourceDisplayName);
        AssertEx.False(JsonSerializer.Serialize(result).Contains(paths.Root, StringComparison.Ordinal));
    }

    [Test]
    [Arguments("model-00001-of-00002.gguf", "llama", GgufImportRejectionCode.SplitModel)]
    [Arguments("embedding.gguf", "bert", GgufImportRejectionCode.UnsupportedArchitecture)]
    [Arguments("projector.gguf", "llama", GgufImportRejectionCode.UnsupportedArchitecture)]
    public async Task Inspector_RejectsLockedNonChatClassifications(string fileName,
        string architecture,
        GgufImportRejectionCode expected)
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(architecture), fileName);
        var result = await new GgufImportInspector(Infra.Options(paths.ModelsDirectory))
                          .InspectAsync(new GgufImportSource(source), CancellationToken.None);
        AssertEx.Contains(result.Rejections, expected);
        AssertEx.Null(result.Workload);
    }

    [Test]
    public async Task Inspector_RejectsSymlinkAndManagedDirectorySources()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "source.gguf");
        var link = Path.Combine(paths.Root, "linked.gguf");
        File.CreateSymbolicLink(link, source);
        var managedSource = Path.Combine(paths.ModelsDirectory, "managed.gguf");
        await File.WriteAllBytesAsync(managedSource, BuildCausalGguf());
        var inspector = new GgufImportInspector(Infra.Options(paths.ModelsDirectory));

        var linkedResult = await inspector.InspectAsync(new GgufImportSource(link), CancellationToken.None);
        var managedResult = await inspector.InspectAsync(new GgufImportSource(managedSource), CancellationToken.None);

        AssertEx.Contains(linkedResult.Rejections, GgufImportRejectionCode.InvalidSource);
        AssertEx.Contains(managedResult.Rejections, GgufImportRejectionCode.InvalidSource);
    }

    [Test]
    public async Task Importer_PreparesCommitsRecoversAndRollsBack_WithoutPersistingSourcePath()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "private-source.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var destination = Destination();

        var prepared = await importer.PrepareAsync(new GgufImportSource(source), destination, progress: null, CancellationToken.None);
        AssertEx.False(File.Exists(Path.Combine(paths.ModelsDirectory, destination.RelativeGgufPath)));
        var receipt = await importer.CommitAsync(prepared, CancellationToken.None);

        AssertEx.True(File.Exists(receipt.FinalGgufPath));
        AssertEx.True(File.Exists(receipt.FinalSidecarPath));
        var sidecarJson = await File.ReadAllTextAsync(receipt.FinalSidecarPath);
        AssertEx.False(sidecarJson.Contains(paths.Root, StringComparison.Ordinal));
        AssertEx.Contains(sidecarJson, "private-source.gguf", StringComparison.Ordinal);

        File.Delete(Path.Combine(paths.ModelsDirectory, "index.json"));
        using var recoveredRegistry = Infra.Registry(options);
        var recovered = await recoveredRegistry.FindAsync(destination.CanonicalModelName, CancellationToken.None);
        AssertEx.NotNull(recovered);
        AssertEx.Equal(LocalModelOrigin.Imported, recovered!.Origin);
        AssertEx.Equal(receipt.RegistryEntry.RegistryRevision!, recovered.RegistryRevision);
        AssertEx.Equal(receipt.ModelContentFingerprint, recovered.ModelContentFingerprint);

        await importer.RollbackCommittedAsync(receipt, CancellationToken.None);
        AssertEx.False(File.Exists(receipt.FinalGgufPath));
        AssertEx.False(File.Exists(receipt.FinalSidecarPath));
    }

    [Test]
    public async Task Importer_CommitNeverOverwritesAnExistingDestination_AndDiscardsTemps()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "source.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var prepared = await importer.PrepareAsync(new GgufImportSource(source), Destination(), progress: null, CancellationToken.None);
        var finalPath = Path.Combine(paths.ModelsDirectory, prepared.Destination.RelativeGgufPath);
        await File.WriteAllTextAsync(finalPath, "do-not-overwrite");

        await AssertEx.ThrowsAsync<IOException>(() => importer.CommitAsync(prepared, CancellationToken.None));

        AssertEx.Equal("do-not-overwrite", await File.ReadAllTextAsync(finalPath));
        await importer.DiscardPreparedAsync(prepared, CancellationToken.None);
        AssertEx.False(File.Exists(prepared.TemporaryGgufPath));
        AssertEx.False(File.Exists(prepared.TemporarySidecarPath));
    }

    [Test]
    public async Task Importer_CancellationDuringCopy_RemovesAllTemporaryFiles()
    {
        using var paths = new ImportPaths();
        var bytes = BuildCausalGguf().Concat(new byte[200_000]).ToArray();
        var source = paths.WriteSource(bytes, "source.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<GgufImportProgress>(_ => cancellation.Cancel());

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            importer.PrepareAsync(new GgufImportSource(source), Destination(), progress, cancellation.Token));

        AssertEx.Equal(expected: 0, Directory.EnumerateFiles(paths.ModelsDirectory, "*.part", SearchOption.TopDirectoryOnly).Count());
    }

    private static GgufModelImporter NewImporter(XE_Local_AI_Engine.Providers.HuggingFace.Options.HuggingFaceOptions options,
        GgufModelRegistry registry)
    {
        return new GgufModelImporter(new GgufImportInspector(options), registry, Infra.AbundantSpace(), options, TimeProvider.System);
    }

    private static GgufImportDestination Destination()
    {
        return new GgufImportDestination("Local/Demo:Q4_K_M",
            "Q4_K_M",
            "local-demo-q4_k_m-0123456789abcdef01234567.gguf",
            "local-demo-q4_k_m-0123456789abcdef01234567.gguf.xe-model.json",
            LocalModelOrigin.Imported);
    }

    private static byte[] BuildCausalGguf(string architecture = "llama")
    {
        return new GgufHeaderBytesBuilder()
              .WithString("general.architecture", architecture)
              .WithString("general.type", "model")
              .WithUint32("general.file_type", value: 15)
              .Build();
    }

    private static GgufModelRegistryEntry GoldenEntry()
    {
        return new GgufModelRegistryEntry
        {
            ModelName = "Demo:Q4_K_M",
            RepoId = "org/repo",
            FileName = "demo-Q4_K_M.gguf",
            Quant = "Q4_K_M",
            LocalPath = "models/demo-Q4_K_M.gguf",
            SizeBytes = 4,
            Sha256 = new string('0', 64),
            SourceRevision = "abc",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat,
            Origin = LocalModelOrigin.Imported,
            SourceDisplayName = "source.gguf",
            MetadataSchemaVersion = 1
        };
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ImportPaths : IDisposable
    {
        public ImportPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), "xe-import-foundation-" + Guid.NewGuid().ToString("N"));
            ModelsDirectory = Path.Combine(Root, "models");
            SourceDirectory = Path.Combine(Root, "sources");
            Directory.CreateDirectory(ModelsDirectory);
            Directory.CreateDirectory(SourceDirectory);
        }

        public string Root { get; }
        public string ModelsDirectory { get; }
        private string SourceDirectory { get; }

        public string WriteSource(byte[] bytes, string fileName)
        {
            var path = Path.Combine(SourceDirectory, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
