import type { AgentDefinition } from "@/features/agents/models/AgentDefinitionModels";
import { isModelToolCapable } from "@/features/agents/models/ToolCapability";

/**
 * The triage's dropdown value. Stable sentinel rather than the definition's id: on create the orchestrator has no id
 * yet, and an edge still has to be able to point at it.
 */
const TRIAGE_OPTION_VALUE = "__triage__";

/** Specialist candidates exclude self — the orchestrator is the triage, never its own specialist. */
export function orchestrationParticipantOptions(
	candidateDefinitions: readonly AgentDefinition[],
	selfId: string,
): { value: string; label: string }[] {
	return candidateDefinitions
		.filter((definition) => definition.id !== selfId)
		.map((definition) => ({ value: definition.id, label: definition.name }));
}

export function orchestrationDefinitionsById(candidateDefinitions: readonly AgentDefinition[]): Map<string, AgentDefinition> {
	return new Map(candidateDefinitions.map((definition) => [definition.id, definition]));
}

/** Edge endpoint options = the triage (under its sentinel) plus every selected specialist, in selection order. */
export function orchestrationEndpointOptions(
	triageLabel: string,
	participantAgentDefinitionIds: readonly string[],
	definitionsById: ReadonlyMap<string, AgentDefinition>,
): { value: string; label: string }[] {
	return [
		{ value: TRIAGE_OPTION_VALUE, label: triageLabel },
		...participantAgentDefinitionIds.map((id) => ({ value: id, label: definitionsById.get(id)?.name ?? id })),
	];
}

/** Resolve an edge endpoint dropdown value (sentinel or specialist id) to the stored id. */
export function resolveOrchestrationEndpoint(value: string, selfId: string): string {
	return value === TRIAGE_OPTION_VALUE ? selfId : value;
}

/** Resolve a stored endpoint id back to its dropdown value; the triage (or a not-yet-saved self) maps to the sentinel. */
export function toOrchestrationEndpointValue(id: string, selfId: string): string {
	return id === selfId || id === "" ? TRIAGE_OPTION_VALUE : id;
}

/**
 * Every model in play — the orchestrator's own plus each selected participant's — that is NOT tool-capable. Routing
 * IS tool calling, so any of these degrades the whole orchestration to a single agent. Empty when the capability
 * source is unavailable: an unknown list must not accuse a model that may well be fine.
 */
export function incapableOrchestrationModels(
	orchestratorModelProfile: string | null,
	participantAgentDefinitionIds: readonly string[],
	definitionsById: ReadonlyMap<string, AgentDefinition>,
	toolCapableModels: readonly string[],
): string[] {
	if (toolCapableModels.length === 0) {
		return [];
	}
	const offenders: string[] = [];
	if (!isModelToolCapable(orchestratorModelProfile, toolCapableModels)) {
		offenders.push(orchestratorModelProfile ?? "");
	}
	for (const id of participantAgentDefinitionIds) {
		const model = definitionsById.get(id)?.modelProfile ?? null;
		if (model !== null && !isModelToolCapable(model, toolCapableModels)) {
			offenders.push(model);
		}
	}
	return Array.from(new Set(offenders.filter((model) => model.length > 0)));
}
