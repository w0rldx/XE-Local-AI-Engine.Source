import { z } from "zod";

// Orchestration topology (orchestration). A Kind=Orchestrator agent definition carries its handoff topology as a raw
// JSON string (orchestrationTopologyJson on the wire). This module owns the typed view-model + Zod schema that the
// form section serializes to/from that string. The shape mirrors the backend contract (camelCase):
//   { version, triageAgentDefinitionId, participantAgentDefinitionIds[], handoffs[{from,to,reason?}],
//     maxTurnsPerAgent, returnToPrevious }
// The triage is THIS definition's own id and is always included in participantAgentDefinitionIds.

const ORCHESTRATION_TOPOLOGY_VERSION = 1;

// Conservative default turn cap. Keeps a routing loop from spinning.
const DEFAULT_MAX_TURNS_PER_AGENT = 8;

export interface OrchestrationHandoff {
	fromAgentDefinitionId: string;
	toAgentDefinitionId: string;
	// Optional human routing hint. Empty string is treated as "no reason" and serialized as null.
	reason: string | null;
}

// Structured form view-model for the topology editor. participantAgentDefinitionIds are the SPECIALISTS chosen in
// the form; the triage (this definition) is folded back in only at serialization time so the editor never lists the
// orchestrator among its own specialists.
export interface OrchestrationTopology {
	participantAgentDefinitionIds: string[];
	handoffs: OrchestrationHandoff[];
	maxTurnsPerAgent: number;
	returnToPrevious: boolean;
}

// Empty topology for a freshly-switched-to-Orchestrator definition.
export function emptyOrchestrationTopology(): OrchestrationTopology {
	return {
		participantAgentDefinitionIds: [],
		handoffs: [],
		maxTurnsPerAgent: DEFAULT_MAX_TURNS_PER_AGENT,
		returnToPrevious: false,
	};
}

const handoffSchema = z.object({
	fromAgentDefinitionId: z.string().trim().min(1),
	toAgentDefinitionId: z.string().trim().min(1),
	reason: z.string().nullable(),
});

// Validates the structured topology before submit so the user sees errors inline. Mirrors the server rules
//: ≥ 2 participants total (triage + ≥ 1 specialist), maxTurnsPerAgent is a positive int,
// returnToPrevious is bool, and a handoff never connects an agent to itself. The triage id is NOT part of this
// view-model (it is the definition's own id, added at serialize time), so the "≥ 2 participants" rule reads as
// "≥ 1 specialist selected" here. The "every endpoint ∈ participants" rule is enforced structurally by the form
// (edge dropdowns are constrained to the selected participant set, which includes the triage) — this schema only
// guards the parts it can see (non-empty distinct endpoints).
// Structural shape only (no cross-field rules). Used as the form-values field type so a Single definition can
// carry an empty topology without tripping the orchestrator-only rules; the full validity rules live in
// `orchestrationTopologySchema` below and are applied conditionally (kind=Orchestrator) by the form schema.
export const orchestrationTopologyShapeSchema = z.object({
	participantAgentDefinitionIds: z.array(z.string().trim().min(1)),
	handoffs: z.array(handoffSchema),
	maxTurnsPerAgent: z.number().int().positive(),
	returnToPrevious: z.boolean(),
});

export const orchestrationTopologySchema = orchestrationTopologyShapeSchema
	.refine((value) => value.participantAgentDefinitionIds.length > 0, {
		message: "An orchestrator needs at least one specialist participant.",
		path: ["participantAgentDefinitionIds"],
	})
	.refine((value) => value.handoffs.every((edge) => edge.fromAgentDefinitionId !== edge.toAgentDefinitionId), {
		message: "A handoff cannot connect an agent to itself.",
		path: ["handoffs"],
	});

// Wire shape persisted in orchestrationTopologyJson (camelCase, version-tagged). Triage + participants are GUIDs.
interface OrchestrationTopologyWire {
	version: number;
	triageAgentDefinitionId: string;
	participantAgentDefinitionIds: string[];
	handoffs: { fromAgentDefinitionId: string; toAgentDefinitionId: string; reason: string | null }[];
	maxTurnsPerAgent: number;
	returnToPrevious: boolean;
}

