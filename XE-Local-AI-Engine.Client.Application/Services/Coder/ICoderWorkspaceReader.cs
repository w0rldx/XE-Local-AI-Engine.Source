namespace XE_Local_AI_Engine.Client.Services.Coder;

using XE_Local_AI_Engine.Client.Services.Coder.Tools;

/// <summary>
///     The single read-only gateway the three coder tool handlers delegate to — analogous to
///     <c>IAgentHomeToolGateway</c> but read-only. It attaches to the live AgentHome sandbox via
///     <c>ISandboxRuntimeProvider.ConnectAsync</c> (which does NOT take the AgentHome run lock, so a coder read never
///     throws <c>AgentHomeBusyException</c> during an in-flight run), confines every model path through
///     <see cref="WorkspacePathGuard" />, excludes secrets, and returns model-facing strings carrying workspace-relative
///     paths only — never a host-absolute path. No write, patch, mutating, or caller-supplied-executable path exists.
/// </summary>
internal interface ICoderWorkspaceReader
{
    /// <summary>Lists workspace entries under the (confined) request path, secrets excluded and count-capped.</summary>
    Task<string> ListFilesAsync(ListFilesToolRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a (confined) workspace file through the jail-guarded read, with binary refusal and caps applied.</summary>
    Task<string> ReadFileAsync(ReadFileToolRequest request, CancellationToken cancellationToken = default);

    /// <summary>Searches the (confined) workspace for the pattern, secrets excluded at grep level and post-filtered.</summary>
    Task<string> SearchTextAsync(SearchTextToolRequest request, CancellationToken cancellationToken = default);
}
