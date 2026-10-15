namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>Who may start a run: a definition's accepted set, and the one source each run records.</summary>
/// <remarks>
///     A <c>[Flags]</c> enum stored as a plain <c>int</c>, like <see cref="IntegrationInputKinds" />: a combination has no
///     stable, length-bounded string form. A definition accepts <c>Manual | Chat</c> unless the operator widens it (ADR 0021).
/// </remarks>
[Flags]
public enum GraphWorkflowTriggerSource
{
    /// <summary>The operator, from the Graph Workflows page.</summary>
    Manual = 1,

    /// <summary>A chat conversation bound to a chat workflow.</summary>
    Chat = 2,

    /// <summary>A scheduled job.</summary>
    Schedule = 4,

    /// <summary>The external integration API.</summary>
    Integration = 8,

    /// <summary>A spoken or typed command resolved by the intent router.</summary>
    Intent = 16
}
