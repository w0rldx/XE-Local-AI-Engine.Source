namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The last-computed llama.cpp runtime update snapshot, shared between the one-shot startup check
///     (<c>LlamaCppUpdateCheckService</c>) and the read-only runtime-status endpoint.
/// </summary>
/// <remarks>
///     It holds the installed tag, the recommended tag, the optional upstream-latest tag and whether a newer
///     recommended runtime is available, so the status endpoint answers "is there an update?" without re-hitting the
///     live catalog on every poll.
/// </remarks>
public sealed class LlamaCppUpdateSnapshot
{
    /// <summary>The currently-installed release tag, or <see langword="null" /> on a fresh node.</summary>
    public required string? InstalledTag { get; init; }

    /// <summary>The resolved recommended release tag, or <see langword="null" /> when unresolved.</summary>
    public required string? RecommendedTag { get; init; }

    /// <summary>The true upstream latest tag (developer mode), or <see langword="null" /> when not resolved.</summary>
    public required string? UpstreamLatestTag { get; init; }

    /// <summary><see langword="true" /> only when a newer recommended tag is resolvable AND it differs from the installed tag.</summary>
    public required bool UpdateAvailable { get; init; }

    /// <summary><see langword="true" /> when the live catalog was unreachable/rate-limited at the time of the snapshot.</summary>
    public required bool IsOffline { get; init; }

    /// <summary>When the snapshot was computed (UTC), or <see langword="null" /> before the first check.</summary>
    public required DateTimeOffset? CheckedAtUtc { get; init; }

    /// <summary>The empty pre-check snapshot: nothing resolved yet, no update advertised, not flagged offline.</summary>
    public static LlamaCppUpdateSnapshot Empty { get; } =
        new() { InstalledTag = null, RecommendedTag = null, UpstreamLatestTag = null, UpdateAvailable = false, IsOffline = false, CheckedAtUtc = null };
}

/// <summary>
///     Thread-safe holder for the latest <see cref="LlamaCppUpdateSnapshot" />. Registered as a singleton so the startup
///     check writes it once and the runtime-status endpoint reads it on demand. The update endpoint also refreshes it
///     after a successful install.
/// </summary>
public interface ILlamaCppUpdateState
{
    /// <summary>The current snapshot (never <see langword="null" />; defaults to <see cref="LlamaCppUpdateSnapshot.Empty" />).</summary>
    LlamaCppUpdateSnapshot Current { get; }

    /// <summary>Atomically replaces the current snapshot with <paramref name="snapshot" />.</summary>
    void Store(LlamaCppUpdateSnapshot snapshot);
}

/// <summary>
///     Default in-memory <see cref="ILlamaCppUpdateState" />. A single reference field swapped under <c>Volatile</c>
///     read/write — reads are lock-free and writes are atomic (reference assignment), so the startup writer and endpoint
///     readers never tear a snapshot.
/// </summary>
public sealed class LlamaCppUpdateState : ILlamaCppUpdateState
{
    private LlamaCppUpdateSnapshot _current = LlamaCppUpdateSnapshot.Empty;

    public LlamaCppUpdateSnapshot Current => Volatile.Read(ref _current);

    public void Store(LlamaCppUpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
