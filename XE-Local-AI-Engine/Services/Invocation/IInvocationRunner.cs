namespace XE_Local_AI_Engine.Services.Invocation
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using XE_Local_AI_Engine.Models;

    public interface IInvocationRunner
    {
        Task RunAsync(RuntimePackage package, CancellationToken cancellationToken = default);

        Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default);

        void Cancel(Guid invocationId);

        void CancelAll();

        void CleanupStaleToolCalls(TimeSpan maxAge);

        void ResolveToolCallResult(ToolCallResultEvent evt);
    }
}
