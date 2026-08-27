namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

/// <summary>
///     The one write path for external connections: the encrypted store plus every side effect a save owes the rest of
///     the node.
/// </summary>
/// <remarks>
///     Endpoints call THIS, never <see cref="IExternalProviderStore" /> directly. A bare store write would leave the
///     model unroutable (no provider-map row), possibly not tool-capable (no allow-list entry), and — after an API-key
///     edit — still being sent to with the previous key from a cached chat client.
/// </remarks>
public interface IExternalProviderAdministrationService
{
    /// <summary>
    ///     Saves one connection and reconciles the derived state. On
    ///     <see cref="ExternalProviderWriteResult.Superseded" /> nothing is reconciled, because nothing was written.
    /// </summary>
    /// <exception cref="ExternalProviderValidationException">The request violates the stored shape's contract.</exception>
    Task<ExternalProviderWriteResult> SaveConnectionAsync(ExternalProviderConnectionSaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes one connection, its models' routing, its allow-list entries, and — when it was selected — the node default.</summary>
    Task<ExternalProviderWriteResult> DeleteConnectionAsync(string connectionId, string? expectedRevision, CancellationToken cancellationToken = default);
}
