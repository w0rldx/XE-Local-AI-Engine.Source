namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    [Range(1, 1024)]
    public int MaxSystemPromptSizeKb { get; set; } = 100;

    [Range(1, 1024)]
    public int MaxMessageSizeKb { get; set; } = 50;

    // Accepts either a plain Ollama tag OR a Hugging Face GGUF reference (hf.co / huggingface.co alias).
    // The HF branch requires exactly domain/org/repo with an optional :quant — no spaces, no extra path segments.
    // Slash placement is governed here; ModelNameValidator still rejects "..", "\\" and "://" before this check.
    public string AllowedModelNamePattern { get; set; } =
        @"^(?:[a-zA-Z0-9._:-]+|(?:hf\.co|huggingface\.co)/[a-zA-Z0-9._-]+/[a-zA-Z0-9._-]+(?::[a-zA-Z0-9._-]+)?)$";
}
