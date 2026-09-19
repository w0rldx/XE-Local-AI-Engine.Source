namespace XE_Local_AI_Engine.Client.Services.Connection;

public sealed class CertificatePin
{
    public required string Sha256Thumbprint { get; init; }

    public required DateTimeOffset PinnedAtUtc { get; init; }

    public required string SubjectCommonName { get; init; }
}
