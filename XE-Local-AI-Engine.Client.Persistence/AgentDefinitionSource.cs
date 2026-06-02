namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Provenance of an agent definition. <see cref="Manual" /> is an operator-authored row created through the normal
///     create contract; <see cref="Seeded" /> is a row materialized from the vendored starter-pack catalog by the
///     dedicated import path. Provenance is forge-proof: only the import store method sets <see cref="Seeded" />, so the
///     operator create/update contract can never mint a seeded row.
/// </summary>
public enum AgentDefinitionSource
{
    Manual = 0,
    Seeded = 1
}
