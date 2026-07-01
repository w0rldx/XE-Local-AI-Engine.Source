namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Canonical structural node of a <see cref="KnowledgeDocument" /> — a section/heading carved out during extraction.
///     Used for section/title metadata and parent-section context expansion at retrieval time. Rebuildable from the raw
///     document, so a re-ingest replaces the section rows for the owning document.
/// </summary>
internal sealed record class KnowledgeDocumentSection
{
    public Guid SectionId { get; set; }

    /// <summary>Owning document. Foreign-keyed to <c>knowledge_documents</c> with cascade delete (see configuration).</summary>
    public Guid DocumentId { get; set; }

    /// <summary>Order of this section within the document.</summary>
    public int Ordinal { get; set; }

    /// <summary>Section title (from the extractor's document header); null when the section has no heading.</summary>
    public string? Heading { get; set; }

    /// <summary>Header level 1-6; null when the section is not a heading.</summary>
    public int? Level { get; set; }
}
