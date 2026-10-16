namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Says why a chat turn waits for a benchmark or a training job, with a <see cref="TurnNoticeKind.QueuedBehindGpuWork" />
///     notice (R7-03). It fires only where the turn really waits, never merely because a GPU hold exists.
/// </summary>
/// <remarks>
///     The benchmark queue takes its shared hold on every poll, even with nothing to claim, so the hold alone proves
///     nothing. A benchmark that claimed work runs a measurement on the node's one invocation slot under a conversation
///     that does not exist, and that is what a queued turn waits behind. A training run, evaluation or export holds the
///     node exclusively, and a turn waits on it only when it has to load its model. The text is persisted with the reply,
///     so it reads in the past tense; the SPA localizes it by this text.
/// </remarks>
internal static class GpuWorkHolderNotice
{
    internal const string BenchmarkMessage = "This message was queued behind a running benchmark.";

    internal const string TrainingRunMessage = "This message was queued behind a running training run.";

    internal const string EvaluationRunMessage = "This message was queued behind a running training evaluation.";

    internal const string ExportMessage = "This message was queued behind a running model export.";

    /// <summary>Reports the notice when the turn is about to queue behind a benchmark measurement holding the invocation slot.</summary>
    public static async Task ReportIfQueuedBehindBenchmarkAsync(IGpuWorkGate gate,
        IWorkerEventDispatcher dispatcher,
        INodeChatPersistenceService persistence,
        Guid conversationId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (dispatcher.CurrentInvocation is not { } holder
            || holder.ConversationId == conversationId
            || NodeChatInvocationPump.IsTerminal(holder.Status)
            || !gate.Holders.Contains(GpuWorkKind.Benchmark)
            || await persistence.GetConversationKindAsync(holder.ConversationId, cancellationToken) is not null)
        {
            return;
        }

        await dispatcher.ReportTurnNoticeAsync(Notice(requestId, BenchmarkMessage));
    }

    /// <summary>The notice for a turn that starts loading its model while an exclusive GPU job owns the node, or <see langword="null" />.</summary>
    public static TurnNoticePayload? ForModelLoad(IGpuWorkGate gate, Guid invocationId) =>
        gate.ExclusiveKind switch
        {
            GpuWorkKind.TrainingRun => Notice(invocationId, TrainingRunMessage),
            GpuWorkKind.EvaluationRun => Notice(invocationId, EvaluationRunMessage),
            GpuWorkKind.Export => Notice(invocationId, ExportMessage),
            _ => null
        };

    private static TurnNoticePayload Notice(Guid invocationId, string message) =>
        new()
        {
            InvocationId = invocationId,
            Kind = TurnNoticeKind.QueuedBehindGpuWork,
            Message = message
        };
}
