namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     A caller-supplied work-session input the service refuses: a blank title, an unknown or non-tool-capable agent, a
///     follow-up over the node's message-size cap, a cloud-egress repoint.
///     <para>
///         Dedicated rather than <see cref="ArgumentException" /> because <see cref="ArgumentNullException" /> derives
///         from that one, and an internal null-guard bug must never reach the operator as a 400.
///     </para>
/// </summary>
public sealed class WorkSessionValidationException(string message) : Exception(message);
