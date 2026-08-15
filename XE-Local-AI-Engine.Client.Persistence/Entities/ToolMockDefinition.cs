namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class ToolMockDefinition
{
    public Guid Id { get; set; }

    /// <summary>The catalog tool name this mock stands in for. Plaintext (structural), case-insensitive.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    ///     Declarative match rules and literal responses as UTF-8 JSON. Plaintext while tracked in memory; encrypted at
    ///     rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>tool_mock_json</c>. Required.
    /// </summary>
    public byte[] MockJson { get; set; } = [];

    /// <summary>
    ///     The static verifier's findings as UTF-8 JSON. Same treatment as <see cref="MockJson" /> under a distinct AAD
    ///     column name <c>tool_mock_verification_json</c>, so a mock body can never be substituted for a verdict body.
    ///     Optional — an unverified mock has none.
    /// </summary>
    public byte[]? VerificationJson { get; set; }

    public ToolMockVerificationState VerificationState { get; set; }

    /// <summary>Only a <see cref="ToolMockVerificationState.Verified" /> and enabled mock is usable; there is no fallthrough.</summary>
    public bool Enabled { get; set; }

    public long Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
