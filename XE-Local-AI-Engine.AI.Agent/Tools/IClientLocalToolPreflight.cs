namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Optional companion of an approval-gated <see cref="IClientLocalToolHandler" />: says, before a turn starts, that every call
///     of the tool this turn is certain to be refused whatever its arguments.
/// </summary>
/// <remarks>
///     Asking a person to approve a call the node will refuse anyway is noise, so an agent turn whose preflight refuses gets
///     the tool without its approval wrap, and a call answers with the refusal and never reaches the handler.
/// </remarks>
internal interface IClientLocalToolPreflight
{
    /// <summary>The refusal text when every call would be refused right now, otherwise <see langword="null" />.</summary>
    ValueTask<string?> PreflightAsync(CancellationToken cancellationToken = default);
}
