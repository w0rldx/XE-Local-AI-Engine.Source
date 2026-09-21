namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Resolves a validated managed whisper.cpp runtime when one is selected; otherwise downloads, hash-verifies and
///     caches the exact pinned <c>whisper-server</c> prebuilt for the host and backend.
/// </summary>
/// <remarks>
///     Source compilation itself is the source-build service's job, not this one's. Resolution order: operator
///     bring-your-own override, then the authoritative installed managed-runtime record,
///     then the exact pinned release asset for the OS/arch and requested <see cref="WhisperBackend" /> — downloaded,
///     verified against the pinned SHA256 (a corrupt download is discarded and retried once, then surfaced sanitized),
///     extracted under a stable cache directory, and reused offline from there afterwards.
/// </remarks>
public interface IWhisperCppBinaryManager
{
    /// <summary>
    ///     Ensures a validated <c>whisper-server</c> binary for <paramref name="backend" /> is present on disk and
    ///     returns its resolved location. Idempotent: a valid managed runtime or a cached prebuilt is reused.
    /// </summary>
    /// <exception cref="WhisperRuntimeException">
    ///     The binary could not be acquired — a download failure, a repeated hash mismatch, no prebuilt for this host,
    ///     a tombstoned managed record, or a broken bring-your-own override. The message is sanitized for display.
    /// </exception>
    Task<WhisperBinary> EnsureBinaryAsync(WhisperBackend backend, CancellationToken ct);
}
