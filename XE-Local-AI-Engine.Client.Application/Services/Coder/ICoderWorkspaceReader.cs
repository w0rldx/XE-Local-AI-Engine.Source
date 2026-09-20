namespace XE_Local_AI_Engine.Client.Services.Coder;

using XE_Local_AI_Engine.Client.Services.Coder.Tools;

/// <summary>The single read-only gateway the three coder tool handlers delegate to, analogous to <c>IAgentHomeToolGateway</c>.</summary>
/// <remarks>
///     It attaches to the live AgentHome sandbox through <c>ISandboxRuntimeProvider.ConnectAsync</c>, which does NOT take the AgentHome run
///     lock, so a coder read never throws <c>AgentHomeBusyException</c> during an in-flight run. Every model path is confined through
///     <see cref="WorkspacePathGuard" />, secrets are excluded, and model-facing strings carry workspace-relative paths only, never a
///     host-absolute one. No write, patch, mutating or caller-supplied-executable path exists.
/// </remarks>
internal interface ICoderWorkspaceReader
{
    /// <summary>Lists workspace entries under the (confined) request path, secrets excluded and count-capped.</summary>
    Task<string> ListFilesAsync(ListFilesToolRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a (confined) workspace file through the jail-guarded read, with binary refusal and caps applied.</summary>
    Task<string> ReadFileAsync(ReadFileToolRequest request, CancellationToken cancellationToken = default);

    /// <summary>Searches the (confined) workspace for the pattern, secrets excluded at grep level and post-filtered.</summary>
    Task<string> SearchTextAsync(SearchTextToolRequest request, CancellationToken cancellationToken = default);
}
