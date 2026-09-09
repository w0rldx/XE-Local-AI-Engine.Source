namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     What one state-tool call committed: the sentence handed back to the model, and — when it wrote a row — the
///     watermark that write allocated plus what changed, so the base can announce it after the commit.
/// </summary>
internal sealed record WorkSessionToolOutcome(string Message, long? Sequence = null, WorkSessionChangeKind Kind = WorkSessionChangeKind.Status);
