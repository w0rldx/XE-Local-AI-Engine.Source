namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class TrainingDatasetDefinition
{
    public Guid Id { get; set; }

    /// <summary>Operator label for the definition (plaintext, structural).</summary>
    public string Name { get; set; } = string.Empty;

    public TrainingDatasetKind Kind { get; set; }

    /// <summary>
    ///     The whole definition body as UTF-8 JSON (tool-schema snapshot, sample-kind mix, size target, teacher agent and
    ///     model, teacher output mode, critic toggle, hold-out fraction, seed policy). Plaintext while tracked in memory;
    ///     encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name
    ///     <c>training_definition_json</c>. Required.
    /// </summary>
    public byte[] DefinitionJson { get; set; } = [];

    /// <summary>
    ///     The artifact version an edit bumps — what <see cref="TrainingDataset.DefinitionVersion" /> pins. Distinct from
    ///     <see cref="Version" />, which is the optimistic-concurrency token.
    /// </summary>
    public long DefinitionVersion { get; set; }

    /// <summary>Optimistic-concurrency token, bumped by hand on every mutating store method (benchmark convention).</summary>
    public long Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
