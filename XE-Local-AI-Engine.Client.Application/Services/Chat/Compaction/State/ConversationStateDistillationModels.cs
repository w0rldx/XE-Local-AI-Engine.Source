namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

/// <summary>Why a distillation run ended the way it did.</summary>
public enum ConversationStateDistillationStatus
{
    /// <summary>At least one call's delta was applied and persisted with its watermark.</summary>
    Distilled,

    NothingToDistill,

    NoLocalModel,

    /// <summary>The first call produced no parseable delta; state and watermark are untouched.</summary>
    DistillerReturnedNothing,

    /// <summary>The shared deadline expired; calls persisted before it stay.</summary>
    TimedOut,

    /// <summary>A path change or variant mint cleared the state while a call was in flight; that call's delta was discarded.</summary>
    Superseded,

    ConversationNotFound,

    Disabled
}

public sealed class ConversationStateDistillationOutcome
{
    public required ConversationStateDistillationStatus Status { get; init; }

    public int Calls { get; init; }

    /// <summary>The state watermark after the run (anchor space); null when the conversation has no state.</summary>
    public int? CoversToSequence { get; init; }

    /// <summary>The state after the run, or the stored one when nothing ran; null when none exists.</summary>
    public ConversationStateDocument? Document { get; init; }
}
