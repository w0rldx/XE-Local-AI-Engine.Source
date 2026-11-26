namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>One saved training-stepper run: its summary in an encrypted column, its recorded trace in the Model Lab playground blob scope named by <see cref="Id" />.</summary>
internal sealed class ModelLabPlaygroundRun
{
    public Guid Id { get; set; }

    public string TemplateId { get; set; } = string.Empty;

    public long CreatedAtUtc { get; set; }

    public Guid TraceBlobId { get; set; }

    public string TraceSha256 { get; set; } = string.Empty;

    public long TraceByteCount { get; set; }

    /// <summary>Encrypted: the script's <c>result.json</c>.</summary>
    public byte[] ResultJson { get; set; } = [];
}
