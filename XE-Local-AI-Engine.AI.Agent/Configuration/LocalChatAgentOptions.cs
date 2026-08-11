namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for the node-local chat agent used by the React management UI.
/// </summary>
public sealed class LocalChatAgentOptions
{
    /// <summary>Configuration section containing local-chat agent defaults.</summary>
    public const string Section = "Agent:LocalChat";

    /// <summary>Display/name prefix assigned to the local chat agent.</summary>
    [Required]
    public string AgentName { get; set; } = "XeLocalAgent";

    /// <summary>Fallback local model id used when no request-specific model is supplied.</summary>
    [Required]
    public string DefaultModel { get; set; } = "qwen3.5:0.8b";

    /// <summary>Embedded instruction resource loaded as the local-chat system prompt.</summary>
    [Required]
    public string InstructionsResource { get; set; } = "XE_Local_AI_Engine.AI.Agent.Instructions.LocalChatDefault.txt";

    /// <summary>Whether the local chat offer list should include executable tools by default.</summary>
    public bool EnableTools { get; set; } = true;

    /// <summary>
    ///     Maximum number of extracted-document characters inlined into a plain-chat turn from the conversation's
    ///     uploaded attachments. Content beyond this budget is truncated with a notice. Agent-mode turns ignore this
    ///     (the agent reads the files through its tools).
    /// </summary>
    [Range(minimum: 1_000, maximum: 2_000_000)]
    public int MaxInlinedAttachmentChars { get; set; } = 48_000;

    /// <summary>
    ///     Maximum number of image attachments attached to a single vision (multimodal) turn. The client re-sends every
    ///     conversation attachment on each turn, so this — with <see cref="MaxImageAttachmentBytes" /> — bounds how many
    ///     decrypted images one send materializes; images beyond the cap are dropped (first-requested kept).
    /// </summary>
    [Range(minimum: 1, maximum: 64)]
    public int MaxImageAttachments { get; set; } = 8;

    /// <summary>
    ///     Aggregate decrypted-image byte budget for a single vision turn. Images are decrypted into memory and serialized
    ///     to the model, so this caps the total allocation a turn can trigger regardless of per-file upload limits; once
    ///     the running total would exceed it, no further images are attached. Defaults to 32 MiB.
    /// </summary>
    [Range(minimum: 1L * 1024 * 1024, maximum: 512L * 1024 * 1024)]
    public long MaxImageAttachmentBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>
    ///     Maximum number of retrieved knowledge-base characters inlined into a plain-chat turn when the user has opted
    ///     into knowledge-base grounding. The fused hybrid-search hits are fenced and concatenated up to this
    ///     budget; hits beyond it are dropped (lowest-scored first). Smaller than the attachment budget because KB
    ///     grounding is a supplement to, not a replacement for, the conversation. Agent-mode turns ignore this (the
    ///     agent reads the knowledge base through its <c>search_knowledge_base</c> tool instead).
    /// </summary>
    [Range(minimum: 1_000, maximum: 500_000)]
    public int MaxInlinedKnowledgeChars { get; set; } = 16_000;

    /// <summary>
    ///     Number of top fused knowledge-base hits retrieved to ground a plain-chat turn. Bounded so a single
    ///     turn cannot pull an unbounded number of chunks into context; the character budget
    ///     (<see cref="MaxInlinedKnowledgeChars" />) is the hard cap on top of this count. Mirrors the
    ///     <c>search_knowledge_base</c> tool's default limit.
    /// </summary>
    [Range(minimum: 1, maximum: 20)]
    public int KnowledgeChatTopK { get; set; } = 5;
}
