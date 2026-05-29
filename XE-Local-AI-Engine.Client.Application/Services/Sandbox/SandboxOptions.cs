namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration-bound selection of the AgentHome <see cref="ISandboxRuntimeProvider" /> (AgentHome plan §6.2,
///     "configuration-bound and restart-required for the first pass"). The provider is resolved once at startup as a
///     singleton, so changing <see cref="Provider" /> requires a restart. The MVP default is <c>"fake"</c>; Marker
///     J-local adds <c>"local-container"</c> and flips the default. Bound from the <c>AgentHome:Sandbox</c> section.
/// </summary>
public sealed class SandboxOptions
{
    public const string SectionName = "AgentHome:Sandbox";

    /// <summary>Selected provider name. Must match an <see cref="ISandboxRuntimeProvider.ProviderName" /> known to DI.</summary>
    public string Provider { get; set; } = "fake";
}
