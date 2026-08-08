namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Typed memory scope for an extracted playbook action (adaptive memory). Stored as a nullable backing int on
///     <see cref="Entities.PlaybookAction" />; <c>null</c> means an untyped legacy action (manual/analysis) that predates
///     scoped memory. Non-injected metadata — it does NOT participate in the runtime package config hash (mirroring how
///     <c>Scope</c>/<c>Source</c> are excluded).
/// </summary>
public enum MemoryScope
{
    /// <summary>How-to/procedure lessons distilled from a successful run.</summary>
    Procedural = 0,

    /// <summary>Things to avoid, distilled from a failed run (tool error / invoke exception).</summary>
    Failure = 1,

    /// <summary>Stated user preferences observed during a run.</summary>
    UserPreference = 2,

    /// <summary>Project-specific facts/conventions observed during a run.</summary>
    Project = 3
}
