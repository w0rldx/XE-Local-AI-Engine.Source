import {
	type IntegrationToolFacts,
	integrationToolContinuesUnanswered,
} from "@/features/integrations/models/IntegrationModels";

// Client-side mirror of the backend's TIGHTEN-ONLY tool-approval compose
// (AgentDefinitionResolver: policy.RequiresApproval(...) || definition.ToolApprovals[name]). The catalog's
// `effectiveRequiresApproval` IS the left operand projected onto the DTO, so a per-agent `false` is a NO-OP: it can
// never loosen a tool the node policy already gates.
//
// The resolver is FAIL-CLOSED for a tool name the live catalog does not know: an integration run is unattended, so
// guessing "probably fine" is the one error mode that matters here. A `??` would be fail-OPEN — a stale per-agent
// `"web_fetch": false` would hide the banner for a tool the node policy still gates.

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
		// `ask_user` is approval-gated too — that is how the call reaches the human — but an unattended run does not
		// stop on it: the coordinator stashes a "not answered" result and the turn continues. Warning about it named a
		// tool that would not actually fail the run. Every other behaviour still composes the tighten-only OR, so an
		// unrecognised value stays fail-closed: a failing tool is approval-gated by construction.
		if (entry.unattendedBehaviour === integrationToolContinuesUnanswered) {
			return false;
		}
		return entry.effectiveRequiresApproval || toolApprovals[name] === true;
	});
}
