namespace XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     Why a single named-tool invocation ended the way it did. <see cref="Executed" /> = ran in-process.
///     <see cref="UnknownTool" /> = no catalog entry. <see cref="NotInvocable" /> = the tool exists but fails the
///     Tool-node envelope (<c>ToolCategory.ReadLocal</c> AND a composed approval of <see langword="false" />) or has no
///     in-process executable. <see cref="InvalidArguments" /> = not a JSON object, or fails the tool's own schema.
///     <see cref="Timeout" />, <see cref="Cancelled" /> and <see cref="Faulted" /> are self-describing.
/// </summary>
public enum ToolInvocationOutcomeKind
{
    Executed,
    UnknownTool,
    NotInvocable,
    InvalidArguments,
    Timeout,
    Cancelled,
    Faulted
}

/// <summary>One invocation's verdict. <see cref="Reason" /> is structural and never echoes an argument value.</summary>
public sealed record ToolInvocationOutcome(ToolInvocationOutcomeKind Kind, string? Result, string Reason);

/// <summary>A tool this service would actually invoke, with the raw JSON-schema text it validates against.</summary>
public sealed record InvocableToolDescriptor(string Name, string Description, string ParameterSchema);

/// <summary>The calling node run, for logging and correlation.</summary>
/// <param name="Timeout">Hard budget for the whole call, including argument validation.</param>
public sealed record ToolInvocationContext(Guid RunId, Guid NodeRunId, string NodeKey, TimeSpan Timeout);

/// <summary>
///     Invokes ONE named engine tool in-process, enforcing the whole invocation envelope inside itself so no caller can
///     skip a gate. Promoted from <c>HeadlessToolExecutor</c>'s enforcement pattern, with two differences: it consults
///     the model-agnostic catalog and BOTH executable registries (so the worker-owned read tools are reachable), and
///     anything outside the envelope is refused rather than mocked.
/// </summary>
public interface IToolInvocationService
{
    /// <summary>
    ///     Invokes <paramref name="toolName" /> with <paramref name="argumentsJson" />. Never throws for a bad call:
    ///     every refusal, timeout and fault is an outcome.
    /// </summary>
    Task<ToolInvocationOutcome> InvokeAsync(string toolName,
        string argumentsJson,
        ToolInvocationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every tool this service would actually invoke, with the schema it validates against. The ONE source the
    ///     save-time validator, the run-start re-validation and the picker all read, so they cannot disagree with the
    ///     runtime about what is invocable.
    /// </summary>
    Task<IReadOnlyList<InvocableToolDescriptor>> ListInvocableToolsAsync(CancellationToken cancellationToken = default);
}
