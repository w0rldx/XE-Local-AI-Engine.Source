namespace XE_Local_AI_Engine.Client.Services.Connection;

using System.Security.Cryptography.X509Certificates;

/// <summary>
///     Persistence boundary for i cert pin data.
/// </summary>
public interface ICertPinStore
{
    Task<CertificatePin?> GetPinAsync(CancellationToken cancellationToken = default);

    Task SavePinAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default);

    Task<bool> MatchesAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default);

    Task ClearPinAsync(CancellationToken cancellationToken = default);
}
