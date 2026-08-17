namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>A file this operation created and may therefore delete when compensating. Artifacts it did not create are never touched.</summary>
internal sealed record OwnedArtifact(string Path, bool Owned);

/// <summary>
///     Compensating deletion of the artifacts an import or a download created. Shared by both transactions because a
///     rollback that deletes a pre-existing file the operation did not create destroys operator data, so the ownership
///     rule must be one implementation rather than a copy per transaction.
/// </summary>
internal static class OwnedArtifactCleanup
{
    /// <summary>
    ///     Deletes every owned artifact, continuing past failures so one unremovable file does not strand the rest.
    ///     Returns the aggregated failure, or <see langword="null" /> when everything owned was removed.
    /// </summary>
    public static Exception? TryDeleteAll(params OwnedArtifact[] artifacts)
    {
        List<Exception>? failures = null;
        foreach (var artifact in artifacts)
        {
            try
            {
                Delete(artifact);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures is null ? null : new AggregateException(failures);
    }

    /// <summary>
    ///     As <see cref="TryDeleteAll" />, but throws when anything owned survives. <paramref name="ownership" /> names
    ///     the pipeline that owns the artifacts ("import" / "download") and is carried in the message, so the surviving
    ///     inner exception still identifies which transaction failed to compensate.
    /// </summary>
    /// <exception cref="IOException">At least one owned artifact could not be removed.</exception>
    public static void DeleteAll(string ownership, params OwnedArtifact[] artifacts)
    {
        var failure = TryDeleteAll(artifacts);
        if (failure is not null)
        {
            throw new IOException($"One or more {ownership}-owned artifacts could not be removed.", failure);
        }
    }

    private static void Delete(OwnedArtifact artifact)
    {
        if (!artifact.Owned)
        {
            return;
        }

        File.Delete(artifact.Path);
        if (File.Exists(artifact.Path) || Directory.Exists(artifact.Path))
        {
            throw new IOException("An owned artifact could not be removed.");
        }
    }
}
