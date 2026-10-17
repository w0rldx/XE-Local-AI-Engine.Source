namespace XE_Local_AI_Engine.Cli.Rendering;

using XE_Local_AI_Engine.Cli.Streaming;

/// <summary>Shows one chat turn: the terminal renderer for people, the JSON-lines renderer for scripts.</summary>
internal interface ITurnRenderer
{
    Task RenderAsync(CliStreamEvent item, CancellationToken cancellationToken);

    /// <summary>Something the client itself did or could not do, such as denying an approval without a terminal.</summary>
    Task ClientNoticeAsync(string kind, string message, CancellationToken cancellationToken);

    Task FinishAsync(TurnOutcome outcome, CancellationToken cancellationToken);
}
