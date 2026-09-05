namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;
using OS = TUnit.Core.Enums.OS;

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
        const string alphaHash = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";
        AssertEx.Equal($"sha256:{hash}:4", GgufMemberFingerprint.Compute(hash, sizeBytes: 4));
        AssertEx.True(GgufMemberFingerprint.IsCanonical($"sha256:{hash}:4"));
        AssertEx.False(GgufMemberFingerprint.IsCanonical($"sha256:{alphaHash.ToUpperInvariant()}:4"));
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
        var root = Path.GetFullPath(".");
        const string expected = "v1:2c3638368aed92e8104a1b83063ca5dc99519bb0b44e64b2e29fb84eb15bfe2a";
        AssertEx.Equal(expected, GgufRegistryRevision.ComputeV1(entry, root));
        AssertEx.Equal(expected, GgufRegistryRevision.ComputeV1(entry with
        {
            DownloadedAtUtc = DateTimeOffset.UnixEpoch.AddYears(10),
            RegistryRevision = "v1:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        }, root));
        AssertEx.NotEqual(expected, GgufRegistryRevision.ComputeV1(entry with
        {
            RepoId = "other/repo"
        }, root));
        AssertEx.NotEqual(expected, GgufRegistryRevision.ComputeV1(entry with
        {
            Origin = LocalModelOrigin.HuggingFace
        }, root));
        AssertEx.Throws<ArgumentException>(() => GgufRegistryRevision.ComputeV1(entry with
        {
            LocalPath = Path.GetFullPath(Path.Combine(root, "..", "escaped.gguf"))
        }, root));
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
        AssertEx.True(GgufRegistryRevision.IsCanonical(result.SourceIdentityToken));
        AssertEx.False(JsonSerializer.Serialize(result).Contains(paths.Root, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Reasoning-budget enforceability is decided at IMPORT, from the same strict header the acceptance decision
    ///     reads, so an operator learns BEFORE the multi-gigabyte copy that this model's thinking cap will not stick.
    ///     A template that opens a thinking channel and never closes it is still a perfectly usable reasoning model —
    ///     the import is ACCEPTED — but llama.cpp would accept its <c>reasoning_budget_tokens</c> and then ignore it,
    ///     which is what the warning says.
    /// </summary>
    [Test]
    public async Task Inspector_ReasoningTemplateWithoutEndMarker_IsAcceptedAndWarnsThatTheBudgetCannotBeEnforced()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildReasoningGguf("{%- if enable_thinking %}<think>\n{%- endif %}"), "unclosed-think-Q4_K_M.gguf");
        var inspector = new GgufImportInspector(Infra.Options(paths.ModelsDirectory));

        var result = await inspector.InspectAsync(new GgufImportSource(source), CancellationToken.None);

        AssertEx.True(result.IsAccepted, "an unenforceable thinking budget is an advisory, never a rejection");
        AssertEx.False(result.ReasoningBudgetEnforceable);
        AssertEx.ContainsSingle(result.Warnings, static warning => warning.Contains("thinking budget", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The counterpart: the real Qwen3.x shape renders <c>&lt;/think&gt;</c> after the reasoning, which llama.cpp's
    ///     differential autoparser recovers as a think-end tag, so the budget IS enforceable and the import carries no
    ///     advisory at all.
    /// </summary>
    [Test]
    public async Task Inspector_ReasoningTemplateWithEndMarker_IsEnforceableAndWarnsNothing()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildReasoningGguf("{{- '<think>\n' + reasoning_content + '\n</think>\n\n' + content }}"),
            "closed-think-Q4_K_M.gguf");
        var inspector = new GgufImportInspector(Infra.Options(paths.ModelsDirectory));

        var result = await inspector.InspectAsync(new GgufImportSource(source), CancellationToken.None);

        AssertEx.True(result.IsAccepted);
        AssertEx.True(result.ReasoningBudgetEnforceable);
        AssertEx.Empty(result.Warnings);
    }

    [Test]
    public async Task Inspector_AdapterWorkload_InProcessOnly()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildAdapterGguf(), "tuned-lora-Q4_K_M.gguf");
        var inspector = new GgufImportInspector(Infra.Options(paths.ModelsDirectory));

        var inProcess = await inspector.InspectAsync(new GgufImportSource(source),
            GgufImportInspectionMode.InProcessTrainedCommit,
            CancellationToken.None);
        AssertEx.True(inProcess.IsAccepted, "A trained LoRA adapter must be accepted on the in-process path.");
        AssertEx.Equal(GgufImportWorkload.LoraAdapter, inProcess.Workload);

        // The public HTTP import surface is unchanged: an adapter upload is still refused on its type.
        var publicImport = await inspector.InspectAsync(new GgufImportSource(source), CancellationToken.None);
        AssertEx.False(publicImport.IsAccepted);
        AssertEx.Contains(publicImport.Rejections, GgufImportRejectionCode.UnsupportedModelType);
        AssertEx.Null(publicImport.Workload);
    }

    [Test]
    public async Task Inspector_MergedNamedAdapter_MetadataDecided()
    {
        // A merged fine-tune is an ordinary model whose FILE NAME happens to contain "adapter". On the in-process path
        // the decision comes from general.type, so the name costs it nothing; on the public path the name heuristic
        // still rejects it, exactly as before.
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "merged-adapter-model-Q4_K_M.gguf");
        var inspector = new GgufImportInspector(Infra.Options(paths.ModelsDirectory));

        var inProcess = await inspector.InspectAsync(new GgufImportSource(source),
            GgufImportInspectionMode.InProcessTrainedCommit,
            CancellationToken.None);
        AssertEx.True(inProcess.IsAccepted, "A merged fine-tune must not be rejected for its file name.");
        AssertEx.Equal(GgufImportWorkload.CausalChat, inProcess.Workload);

        var publicImport = await inspector.InspectAsync(new GgufImportSource(source), CancellationToken.None);
        AssertEx.False(publicImport.IsAccepted);
        AssertEx.Contains(publicImport.Rejections, GgufImportRejectionCode.UnsupportedArchitecture);
    }

    [Test]
    public async Task Inspector_InProcess_StillRejectsUnsupportedArchitecture()
    {
        // The bypass covers the display-name heuristic only. A non-causal architecture stays rejected on both paths —
        // an export that produced one is a bug, not a model to register.
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(architecture: "bert"), "tuned-Q4_K_M.gguf");
        var inspector = new GgufImportInspector(Infra.Options(paths.ModelsDirectory));

        var inProcess = await inspector.InspectAsync(new GgufImportSource(source),
            GgufImportInspectionMode.InProcessTrainedCommit,
            CancellationToken.None);

        AssertEx.False(inProcess.IsAccepted);
        AssertEx.Contains(inProcess.Rejections, GgufImportRejectionCode.UnsupportedArchitecture);
        AssertEx.Equal("bert", inProcess.Architecture);
    }

    [Test]
    public async Task Inspector_SourceIdentityToken_ChangesWhenSamePathIsAtomicallyReplaced()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "source.gguf");
        var replacement = paths.WriteSource(BuildCausalGguf().Append((byte)0).ToArray(), "replacement.gguf");
        var inspector = new GgufImportInspector(Infra.Options(paths.ModelsDirectory));

        var before = await inspector.InspectAsync(new GgufImportSource(source), CancellationToken.None);
        File.Move(replacement, source, overwrite: true);
        var after = await inspector.InspectAsync(new GgufImportSource(source), CancellationToken.None);

        AssertEx.True(before.IsAccepted);
        AssertEx.True(after.IsAccepted);
        AssertEx.NotEqual(before.SourceIdentityToken, after.SourceIdentityToken);
    }

    [Test]
    public void ValidatedSource_WindowsFlags_RequestAtomicNoFollowAndActualAsyncIo()
    {
        AssertEx.True((ValidatedGgufImportSource.WindowsOpenFlags & ValidatedGgufImportSource.WindowsOpenReparsePoint) != 0);
        AssertEx.True((ValidatedGgufImportSource.WindowsOpenFlags & ValidatedGgufImportSource.WindowsOverlapped) != 0);
        AssertEx.True((ValidatedGgufImportSource.WindowsOpenFlags & ValidatedGgufImportSource.WindowsSequentialScan) != 0);
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
    public async Task Inspector_RejectsSymlinkAncestor_AndUsesFilenameQuantBeforeHeader()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "model-Q8_0.gguf");
        var inspector = new GgufImportInspector(Infra.Options(paths.ModelsDirectory));
        var filenameQuant = await inspector.InspectAsync(new GgufImportSource(source), CancellationToken.None);
        AssertEx.Equal("Q8_0", filenameQuant.DetectedQuantization);

        if (!OperatingSystem.IsWindows())
        {
            var linkedDirectory = Path.Combine(paths.Root, "linked-sources");
            Directory.CreateSymbolicLink(linkedDirectory, paths.SourceDirectoryPath);
            var linkedSource = Path.Combine(linkedDirectory, Path.GetFileName(source));
            var linkedResult = await inspector.InspectAsync(new GgufImportSource(linkedSource), CancellationToken.None);
            AssertEx.Contains(linkedResult.Rejections, GgufImportRejectionCode.InvalidSource);
        }
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

        await File.WriteAllTextAsync(Path.Combine(paths.ModelsDirectory, "index.json"), "{\"models\":[]}");
        using var partialRegistry = Infra.Registry(options);
        var reconciled = await partialRegistry.FindAsync(destination.CanonicalModelName, CancellationToken.None);
        AssertEx.NotNull(reconciled);
        AssertEx.Equal(receipt.RegistryEntry.RegistryRevision!, reconciled!.RegistryRevision);

        var snapshotStore = new InstalledGgufSnapshotStore(partialRegistry, options);
        var candidate = await snapshotStore.DiscoverCandidateAsync(destination.CanonicalModelName, CancellationToken.None);
        AssertEx.NotNull(candidate);
        var snapshot = await snapshotStore.LoadVerifiedAsync(destination.CanonicalModelName, candidate!, CancellationToken.None);
        AssertEx.Equal(expected: 2, snapshot.Members.Count);
        AssertEx.ContainsSingle(snapshot.Members, static member => member.Role == InstalledModelPhysicalMemberRole.Weight);
        AssertEx.ContainsSingle(snapshot.Members, static member => member.Role == InstalledModelPhysicalMemberRole.Sidecar);
        AssertEx.ContainsSingle(snapshot.RegistryAliases, alias => alias.RegistryRevision == receipt.RegistryEntry.RegistryRevision);
        AssertEx.True(GgufRegistryRevision.IsCanonical(snapshot.RegistryAliasSetHash));
        AssertEx.True(GgufRegistryRevision.IsCanonical(snapshot.PhysicalMemberSetHash));
        AssertEx.Equal(receipt.ModelContentFingerprint, snapshot.ModelContentFingerprint);
        AssertEx.False(JsonSerializer.Serialize(snapshot).Contains(paths.Root, StringComparison.Ordinal));

        await importer.RollbackCommittedAsync(receipt, CancellationToken.None);
        AssertEx.False(File.Exists(receipt.FinalGgufPath));
        AssertEx.False(File.Exists(receipt.FinalSidecarPath));
    }

    [Test]
    public async Task Registry_RepairsSidecarFromVerifiedManifest_WhenAcquisitionSidecarIsMissingOrCorrupt()
    {
        // The manifest row's RegistryRevision is a self-consistency token over its own fields (see
        // GgufRegistryRevision.ComputeV1); it already passed that check when written by CommitAsync. A missing or
        // corrupt acquisition sidecar afterward must neither delete the entry nor leave a half-state where
        // the model is loadable but the sidecar-requiring mutation/deletion snapshot checks fail with a server error —
        // the registry rewrites the sidecar from the token-verified manifest value, proven by round-tripping the exact
        // revision.
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "source.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var prepared = await importer.PrepareAsync(new GgufImportSource(source), Destination(), progress: null, CancellationToken.None);
        var receipt = await importer.CommitAsync(prepared, CancellationToken.None);

        // Sidecar missing entirely: a fresh registry load keeps the manifest entry it already trusts AND repairs the
        // sidecar so deletion/mutation snapshot loading works again.
        File.Delete(receipt.FinalSidecarPath);
        using (var missingSidecarRegistry = Infra.Registry(options))
        {
            var listed = await missingSidecarRegistry.ListAsync(CancellationToken.None);
            AssertEx.ContainsSingle(listed, entry => entry.ModelName == receipt.RegistryEntry.ModelName);
            var kept = listed.Single(entry => entry.ModelName == receipt.RegistryEntry.ModelName);
            AssertEx.Equal(receipt.RegistryEntry.RegistryRevision!, kept.RegistryRevision);
            AssertEx.Equal(receipt.RegistryEntry.ModelContentFingerprint!, kept.ModelContentFingerprint);
            AssertEx.Equal(receipt.RegistryEntry.Sha256!, kept.Sha256);
            AssertEx.True(File.Exists(kept.LocalPath), "the backing GGUF must not be touched when its sidecar is missing");
            AssertEx.True(File.Exists(receipt.FinalSidecarPath), "the sidecar must be rewritten from the verified manifest entry");
            var repaired = await GgufAcquisitionSidecar.ReadValidAsync(receipt.FinalSidecarPath, kept.LocalPath,
                paths.ModelsDirectory, CancellationToken.None);
            AssertEx.NotNull(repaired);
            AssertEx.Equal(receipt.RegistryEntry.RegistryRevision!, repaired!.RegistryRevision);
        }

        // Sidecar present but corrupt (unreadable JSON): same outcome — the invalid file is replaced by the repaired
        // one, with no invented provenance and no deletion.
        await File.WriteAllTextAsync(receipt.FinalSidecarPath, "{ this is not valid json");
        using var corruptSidecarRegistry = Infra.Registry(options);
        var listedAfterCorruption = await corruptSidecarRegistry.ListAsync(CancellationToken.None);
        AssertEx.ContainsSingle(listedAfterCorruption, entry => entry.ModelName == receipt.RegistryEntry.ModelName);
        var keptAfterCorruption = listedAfterCorruption.Single(entry => entry.ModelName == receipt.RegistryEntry.ModelName);
        AssertEx.Equal(receipt.RegistryEntry.RegistryRevision!, keptAfterCorruption.RegistryRevision);
        AssertEx.Equal(receipt.RegistryEntry.ModelContentFingerprint!, keptAfterCorruption.ModelContentFingerprint);
        var repairedAfterCorruption = await GgufAcquisitionSidecar.ReadValidAsync(receipt.FinalSidecarPath,
            keptAfterCorruption.LocalPath, paths.ModelsDirectory, CancellationToken.None);
        AssertEx.NotNull(repairedAfterCorruption);
        AssertEx.Equal(receipt.RegistryEntry.RegistryRevision!, repairedAfterCorruption!.RegistryRevision);
    }

    [Test]
    public async Task Registry_DoesNotRepairSidecar_WhenWeightBytesWereReplaced()
    {
        // Repair reconstructs metadata only; it must never bless replaced bytes. When the weight no longer matches the
        // manifest's committed size, the entry is kept so the manifest stays readable, but no sidecar is
        // written, so content verification keeps failing closed at every mutation/launch boundary.
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "source.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var prepared = await importer.PrepareAsync(new GgufImportSource(source), Destination(), progress: null, CancellationToken.None);
        var receipt = await importer.CommitAsync(prepared, CancellationToken.None);

        File.Delete(receipt.FinalSidecarPath);
        await File.AppendAllTextAsync(receipt.RegistryEntry.LocalPath, "tampered");
        using var reloaded = Infra.Registry(options);
        var listed = await reloaded.ListAsync(CancellationToken.None);
        AssertEx.ContainsSingle(listed, entry => entry.ModelName == receipt.RegistryEntry.ModelName);
        AssertEx.False(File.Exists(receipt.FinalSidecarPath),
            "a size-mismatched weight must not receive a regenerated sidecar");
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
        // Sidecar moves first (see CommitAsync), so a collision on the sidecar destination is the one still checked
        // before anything is moved — a collision on the GGUF destination is covered separately as a partial-commit
        // case in Importer_PostRenameRegistryFailure_ReturnsOwnedReceiptForApplicationRollback.
        var finalSidecarPath = Path.Combine(paths.ModelsDirectory, prepared.Destination.RelativeSidecarPath);
        await File.WriteAllTextAsync(finalSidecarPath, "do-not-overwrite");

        var exception = await AssertEx.ThrowsAsync<GgufImportException>(() => importer.CommitAsync(prepared, CancellationToken.None));

        AssertEx.Equal(GgufImportRejectionCode.DestinationConflict, exception.Reason);
        AssertEx.Equal("do-not-overwrite", await File.ReadAllTextAsync(finalSidecarPath));
        await importer.DiscardPreparedAsync(prepared, CancellationToken.None);
        AssertEx.False(File.Exists(prepared.TemporaryGgufPath));
        AssertEx.False(File.Exists(prepared.TemporarySidecarPath));
    }

    [Test]
    public async Task Importer_PostRenameRegistryFailure_ReturnsOwnedReceiptForApplicationRollback()
    {
        using var paths = new ImportPaths();
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var prepared = await importer.PrepareAsync(new GgufImportSource(paths.WriteSource(BuildCausalGguf(), "source.gguf")),
            Destination(),
            progress: null,
            CancellationToken.None);
        var manifestPath = Path.Combine(paths.ModelsDirectory, "index.json");
        Directory.CreateDirectory(manifestPath);

        var exception = await AssertEx.ThrowsAsync<GgufImportCommitException>(() =>
            importer.CommitAsync(prepared, CancellationToken.None));

        AssertEx.True(exception.CommitReceipt.OwnsFinalGguf);
        AssertEx.True(exception.CommitReceipt.OwnsFinalSidecar);
        AssertEx.True(File.Exists(exception.CommitReceipt.FinalGgufPath));
        AssertEx.True(File.Exists(exception.CommitReceipt.FinalSidecarPath));
        Directory.Delete(manifestPath);
        await importer.RollbackCommittedAsync(exception.CommitReceipt, CancellationToken.None);
        AssertEx.False(File.Exists(exception.CommitReceipt.FinalGgufPath));
        AssertEx.False(File.Exists(exception.CommitReceipt.FinalSidecarPath));
    }

    [Test]
    public async Task Importer_RollbackAndDiscard_ReportOwnedArtifactsThatCouldNotBeDeleted()
    {
        using var paths = new ImportPaths();
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var source = new GgufImportSource(paths.WriteSource(BuildCausalGguf(), "source.gguf"));
        var committedPrepared = await importer.PrepareAsync(source, Destination(), progress: null, CancellationToken.None);
        var receipt = await importer.CommitAsync(committedPrepared, CancellationToken.None);
        File.Delete(receipt.FinalSidecarPath);
        Directory.CreateDirectory(receipt.FinalSidecarPath);

        _ = await AssertEx.ThrowsAsync<IOException>(() =>
            importer.RollbackCommittedAsync(receipt, CancellationToken.None));
        AssertEx.True(Directory.Exists(receipt.FinalSidecarPath));
        Directory.Delete(receipt.FinalSidecarPath);

        var discardPrepared = await importer.PrepareAsync(source, Destination(), progress: null, CancellationToken.None);
        File.Delete(discardPrepared.TemporarySidecarPath);
        Directory.CreateDirectory(discardPrepared.TemporarySidecarPath);

        _ = await AssertEx.ThrowsAsync<IOException>(() =>
            importer.DiscardPreparedAsync(discardPrepared, CancellationToken.None));
        AssertEx.True(Directory.Exists(discardPrepared.TemporarySidecarPath));
    }

    [Test]
    public async Task Importer_RollbackContinuesAfterRegistryRemovalCrash_AndRejectsNewOwner()
    {
        using var paths = new ImportPaths();
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var prepared = await importer.PrepareAsync(new GgufImportSource(paths.WriteSource(BuildCausalGguf(), "source.gguf")),
            Destination(),
            progress: null,
            CancellationToken.None);
        var receipt = await importer.CommitAsync(prepared, CancellationToken.None);
        AssertEx.True(await registry.RemoveExactAsync(receipt.RegistryEntry, CancellationToken.None));
        File.Delete(receipt.FinalSidecarPath);

        await importer.RollbackCommittedAsync(receipt, CancellationToken.None);

        AssertEx.False(File.Exists(receipt.FinalGgufPath));
        await File.WriteAllBytesAsync(receipt.FinalGgufPath, BuildCausalGguf());
        var newOwner = receipt.RegistryEntry with
        {
            ModelName = "Different/Owner:Q4_K_M",
            Origin = null,
            MetadataSchemaVersion = null,
            ModelContentFingerprint = null,
            RegistryRevision = null
        };
        await registry.InsertIfAbsentAsync(newOwner, CancellationToken.None);

        var exception = await AssertEx.ThrowsAsync<GgufImportException>(() =>
            importer.RollbackCommittedAsync(receipt, CancellationToken.None));

        AssertEx.Equal(GgufImportRejectionCode.DestinationConflict, exception.Reason);
        AssertEx.True(File.Exists(receipt.FinalGgufPath));
    }

    [Test]
    public async Task Registry_NormalListAndFind_DoNotRehashUnchangedManifestModelBytes()
    {
        using var paths = new ImportPaths();
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var prepared = await importer.PrepareAsync(new GgufImportSource(paths.WriteSource(BuildCausalGguf(), "source.gguf")),
            Destination(),
            progress: null,
            CancellationToken.None);
        var receipt = await importer.CommitAsync(prepared, CancellationToken.None);
        var tampered = await File.ReadAllBytesAsync(receipt.FinalGgufPath);
        tampered[^1] ^= 0x5a;
        await File.WriteAllBytesAsync(receipt.FinalGgufPath, tampered);
        var shape = await GgufAcquisitionSidecar.ReadShapeValidAsync(receipt.FinalSidecarPath,
            receipt.FinalGgufPath,
            paths.ModelsDirectory,
            CancellationToken.None);
        AssertEx.NotNull(shape);
        var recovered = GgufAcquisitionSidecar.ToRegistryEntry(shape!, receipt.FinalGgufPath, paths.ModelsDirectory);
        AssertEx.Equal(receipt.RegistryEntry.RegistryRevision!, recovered.RegistryRevision);
        AssertEx.Equal(receipt.RegistryEntry.LocalPath, recovered.LocalPath);

        AssertEx.ContainsSingle(await registry.ListAsync(CancellationToken.None), entry => entry.ModelName == receipt.RegistryEntry.ModelName);
        AssertEx.NotNull(await registry.FindAsync(receipt.RegistryEntry.ModelName, CancellationToken.None));

        var snapshots = new InstalledGgufSnapshotStore(registry, options);
        var candidate = await snapshots.DiscoverCandidateAsync(receipt.RegistryEntry.ModelName, CancellationToken.None);
        await AssertEx.ThrowsAsync<InstalledGgufSnapshotException>(() =>
            snapshots.LoadVerifiedAsync(receipt.RegistryEntry.ModelName, candidate!, CancellationToken.None));
    }

    [Test]
    public async Task Registry_ReconciliationPreservesCaseInsensitiveSameNameDifferentPathCollision()
    {
        using var paths = new ImportPaths();
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var prepared = await importer.PrepareAsync(new GgufImportSource(paths.WriteSource(BuildCausalGguf(), "source.gguf")),
            Destination(),
            progress: null,
            CancellationToken.None);
        var receipt = await importer.CommitAsync(prepared, CancellationToken.None);
        var unrelatedPath = Path.Combine(paths.ModelsDirectory, "unrelated-Q4_K_M.gguf");
        await File.WriteAllTextAsync(unrelatedPath, "unrelated");
        var unrelated = receipt.RegistryEntry with
        {
            ModelName = "local/demo:q4_k_m",
            RepoId = "unrelated",
            FileName = Path.GetFileName(unrelatedPath),
            LocalPath = unrelatedPath,
            SizeBytes = new FileInfo(unrelatedPath).Length,
            Sha256 = null,
            SourceRevision = string.Empty,
            Origin = null,
            SourceDisplayName = null,
            MetadataSchemaVersion = null,
            ModelContentFingerprint = null,
            RegistryRevision = null
        };
        unrelated = unrelated with
        {
            RegistryRevision = GgufRegistryRevision.ComputeV1(unrelated, paths.ModelsDirectory)
        };
        await File.WriteAllTextAsync(Path.Combine(paths.ModelsDirectory, "index.json"), JsonSerializer.Serialize(new
        {
            Models = new[]
            {
                unrelated
            }
        }));

        using var reconciled = Infra.Registry(options);
        var listed = await reconciled.ListAsync(CancellationToken.None);

        AssertEx.ContainsSingle(listed, entry => entry == unrelated);
        AssertEx.Null(await reconciled.FindAsync(receipt.RegistryEntry.ModelName, CancellationToken.None));
        AssertEx.True(File.Exists(receipt.FinalGgufPath));
        AssertEx.True(File.Exists(unrelatedPath));
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

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task Importer_CopiesFromValidatedHandle_AndRejectsPathReplacement()
    {
        using var paths = new ImportPaths();
        var bytes = BuildCausalGguf().Concat(new byte[200_000]).ToArray();
        var source = paths.WriteSource(bytes, "source.gguf");
        var replacement = paths.WriteSource(bytes.Select(static value => (byte)(value ^ 0x5a)).ToArray(), "replacement.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var replaced = false;
        var progress = new InlineProgress<GgufImportProgress>(_ =>
        {
            if (replaced)
            {
                return;
            }

            File.Move(replacement, source, overwrite: true);
            replaced = true;
        });

        var exception = await AssertEx.ThrowsAsync<GgufImportException>(() =>
            importer.PrepareAsync(new GgufImportSource(source), Destination(), progress, CancellationToken.None));

        AssertEx.Equal(GgufImportRejectionCode.InvalidSource, exception.Reason);
        AssertEx.False(exception.Message.Contains(paths.Root, StringComparison.Ordinal));
        AssertEx.Equal(expected: 0, Directory.EnumerateFiles(paths.ModelsDirectory, "*.part", SearchOption.TopDirectoryOnly).Count());
    }

    [Test]
    public async Task Importer_AllowsCanonicalQuantOverrideOnlyWhenDetectionIsUnavailable()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(includeFileType: false), "model.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);

        var prepared = await importer.PrepareAsync(new GgufImportSource(source), Destination(), progress: null, CancellationToken.None);
        await importer.DiscardPreparedAsync(prepared, CancellationToken.None);

        var detectedSource = paths.WriteSource(BuildCausalGguf(), "model-Q8_0.gguf");
        var mismatch = await AssertEx.ThrowsAsync<GgufImportException>(() =>
            importer.PrepareAsync(new GgufImportSource(detectedSource), Destination(), progress: null, CancellationToken.None));
        AssertEx.Equal(GgufImportRejectionCode.UnsupportedQuantization, mismatch.Reason);
    }

    [Test]
    public async Task Importer_CommitRejectsCaseOnlyCollisionCreatedAfterPrepare()
    {
        using var paths = new ImportPaths();
        var source = paths.WriteSource(BuildCausalGguf(), "source.gguf");
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var prepared = await importer.PrepareAsync(new GgufImportSource(source), Destination(), progress: null, CancellationToken.None);
        // Sidecar moves first (see CommitAsync), so a case-only collision on the sidecar destination is the one still
        // rejected cleanly before anything is moved.
        var caseCollision = Path.Combine(paths.ModelsDirectory, prepared.Destination.RelativeSidecarPath.ToUpperInvariant());
        await File.WriteAllTextAsync(caseCollision, "existing");

        var exception = await AssertEx.ThrowsAsync<GgufImportException>(() => importer.CommitAsync(prepared, CancellationToken.None));

        AssertEx.Equal(GgufImportRejectionCode.DestinationConflict, exception.Reason);
        AssertEx.Equal("existing", await File.ReadAllTextAsync(caseCollision));
        await importer.DiscardPreparedAsync(prepared, CancellationToken.None);
    }

    [Test]
    public async Task Importer_SanitizesMissingSourceFailure()
    {
        using var paths = new ImportPaths();
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var importer = NewImporter(options, registry);
        var missing = Path.Combine(paths.SourceDirectoryPath, "private-missing.gguf");

        var exception = await AssertEx.ThrowsAsync<GgufImportException>(() =>
            importer.PrepareAsync(new GgufImportSource(missing), Destination(), progress: null, CancellationToken.None));

        AssertEx.False(exception.Message.Contains(paths.Root, StringComparison.Ordinal));
        AssertEx.NotNull(exception.InnerException);
    }

    [Test]
    public async Task Sidecar_RejectsNoncanonicalQuantAndPartialProjectorCombination()
    {
        using var paths = new ImportPaths();
        var weightPath = Path.Combine(paths.ModelsDirectory, "demo-Q4_K_M.gguf");
        await File.WriteAllBytesAsync(weightPath, [1, 2, 3, 4]);
        var hash = await GgufAcquisitionSidecar.ComputeSha256Async(weightPath, CancellationToken.None);
        var fingerprint = GgufMemberFingerprint.Compute(hash, sizeBytes: 4);
        var modelFingerprint = GgufModelContentFingerprint.ComputeV1([
            new GgufModelContentMember("demo-Q4_K_M.gguf", InstalledModelPhysicalMemberRole.Weight, 4, hash, ["Local/Demo:Q4_K_M"])
        ]);
        var entry = new GgufModelRegistryEntry
        {
            ModelName = "Local/Demo:Q4_K_M",
            RepoId = "Local/Demo:Q4_K_M",
            FileName = "demo-Q4_K_M.gguf",
            Quant = "Q4_K_M",
            LocalPath = weightPath,
            SizeBytes = 4,
            Sha256 = hash,
            SourceRevision = $"sha256:{hash}",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat,
            Origin = LocalModelOrigin.Imported,
            SourceDisplayName = "source.gguf",
            MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            ModelContentFingerprint = modelFingerprint
        };
        var revision = GgufRegistryRevision.ComputeV1(entry, paths.ModelsDirectory);
        var sidecar = new GgufAcquisitionMetadata
        {
            SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            RegistryRevision = revision,
            ModelName = entry.ModelName,
            Origin = LocalModelOrigin.Imported,
            LocalFileName = entry.FileName,
            Quantization = "q4_k_m",
            WeightContentSha256 = hash,
            WeightSizeBytes = 4,
            WeightMemberFingerprint = fingerprint,
            SourceDisplayName = "source.gguf",
            AcquiredAtUtc = DateTimeOffset.UnixEpoch,
            RegistryRepoId = entry.RepoId,
            RegistrySourceRevision = entry.SourceRevision,
            Role = GgufRole.Chat,
            ProjectorContentSha256 = hash,
            ModelContentFingerprint = modelFingerprint
        };
        var sidecarPath = weightPath + GgufAcquisitionSidecar.Suffix;
        await GgufAcquisitionSidecar.WriteAsync(sidecarPath, sidecar, CancellationToken.None);

        var valid = await GgufAcquisitionSidecar.ReadValidAsync(sidecarPath, weightPath, paths.ModelsDirectory, CancellationToken.None);

        AssertEx.Null(valid);
    }

    [Test]
    public void Fingerprints_PreserveCaseOnlyOwningAliases_WithDeterministicOrdering()
    {
        var hash = new string('1', 64);
        var withBoth = GgufModelContentFingerprint.ComputeV1([
            new GgufModelContentMember("demo.gguf", InstalledModelPhysicalMemberRole.Weight, 4, hash, ["Alias", "alias"])
        ]);
        var reversed = GgufModelContentFingerprint.ComputeV1([
            new GgufModelContentMember("demo.gguf", InstalledModelPhysicalMemberRole.Weight, 4, hash, ["alias", "Alias"])
        ]);
        var collapsed = GgufModelContentFingerprint.ComputeV1([
            new GgufModelContentMember("demo.gguf", InstalledModelPhysicalMemberRole.Weight, 4, hash, ["Alias"])
        ]);

        AssertEx.Equal(withBoth, reversed);
        AssertEx.NotEqual(withBoth, collapsed);
    }

    [Test]
    public async Task Snapshot_LegacyProjectorWithoutRecordedFacts_ComputesFreshFacts()
    {
        using var paths = new ImportPaths();
        var weightPath = Path.Combine(paths.ModelsDirectory, "legacy-Q4_K_M.gguf");
        var projectorPath = Path.Combine(paths.ModelsDirectory, "legacy.mmproj.gguf");
        await File.WriteAllBytesAsync(weightPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(projectorPath, [5, 6, 7]);
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        var entry = new GgufModelRegistryEntry
        {
            ModelName = "Legacy:Q4_K_M",
            RepoId = "legacy",
            FileName = Path.GetFileName(weightPath),
            Quant = "Q4_K_M",
            LocalPath = weightPath,
            SizeBytes = 4,
            Sha256 = null,
            SourceRevision = string.Empty,
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat,
            ProjectorFileName = Path.GetFileName(projectorPath),
            ProjectorLocalPath = projectorPath,
            ProjectorSizeBytes = null,
            ProjectorSha256 = null
        };
        await registry.UpsertAsync(entry, CancellationToken.None);
        var snapshots = new InstalledGgufSnapshotStore(registry, options);
        var candidate = await snapshots.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None);

        var snapshot = await snapshots.LoadVerifiedAsync(entry.ModelName, candidate!, CancellationToken.None);

        AssertEx.ContainsSingle(snapshot.Members, static member => member.Role == InstalledModelPhysicalMemberRole.Projector
                                                                   && member.SizeBytes == 3
                                                                   && member.MemberFingerprint is not null);
    }

    [Test]
    public async Task Sidecar_NestedWeightPath_UsesSameRelativePathForAggregateAndSnapshot()
    {
        using var paths = new ImportPaths();
        var nestedDirectory = Path.Combine(paths.ModelsDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var weightPath = Path.Combine(nestedDirectory, "demo-Q4_K_M.gguf");
        await File.WriteAllBytesAsync(weightPath, [1, 2, 3, 4]);
        var hash = await GgufAcquisitionSidecar.ComputeSha256Async(weightPath, CancellationToken.None);
        const string modelName = "Nested/Demo:Q4_K_M";
        const string relativePath = "nested/demo-Q4_K_M.gguf";
        var modelFingerprint = GgufModelContentFingerprint.ComputeV1([
            new GgufModelContentMember(relativePath, InstalledModelPhysicalMemberRole.Weight, 4, hash, [modelName])
        ]);
        var entry = new GgufModelRegistryEntry
        {
            ModelName = modelName,
            RepoId = "org/repo",
            FileName = Path.GetFileName(weightPath),
            Quant = "Q4_K_M",
            LocalPath = weightPath,
            SizeBytes = 4,
            Sha256 = hash,
            SourceRevision = "revision",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat,
            Origin = LocalModelOrigin.HuggingFace,
            SourceDisplayName = Path.GetFileName(weightPath),
            MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            ModelContentFingerprint = modelFingerprint
        };
        entry = entry with
        {
            RegistryRevision = GgufRegistryRevision.ComputeV1(entry, paths.ModelsDirectory)
        };
        var sidecar = AcquisitionMetadata(entry, hash, modelFingerprint);
        await GgufAcquisitionSidecar.WriteAsync(weightPath + GgufAcquisitionSidecar.Suffix, sidecar, CancellationToken.None);
        AssertEx.NotNull(await GgufAcquisitionSidecar.ReadValidAsync(weightPath + GgufAcquisitionSidecar.Suffix,
            weightPath,
            paths.ModelsDirectory,
            CancellationToken.None));
        var options = Infra.Options(paths.ModelsDirectory);
        using var registry = Infra.Registry(options);
        await registry.InsertIfAbsentAsync(entry, CancellationToken.None);
        var snapshots = new InstalledGgufSnapshotStore(registry, options);
        var candidate = await snapshots.DiscoverCandidateAsync(modelName, CancellationToken.None);

        var snapshot = await snapshots.LoadVerifiedAsync(modelName, candidate!, CancellationToken.None);

        AssertEx.Equal(modelFingerprint, snapshot.ModelContentFingerprint);
        AssertEx.ContainsSingle(snapshot.Members, member => member.RelativePath == relativePath);
    }

    private static GgufAcquisitionMetadata AcquisitionMetadata(GgufModelRegistryEntry entry,
        string hash,
        string modelFingerprint)
    {
        return new GgufAcquisitionMetadata
        {
            SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            RegistryRevision = entry.RegistryRevision!,
            ModelName = entry.ModelName,
            Origin = entry.Origin!.Value,
            LocalFileName = entry.FileName,
            Quantization = entry.Quant,
            WeightContentSha256 = hash,
            WeightSizeBytes = entry.SizeBytes,
            WeightMemberFingerprint = GgufMemberFingerprint.Compute(hash, entry.SizeBytes),
            SourceDisplayName = entry.SourceDisplayName!,
            AcquiredAtUtc = entry.DownloadedAtUtc,
            RegistryRepoId = entry.RepoId,
            RegistrySourceRevision = entry.SourceRevision,
            Role = entry.Role,
            ModelContentFingerprint = modelFingerprint
        };
    }

    private static GgufModelImporter NewImporter(HuggingFaceOptions options,
        GgufModelRegistry registry)
    {
        return new GgufModelImporter(registry, Infra.AbundantSpace(), options, TimeProvider.System);
    }

    private static GgufImportDestination Destination()
    {
        return new GgufImportDestination("Local/Demo:Q4_K_M",
            "Q4_K_M",
            "local-demo-q4_k_m-0123456789abcdef01234567.gguf",
            "local-demo-q4_k_m-0123456789abcdef01234567.gguf.xe-model.json",
            LocalModelOrigin.Imported);
    }

    // A supported causal model that additionally carries a chat template, so the import-time reasoning-budget
    // classification has something to read.
    private static byte[] BuildReasoningGguf(string chatTemplate)
    {
        return new GgufHeaderBytesBuilder()
               .WithString("general.architecture", "qwen3")
               .WithString("general.type", "model")
               .WithString("tokenizer.chat_template", chatTemplate)
               .WithUint32("general.file_type", value: 15)
               .Build();
    }

    private static byte[] BuildCausalGguf(string architecture = "llama", bool includeFileType = true)
    {
        var builder = new GgufHeaderBytesBuilder()
                      .WithString("general.architecture", architecture)
                      .WithString("general.type", "model");
        if (includeFileType)
        {
            builder.WithUint32("general.file_type", value: 15);
        }

        return builder.Build();
    }

    // A LoRA adapter: same architecture as the base it was trained against, distinguished only by general.type.
    private static byte[] BuildAdapterGguf(string architecture = "llama")
    {
        return new GgufHeaderBytesBuilder()
               .WithString("general.architecture", architecture)
               .WithString("general.type", "adapter")
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
        public void Report(T value) =>
            report(value);
    }

    private sealed class ImportPaths : IDisposable
    {
        public ImportPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), "xe-import-foundation-" + Guid.NewGuid().ToString("N"));
            ModelsDirectory = Path.Combine(Root, "models");
            SourceDirectoryPath = Path.Combine(Root, "sources");
            Directory.CreateDirectory(ModelsDirectory);
            Directory.CreateDirectory(SourceDirectoryPath);
        }

        public string Root { get; }
        public string ModelsDirectory { get; }
        public string SourceDirectoryPath { get; }

        public string WriteSource(byte[] bytes, string fileName)
        {
            var path = Path.Combine(SourceDirectoryPath, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException)
            {
                // Best-effort fixture cleanup; a locked file must not mask the assertion result.
            }
        }
    }
}
