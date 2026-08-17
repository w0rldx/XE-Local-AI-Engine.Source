namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>A freshly minted access token together with the absolute UTC instant it stops being accepted.</summary>
public sealed record AccessTokenResult(string AccessToken, DateTime ExpiresAtUtc);
