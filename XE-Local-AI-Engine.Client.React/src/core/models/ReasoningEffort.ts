// Shared reasoning-effort levels a model/agent can be configured with. Lives in core because both `chat` (per-message
// effort picker) and `agents` (per-agent model-field default) depend on it — neither feature owns the concept.
//
// "none"/"low"/"medium"/"high" are the graded efforts for models with the Ollama `thinking` capability.
// "on" is the binary-reasoning ON state for a model WITHOUT that capability that still reasons by default
// (e.g. some GGUF chat templates): it maps to "omit the think field" so the model's built-in reasoning runs,
// while "none" maps to think:false (suppress). Graded models never use "on"; binary models only use "on"/"none".
// "minimal" and "xhigh" are Codex/cloud-only graded levels mapped to OpenAI Responses reasoning.effort — they
// are NEVER offered for Ollama models and must not leak to the Ollama `think` wire.
// "auto" is a CONFIGURATION value, not a wire value: the node resolves it per turn into a concrete tier
// (model + effort + output budget) and reports what it chose as a turn notice. It is never sent to a provider.
export type ReasoningEffort = "none" | "on" | "minimal" | "low" | "medium" | "high" | "xhigh" | "auto";
