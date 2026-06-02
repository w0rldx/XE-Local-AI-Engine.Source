namespace XE_Local_AI_Engine.Client.Services.Connection;

public sealed record CertificatePin(string Sha256Thumbprint, DateTimeOffset PinnedAtUtc, string SubjectCommonName);
