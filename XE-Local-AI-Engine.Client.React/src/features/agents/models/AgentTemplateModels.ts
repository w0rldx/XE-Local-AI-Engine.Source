// UI-only helpers for the starter-agent gallery. The wire DTO is the generated hey-api summary type
// (re-exported below) — this module never hand-writes the wire shape; it only adds the token-budget
// presentation logic the gallery's warning badge keys off of (a chars/4 heuristic estimate, not a true
// tokenizer — see the seed plan §9).

export type { XeLocalAiEngineClientEndpointsAgentsV1AgentTemplateSummary as AgentTemplateSummary } from "@/core/api/generated/types.gen";

// Estimated-prompt-token ceiling above which a seeded persona is flagged as "large" in the gallery. Cloud-tuned
// upstream prompts can be heavy for local small models, so the badge warns before import; the operator still decides.
export const AGENT_TEMPLATE_TOKEN_BUDGET = 4000;

/** True when a template's estimated prompt size exceeds the gallery's soft budget (drives the warning badge). */
export const isOverTokenBudget = (estimatedPromptTokens: number): boolean => estimatedPromptTokens > AGENT_TEMPLATE_TOKEN_BUDGET;
