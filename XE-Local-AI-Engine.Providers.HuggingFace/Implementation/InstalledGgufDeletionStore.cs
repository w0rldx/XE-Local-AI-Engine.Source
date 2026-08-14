namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

internal sealed class InstalledGgufDeletionStore(GgufModelRegistry registry, HuggingFaceOptions options) : IInstalledGgufDeletionStore
{
    public async Task<GgufDeletionStageReceipt> StageAsync(InstalledGgufSnapshot snapshot,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var receipt = GgufDeletionStageReceipt.Create(snapshot, operationId);
        ValidateReceipt(receipt);
        foreach (var retained in receipt.RetainedMembers)
        {
            await VerifyMemberAsync(retained, cancellationToken).ConfigureAwait(false);
        }

        var moved = new List<GgufDeletionStagedMember>();
        try
        {
            foreach (var staged in receipt.StagedMembers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await VerifyMemberAsync(staged.Member, cancellationToken).ConfigureAwait(false);
                var original = Resolve(staged.OriginalRelativePath);
                var quarantine = Resolve(staged.QuarantineRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
                File.Move(original, quarantine, overwrite: false);
                moved.Add(staged);
            }

            return receipt;
        }
        catch
        {
            foreach (var staged in moved.AsEnumerable().Reverse())
            {
                var original = Resolve(staged.OriginalRelativePath);
                var quarantine = Resolve(staged.QuarantineRelativePath);
                if (!File.Exists(original) && File.Exists(quarantine))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Move(quarantine, original, overwrite: false);
                }
            }

            throw;
        }
    }

    public async Task<GgufRegistryAliasMutationReceipt> RemoveAliasesByLocalPathAsync(
        GgufDeletionStageReceipt stageReceipt,
        IReadOnlyList<InstalledModelRegistryAliasSnapshot> expectedAliases,
        CancellationToken cancellationToken)
    {
        ValidateReceipt(stageReceipt);
        ArgumentNullException.ThrowIfNull(expectedAliases);
        if (!expectedAliases.SequenceEqual(stageReceipt.RemovalAliases))
        {
            throw new InvalidOperationException("The complete expected registry alias set changed before deletion.");
        }

        var entries = expectedAliases.Select(ToRegistryEntry).ToArray();
        var removed = await registry.RemoveAliasSetIfMatchAsync(entries, cancellationToken).ConfigureAwait(false);
        if (removed is null)
        {
            throw new InvalidOperationException("InstalledModelRegistrySuperseded");
        }

        return new GgufRegistryAliasMutationReceipt(
            Array.AsReadOnly(expectedAliases.Select(static alias => alias with { }).ToArray()),
            GgufRegistryAliasSetHash.ComputeV1(expectedAliases),
            GgufRegistryAliasSetHash.ComputeV1([]));
    }

    public async Task RestoreAsync(GgufDeletionStageReceipt stageReceipt,
        GgufRegistryAliasMutationReceipt? registryAliasReceipt,
        CancellationToken cancellationToken)
    {
        ValidateReceipt(stageReceipt);
        var aliases = registryAliasReceipt?.RemovedAliases ?? stageReceipt.RemovalAliases;
        if (!await registry.RestoreAliasSetIfMatchAsync(aliases.Select(ToRegistryEntry).ToArray(), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("InstalledModelRegistrySuperseded");
        }

        foreach (var staged in stageReceipt.StagedMembers.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = Resolve(staged.OriginalRelativePath);
            var quarantine = Resolve(staged.QuarantineRelativePath);
            var originalExists = File.Exists(original);
            var quarantineExists = File.Exists(quarantine);
            if (originalExists && quarantineExists)
            {
                throw new IOException("A staged model member cannot be restored because both paths are occupied.");
            }

            if (quarantineExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Move(quarantine, original, overwrite: false);
            }

            await VerifyMemberAsync(staged.Member, cancellationToken).ConfigureAwait(false);
        }

        foreach (var retained in stageReceipt.RetainedMembers)
        {
            await VerifyMemberAsync(retained, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task PurgeAsync(GgufDeletionStageReceipt stageReceipt, CancellationToken cancellationToken)
    {
        ValidateReceipt(stageReceipt);
        foreach (var staged in stageReceipt.StagedMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quarantine = Resolve(staged.QuarantineRelativePath);
            if (File.Exists(quarantine))
            {
                File.Delete(quarantine);
            }
        }

        var operationDirectory = Resolve($".operations/delete/{stageReceipt.OperationId:N}");
        DeleteEmptyTree(operationDirectory);
        return Task.CompletedTask;
    }

    private async Task VerifyMemberAsync(InstalledModelPhysicalMember member, CancellationToken cancellationToken)
    {
        var path = Resolve(member.RelativePath);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length != member.SizeBytes)
        {
            throw new IOException("An installed model member is unavailable or changed.");
        }

        var hash = await GgufAcquisitionSidecar.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hash, member.Sha256, StringComparison.Ordinal))
        {
            throw new IOException("An installed model member is unavailable or changed.");
        }
    }

    private GgufModelRegistryEntry ToRegistryEntry(InstalledModelRegistryAliasSnapshot alias)
    {
        var value = alias.RegistryValue;
        return new GgufModelRegistryEntry
        {
            RegistryRevision = alias.RegistryRevision,
            Origin = value.Origin,
            ModelName = alias.ModelName,
            RepoId = value.RepoId,
            FileName = value.FileName,
            Quant = value.Quant,
            LocalPath = Resolve(value.WeightRelativePath),
            SizeBytes = value.SizeBytes,
            Sha256 = value.Sha256,
            SourceRevision = value.SourceRevision,
            DownloadedAtUtc = value.DownloadedAtUtc,
            Role = value.Role,
            ProjectorFileName = value.ProjectorFileName,
            ProjectorLocalPath = value.ProjectorRelativePath is null ? null : Resolve(value.ProjectorRelativePath),
            ProjectorSizeBytes = value.ProjectorSizeBytes,
            ProjectorSha256 = value.ProjectorSha256,
            SourceDisplayName = value.SourceDisplayName,
            MetadataSchemaVersion = value.MetadataSchemaVersion,
            ModelContentFingerprint = value.ModelContentFingerprint
        };
    }

    private string Resolve(string relativePath) => GgufFilePath.ResolveContainedPath(options.ModelsDirectory, relativePath);

    private static void ValidateReceipt(GgufDeletionStageReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.Equals(receipt.RegistryAliasSetHash, GgufRegistryAliasSetHash.ComputeV1(receipt.RemovalAliases), StringComparison.Ordinal)
            || receipt.StagedMembers.Select(static member => member.OriginalRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != receipt.StagedMembers.Count)
        {
            throw new ArgumentException("The deletion stage receipt is invalid.", nameof(receipt));
        }
    }

    private static void DeleteEmptyTree(string path)
    {
        var current = path;
        while (Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
            if (!Path.GetFileName(current).Equals("delete", StringComparison.Ordinal))
            {
                continue;
            }

            break;
        }
    }
}
