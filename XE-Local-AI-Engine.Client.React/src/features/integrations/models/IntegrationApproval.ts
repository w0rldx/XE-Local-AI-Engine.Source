import type { IntegrationToolFacts } from "@/features/integrations/models/IntegrationModels";

// Client-side mirror of the backend's TIGHTEN-ONLY tool-approval compose
// (AgentDefinitionResolver: policy.RequiresApproval(...) || definition.ToolApprovals[name]). The catalog's
// `effectiveRequiresApproval` IS the left operand projected onto the DTO, so a per-agent `false` is a NO-OP: it can
// never loosen a tool the node policy already gates.
//
// Both resolvers are FAIL-CLOSED for a tool name the live catalog does not know: an integration run is unattended,
// so guessing "probably fine" is the one error mode that matters here. A `??` would be fail-OPEN — a stale
// per-agent `"web_fetch": false` would hide the banner for a tool the node policy still gates.

/** Resolved tools that would stop an unattended run by asking for approval. Unknown names count as approval-requiring. */
export function resolveApprovalRequiringTools(
	allowedToolNames: readonly string[],
	toolApprovals: Readonly<Record<string, boolean>>,
	toolsByName: ReadonlyMap<string, IntegrationToolFacts>,
): string[] {
	return allowedToolNames.filter((name) => {
		const entry = toolsByName.get(name);
		if (entry === undefined) {
			return true;
		}
		return entry.effectiveRequiresApproval || toolApprovals[name] === true;
	});
}

/**
 * Resolved tools that can have side effects, which is what forbids a CallerManaged session (a caller-managed session
 * carries no persisted tool history, so a continued run cannot tell which side effects already happened). Only
 * `ReadLocal` is side-effect free; an unknown name counts as side-effecting, matching the catalog's own `Unknown`.
 */
export function resolveSideEffectingTools(
	allowedToolNames: readonly string[],
	toolsByName: ReadonlyMap<string, IntegrationToolFacts>,
): string[] {
	return allowedToolNames.filter((name) => toolsByName.get(name)?.category !== "ReadLocal");
}
