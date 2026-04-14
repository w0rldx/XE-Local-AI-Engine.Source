namespace XE_Local_AI_Engine.Client.Services.Invocation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Events;

public interface IInvocationRunner
{
    Task RunAsync(RuntimePackage package, CancellationToken cancellationToken = default);

    Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default);

    void Cancel(Guid invocationId);

    void CancelAll();

    void CleanupStaleToolCalls(TimeSpan maxAge);

    void ResolveToolCallResult(ToolCallResultEvent evt);
}
