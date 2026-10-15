namespace XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>The custom tools flagged for unattended use, for the graph action branch of the tool invocation service (ADR 0021).</summary>
/// <remarks>
///     Deliberately NOT on <c>ICustomToolCatalog</c>: that surface hands out only approval-wrapped executables, and
///     this one hands out the executor itself, so only the caller that writes the strict audit row first may hold it.
/// </remarks>
internal interface IUnattendedCustomToolCatalog
{
    /// <summary>
    ///     Every enabled, acknowledged, <c>Fixed</c>-mode tool with <c>AllowUnattended</c>, read live from the store; empty
    ///     without a store read while custom tools are off on this node.
    /// </summary>
    Task<IReadOnlyList<UnattendedCustomTool>> ListUnattendedAsync(CancellationToken cancellationToken = default);
}
