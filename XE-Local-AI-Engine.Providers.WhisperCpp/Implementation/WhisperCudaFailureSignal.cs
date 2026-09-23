namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

/// <summary>
///     Process-wide latch: the pinned CUDA <c>whisper-server</c> the vendor rule selected has exited unexpectedly, so
///     backend selection serves CPU until the node restarts.
/// </summary>
/// <remarks>
///     The <c>nvcuda.dll</c> probe only catches a missing driver. A driver that is present but enumerates zero devices still gets the
///     cuBLAS build, which reports ready and dies on its first request; without this latch every respawn picks CUDA again. Set by the
///     supervisor only for the pinned prebuilt, never for a bring-your-own or managed source build, which are the operator's explicit
///     choice. Never cleared: a restart is the retry.
/// </remarks>
public sealed class WhisperCudaFailureSignal
{
    private string? _reason;

    /// <summary>Why CUDA was abandoned, or <see langword="null" /> while it has not been.</summary>
    public string? Reason => Volatile.Read(ref _reason);

    /// <summary>Latches the failure; the first reason wins.</summary>
    public void Set(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Interlocked.CompareExchange(ref _reason, reason, comparand: null);
    }
}
