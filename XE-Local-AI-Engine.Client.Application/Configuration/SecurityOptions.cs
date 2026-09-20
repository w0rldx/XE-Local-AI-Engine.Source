namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    [Range(minimum: 1, maximum: 1024)]
    public int MaxSystemPromptSizeKb { get; set; } = 100;

    /// <summary>Cap on the UTF-8 size of ONE inbound chat message's content.</summary>
    /// <remarks>
    ///     Enforced at the entry seams only — the SignalR hub (<c>LocalChatHub.SendMessage</c>, before anything is persisted) and the
    ///     encrypted-envelope assembler (<c>RuntimePackageEnvelopeAssembler</c>, where every message is untrusted platform input) — and
    ///     deliberately NOT re-applied to stored history on later turns: the budgeter trims history against the model's real window, and
    ///     hard-failing a turn on an already-stored message poisons the conversation permanently, so merely LOWERING this value would brick
    ///     every conversation holding a larger one. Larger documents belong on the upload path (<see cref="MaxUploadFileSizeMb" />).
    /// </remarks>
    /// <value>Anchored to the transport's own 512 KB hub-invocation cap; why 256: docs/wiki/01-architecture-overview.md ("The local surface").</value>
    [Range(minimum: 1, maximum: 1024)]
    public int MaxMessageSizeKb { get; set; } = 256;

    // Per-file cap for chat upload attachments (multipart upload endpoint). Bounds a single uploaded document; the
    // extracted text is separately capped before it is inlined into a plain-chat turn.
    [Range(minimum: 1, maximum: 512)]
    public int MaxUploadFileSizeMb { get; set; } = 25;

    /// <summary>Accepts either a plain Ollama tag OR an <c>org/repo</c> GGUF reference.</summary>
    /// <remarks>
    ///     The repo branch is exactly <c>org/repo</c> with an optional <c>:quant</c> (no spaces, no extra path segments) and an OPTIONAL
    ///     <c>hf.co</c> / <c>huggingface.co</c> domain prefix, so both the bare "bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M" form (what
    ///     first-run provisioning and GGUF pulls produce) and the "hf.co/org/repo:quant" alias validate. Slash placement is governed here;
    ///     <c>ModelNameValidator</c> still rejects "..", "\\" and "://" before this check.
    /// </remarks>
    public string AllowedModelNamePattern { get; set; } =
        @"^(?:[a-zA-Z0-9._:-]+|(?:(?:hf\.co|huggingface\.co)/)?[a-zA-Z0-9._-]+/[a-zA-Z0-9._-]+(?::[a-zA-Z0-9._-]+)?)$";
}
