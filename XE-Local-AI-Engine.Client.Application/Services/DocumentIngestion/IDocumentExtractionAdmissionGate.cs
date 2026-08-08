namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     Bounds how many synchronous, in-request document extractions run at once. Each extraction buffers a whole upload
///     (up to the per-file cap) in memory and parses it, so unbounded concurrent uploads can aggregate to an
///     out-of-memory condition even when every single file is within its size cap. The conversation upload endpoint
///     acquires admission before extracting and rejects with a busy status when the gate is full.
/// </summary>
public interface IDocumentExtractionAdmissionGate
{
    /// <summary>
    ///     Tries to admit one extraction without waiting. Returns <see langword="true"/> with a lease that MUST be
    ///     disposed when the extraction finishes (releasing the slot), or <see langword="false"/> when the gate is at
    ///     capacity and the caller should reject the request.
    /// </summary>
    bool TryAcquire([NotNullWhen(true)] out IDisposable? lease);
}
