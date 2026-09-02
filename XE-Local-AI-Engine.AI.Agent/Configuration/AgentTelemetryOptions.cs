namespace XE_Local_AI_Engine.AI.Agent.Configuration;

/// <summary>
///     Application-level control over what the agent's gen_ai OpenTelemetry instrumentation records. Bound from the
///     <c>Agent:Telemetry</c> configuration section. The single knob here is a deliberate, code-owned opt-in that
///     overrides the ambient environment default: the MEAI OpenTelemetry chat client honors
///     <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c> when its <c>EnableSensitiveData</c> is left unset, and
///     Aspire injects that variable as <c>true</c> in dev — which would silently emit full prompts, reasoning, and
///     completions into spans. The pipeline therefore sets <c>EnableSensitiveData</c> EXPLICITLY from
///     <see cref="CaptureSensitiveContent" /> so the environment variable can never re-enable capture behind the
///     operator's back.
/// </summary>
public sealed class AgentTelemetryOptions
{
    public const string Section = "Agent:Telemetry";

    /// <summary>
    ///     When <see langword="true" />, the gen_ai instrumentation captures sensitive message content (prompts,
    ///     reasoning, completions, and tool-call arguments) into telemetry spans. Default <see langword="false" />: this
    ///     is a privacy-sensitive opt-in that must be turned on deliberately and is NEVER driven by an ambient
    ///     environment variable. Enabling it logs a prominent startup warning.
    /// </summary>
    public bool CaptureSensitiveContent { get; set; }
}
