namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     A local model to classify: its name plus the content digest the caller last saw, which is what makes a cached
///     classification stale when the two differ. A <see langword="null" /> digest means "unknown", never "unchanged".
/// </summary>
public sealed record ModelIdentity(string ModelName, string? Digest);
