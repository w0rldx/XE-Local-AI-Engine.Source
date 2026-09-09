namespace XE_Local_AI_Engine.Client.Services.Connection;

using System.Security.Cryptography.X509Certificates;

/// <summary>
///     Persistence boundary for the pinned worker-node certificate: the stored pin, its comparison against a
///     presented certificate, and its lifecycle.
/// </summary>
/// <remarks>
///     Deliberately pre-positioned. The implementation is complete, DI-registered in
///     <c>AddNodeModelRuntimeExtensions</c> and covered by <c>CertPinStoreTests</c>, but no production code path
///     resolves it: nothing in the worker-hub connection or its TLS handling consults a pin. The absent call site
///     is a known and accepted state, not a missing reference, and this type is kept by decision rather than by
///     oversight.
/// </remarks>
public interface ICertPinStore
{
    Task<CertificatePin?> GetPinAsync(CancellationToken cancellationToken = default);

    Task SavePinAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default);

    Task<bool> MatchesAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default);

    Task ClearPinAsync(CancellationToken cancellationToken = default);
}
