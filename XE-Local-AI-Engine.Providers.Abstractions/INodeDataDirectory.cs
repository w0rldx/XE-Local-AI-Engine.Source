namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The single source of truth for the directory under which this node persists its per-node runtime state
///     (node settings, the encrypted credential stores, cert pins, the AgentHome workspace, the hardware-profile cache).
/// </summary>
/// <remarks>
///     In a self-contained desktop launch <c>DesktopBootstrap</c> points this at the per-user data directory
///     (Windows <c>%LOCALAPPDATA%\XE-Local-AI-Engine</c>; Linux <c>$XDG_DATA_HOME/XE-Local-AI-Engine</c>) so a single-file
///     exe — whose <c>AppContext.BaseDirectory</c> is a volatile bundle-extraction temp and whose install dir is shared and
///     read-only-prone — keeps its state between runs and never ships it. In every other host (headless / Aspire / CI)
///     <see cref="Root" /> resolves to <c>IHostEnvironment.ContentRootPath</c>, so behavior is byte-identical off the
///     desktop flag. This interface lives in the lowest shared layer so both the Application services and the
///     <c>Providers.CodexOAuth</c> token store can consume it.
/// </remarks>
public interface INodeDataDirectory
{
    /// <summary>The absolute directory under which per-node runtime state is read and written.</summary>
    string Root { get; }
}
