namespace XE_Local_AI_Engine.Client.Services.Auth;

using NSec.Cryptography;

public sealed record NodeKeyResolution
{
    public required string RequestedKeyId { get; init; }

    public required NodeKeyLookupStatus Status { get; init; }

    public string? KeyIdUsed { get; init; }

    public Key? PrivateKey { get; init; }

    public PublicKey? PublicKey { get; init; }

    public bool IsResolved => Status is NodeKeyLookupStatus.Active or NodeKeyLookupStatus.Retired;
}