// Serialize the form topology to the wire JSON string. The triage is THIS definition's own id; when the id is not
// yet known (a brand-new orchestrator that has not been persisted) the triage id is empty — the backend assigns
// identity on create and the topology is re-saved with the real triage on the next edit. The triage is always
// included in participantAgentDefinitionIds (deduped), matching the backend contract.
export function serializeOrchestrationTopology(topology: OrchestrationTopology, triageAgentDefinitionId: string): string {
	const participantIds = triageAgentDefinitionId
		? [triageAgentDefinitionId, ...topology.participantAgentDefinitionIds.filter((id) => id !== triageAgentDefinitionId)]
		: [...topology.participantAgentDefinitionIds];

	const wire: OrchestrationTopologyWire = {
		version: ORCHESTRATION_TOPOLOGY_VERSION,
		triageAgentDefinitionId,
		participantAgentDefinitionIds: participantIds,
		handoffs: topology.handoffs.map((edge) => ({
			fromAgentDefinitionId: edge.fromAgentDefinitionId,
			toAgentDefinitionId: edge.toAgentDefinitionId,
			reason: edge.reason && edge.reason.trim().length > 0 ? edge.reason.trim() : null,
		})),
		maxTurnsPerAgent: topology.maxTurnsPerAgent,
		returnToPrevious: topology.returnToPrevious,
	};

	return JSON.stringify(wire);
}

// Deserialize the wire JSON string back into the form view-model (round-trip on edit). The triage id is stripped
// from participantAgentDefinitionIds so the editor lists only specialists; an unparseable/empty string yields an
// empty topology (graceful — the user re-authors it). Returns the triage id separately so the form can default the
// triage picker to the persisted value.
export interface DeserializedOrchestrationTopology {
	topology: OrchestrationTopology;
	triageAgentDefinitionId: string | null;
}

export function deserializeOrchestrationTopology(json: string | null): DeserializedOrchestrationTopology {
	if (json === null || json.trim().length === 0) {
		return { topology: emptyOrchestrationTopology(), triageAgentDefinitionId: null };
	}

	let parsed: unknown;
	try {
		parsed = JSON.parse(json);
	} catch {
		return { topology: emptyOrchestrationTopology(), triageAgentDefinitionId: null };
	}

	const wireSchema = z.object({
		triageAgentDefinitionId: z.string().nullish(),
		participantAgentDefinitionIds: z.array(z.string()).nullish(),
		handoffs: z
			.array(
				z.object({
					fromAgentDefinitionId: z.string(),
					toAgentDefinitionId: z.string(),
					reason: z.string().nullish(),
				}),
			)
			.nullish(),
		maxTurnsPerAgent: z.number().int().positive().nullish(),
		returnToPrevious: z.boolean().nullish(),
	});

	const result = wireSchema.safeParse(parsed);
	if (!result.success) {
		return { topology: emptyOrchestrationTopology(), triageAgentDefinitionId: null };
	}

	const wire = result.data;
	const triageAgentDefinitionId = wire.triageAgentDefinitionId ?? null;
	const specialists = (wire.participantAgentDefinitionIds ?? []).filter((id) => id !== triageAgentDefinitionId);

	return {
		topology: {
			participantAgentDefinitionIds: specialists,
			handoffs: (wire.handoffs ?? []).map((edge) => ({
				fromAgentDefinitionId: edge.fromAgentDefinitionId,
				toAgentDefinitionId: edge.toAgentDefinitionId,
				reason: edge.reason ?? null,
			})),
			maxTurnsPerAgent: wire.maxTurnsPerAgent ?? DEFAULT_MAX_TURNS_PER_AGENT,
			returnToPrevious: wire.returnToPrevious ?? false,
		},
		triageAgentDefinitionId,
	};
}
