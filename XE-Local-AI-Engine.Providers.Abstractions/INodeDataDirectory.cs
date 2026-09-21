namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The single source of truth for the directory under which this node persists its per-node runtime state
///     (node settings, the encrypted credential stores, cert pins, the AgentHome workspace, the hardware-profile cache).
/// </summary>
/// <remarks>
///     <c>DesktopBootstrap</c> points a desktop launch at the per-user data directory (Windows <c>%LOCALAPPDATA%\XE-Local-AI-Engine</c>;
///     Linux <c>$XDG_DATA_HOME/XE-Local-AI-Engine</c>): a single-file exe's <c>AppContext.BaseDirectory</c> is a volatile
///     bundle-extraction temp and its install dir shared and read-only-prone, so state kept there neither survives a run nor stays
///     unshipped. Every other host (headless / Aspire / CI) resolves <see cref="Root" /> to <c>IHostEnvironment.ContentRootPath</c>,
///     byte-identical off the desktop flag. Lowest shared layer, so the Application services and <c>Providers.CodexOAuth</c> share it.
/// </remarks>
public interface INodeDataDirectory
{
    /// <summary>The absolute directory under which per-node runtime state is read and written.</summary>
    string Root { get; }
}
