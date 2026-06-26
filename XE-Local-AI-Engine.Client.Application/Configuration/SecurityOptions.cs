namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    [Range(minimum: 1, maximum: 1024)]
    public int MaxSystemPromptSizeKb { get; set; } = 100;

    [Range(minimum: 1, maximum: 1024)]
    public int MaxMessageSizeKb { get; set; } = 50;

    // Per-file cap for chat upload attachments (multipart upload endpoint). Bounds a single uploaded document; the
    // extracted text is separately capped before it is inlined into a plain-chat turn.
    [Range(minimum: 1, maximum: 512)]
    public int MaxUploadFileSizeMb { get; set; } = 25;

    // Accepts either a plain Ollama tag OR an org/repo GGUF reference. The repo branch is exactly org/repo with an
    // optional :quant (no spaces, no extra path segments) and an OPTIONAL hf.co / huggingface.co domain prefix — so
    // both the bare "bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M" form (what first-run provisioning and GGUF pulls
    // produce) and the "hf.co/org/repo:quant" alias validate. Slash placement is governed here; ModelNameValidator
    // still rejects "..", "\\" and "://" before this check.
    public string AllowedModelNamePattern { get; set; } =
        @"^(?:[a-zA-Z0-9._:-]+|(?:(?:hf\.co|huggingface\.co)/)?[a-zA-Z0-9._-]+/[a-zA-Z0-9._-]+(?::[a-zA-Z0-9._-]+)?)$";
}
