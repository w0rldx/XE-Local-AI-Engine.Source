namespace XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Value object carrying certificate pin data.
/// </summary>
public sealed record CertificatePin(string Sha256Thumbprint, DateTimeOffset PinnedAtUtc, string SubjectCommonName);
