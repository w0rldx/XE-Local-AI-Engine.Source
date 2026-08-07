namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    [Range(minimum: 1, maximum: 1024)]
    public int MaxSystemPromptSizeKb { get; set; } = 100;

    /// <summary>
    ///     Cap on the UTF-8 size of ONE inbound chat message's content. Enforced at the entry seams only — the SignalR
    ///     hub (<c>LocalChatHub.SendMessage</c>, before anything is persisted) and the encrypted-envelope assembler
    ///     (<c>RuntimePackageEnvelopeAssembler</c>, where every message in the package is untrusted platform input). It
    ///     is deliberately NOT re-applied to a conversation's stored history on later turns: the context budgeter trims
    ///     history against the model's real window, and hard-failing a turn on an already-stored message permanently
    ///     poisoned the conversation (an operator merely LOWERING this value would otherwise brick every conversation
    ///     holding a larger message).
    ///     <para>
    ///         The default is anchored to the transport: <c>AddSignalR</c> sets
    ///         <c>MaximumReceiveMessageSize = 512 KB</c> for the whole hub-invocation payload (see
    ///         <c>ConfigureServices</c>), so 256 KB of content leaves ample room for the JSON envelope around it AND
    ///         guarantees this app-level check fires first — with a legible "your message is too large" — instead of the
    ///         transport tearing the connection down with an opaque frame-size error. 256 KB of text is also roughly the
    ///         byte size of the ~65k-token context window the budgeter trims against, so a paste that gets through is a
    ///         paste the rest of the pipeline can actually work with. Larger documents belong on the attachment upload
    ///         path (<see cref="MaxUploadFileSizeMb" />), which extracts and inlines them under its own budget.
    ///     </para>
    /// </summary>
    [Range(minimum: 1, maximum: 1024)]
    public int MaxMessageSizeKb { get; set; } = 256;

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
