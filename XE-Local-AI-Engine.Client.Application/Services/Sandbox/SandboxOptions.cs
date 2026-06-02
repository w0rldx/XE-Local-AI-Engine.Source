namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration-bound selection of the AgentHome <see cref="ISandboxRuntimeProvider" />. The provider is resolved
///     once at startup as a singleton, so changing <see cref="Provider" /> requires a restart. The default is
///     <c>"fake"</c>; the local-container sandbox provider adds <c>"local-container"</c>. Bound from the
///     <c>AgentHome:Sandbox</c> section.
/// </summary>
public sealed class SandboxOptions
{
    public const string SectionName = "AgentHome:Sandbox";

    /// <summary>Selected provider name. Must match an <see cref="ISandboxRuntimeProvider.ProviderName" /> known to DI.</summary>
    public string Provider { get; set; } = "fake";
}
