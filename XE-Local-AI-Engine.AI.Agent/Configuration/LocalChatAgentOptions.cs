namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class LocalChatAgentOptions
{
    public const string Section = "Agent:LocalChat";

    [Required]
    public string AgentName { get; set; } = "XeLocalAgent";

    [Required]
    public string DefaultModel { get; set; } = "qwen3.5:0.8b";

    [Required]
    public string InstructionsResource { get; set; } = "XE_Local_AI_Engine.AI.Agent.Instructions.LocalChatDefault.txt";

    public bool EnableTools { get; set; } = true;

    /// <summary>
    ///     Maximum extracted-document characters inlined into a plain-chat turn from the conversation's uploaded
    ///     attachments; content beyond the budget is truncated with a notice.
    /// </summary>
    /// <remarks>Agent-mode turns ignore this: the agent reads the files through its tools.</remarks>
    [Range(minimum: 1_000, maximum: 2_000_000)]
    public int MaxInlinedAttachmentChars { get; set; } = 48_000;

    /// <summary>Maximum image attachments carried by a single vision (multimodal) turn.</summary>
    /// <remarks>
    ///     The client re-sends every conversation attachment on each turn, so this — with
    ///     <see cref="MaxImageAttachmentBytes" /> — bounds how many decrypted images one send materializes. Images
    ///     beyond the cap are dropped, first-requested kept.
    /// </remarks>
    [Range(minimum: 1, maximum: 64)]
    public int MaxImageAttachments { get; set; } = 8;

    /// <summary>Aggregate decrypted-image byte budget for a single vision turn. Defaults to 32 MiB.</summary>
    /// <remarks>
    ///     Images are decrypted into memory and serialized to the model, so this caps the total allocation one turn can
    ///     trigger regardless of per-file upload limits; once the running total would exceed it, no further images are
    ///     attached.
    /// </remarks>
    [Range(minimum: 1L * 1024 * 1024, maximum: 512L * 1024 * 1024)]
    public long MaxImageAttachmentBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>
    ///     Maximum retrieved knowledge-base characters inlined into a plain-chat turn when the user has opted into
    ///     knowledge-base grounding.
    /// </summary>
    /// <remarks>
    ///     Fused hybrid-search hits are fenced and concatenated up to this budget; hits beyond it are dropped,
    ///     lowest-scored first. Smaller than the attachment budget because KB grounding supplements the conversation
    ///     rather than replacing it. Agent-mode turns ignore this and read the knowledge base through the
    ///     <c>search_knowledge_base</c> tool instead.
    /// </remarks>
    [Range(minimum: 1_000, maximum: 500_000)]
    public int MaxInlinedKnowledgeChars { get; set; } = 16_000;

    /// <summary>Number of top fused knowledge-base hits retrieved to ground a plain-chat turn.</summary>
    /// <remarks>
    ///     Bounded so a single turn cannot pull an unbounded number of chunks into context, and
    ///     <see cref="MaxInlinedKnowledgeChars" /> is the hard cap on top of this count. Mirrors the
    ///     <c>search_knowledge_base</c> tool's default limit.
    /// </remarks>
    [Range(minimum: 1, maximum: 20)]
    public int KnowledgeChatTopK { get; set; } = 5;
}
