namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

/// <summary>One installed-model inventory entry resolved by <see cref="ModelCapabilityProber" />.</summary>
/// <param name="Name">Normalized model name/tag.</param>
/// <param name="Digest">Content digest when discovered from the runtime; <c>null</c> for configured fallbacks.</param>
/// <param name="IsDiscovered">True when the runtime reported the model; false for configured-name fallbacks.</param>
internal sealed record InstalledModelInfo(string Name, string? Digest, bool IsDiscovered);
