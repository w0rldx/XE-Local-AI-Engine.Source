import type {
	XeLocalAiEngineClientEndpointsAgentsV1AgentDefinitionResponse,
	XeLocalAiEngineClientEndpointsAgentsV1CreateAgentDefinitionRequest,
	XeLocalAiEngineClientEndpointsAgentsV1ListAgentDefinitionsResponse,
} from "@/core/api/generated";
import type { ReasoningEffort } from "@/core/models/ReasoningEffort";
import type {
	AgentDefinition,
	AgentDefinitionFormValues,
	AgentDefinitionKind,
} from "@/features/agents/models/AgentDefinitionModels";
import { serializeOrchestrationTopology } from "@/features/agents/models/OrchestrationTopologyModels";

// Maps optional generated wire fields into required domain values; validation remains at the API boundary.

function normalizeReasoningEffort(value: string | null | undefined): ReasoningEffort | null {
	if (value === "none" || value === "low" || value === "medium" || value === "high") {
		return value;
	}

	return null;
}

// `kind` is the Single/Orchestrator discriminator. The generated type narrows it to the same union, but it is
// optional on the wire, so an absent kind degrades to "Single" (the default persona kind).
function normalizeKind(value: AgentDefinitionKind | undefined): AgentDefinitionKind {
	return value === "Orchestrator" ? "Orchestrator" : "Single";
}

export function toAgentDefinition(dto: XeLocalAiEngineClientEndpointsAgentsV1AgentDefinitionResponse): AgentDefinition {
	return {
		id: dto.id ?? "",
		name: dto.name ?? "",
		description: dto.description ?? "",
		instructions: dto.instructions ?? "",
		modelProfile: dto.modelProfile ?? null,
		reasoningEffort: normalizeReasoningEffort(dto.reasoningEffort),
		kind: normalizeKind(dto.kind),
		allowedToolNames: [...(dto.allowedToolNames ?? [])],
		toolApprovals: { ...(dto.toolApprovals ?? {}) },
		allowedSkillIds: [...(dto.allowedSkillIds ?? [])],
		orchestrationTopologyJson: dto.orchestrationTopologyJson ?? null,
		playbookEnabled: dto.playbookEnabled ?? false,
		defaultTemporaryChat: dto.defaultTemporaryChat ?? false,
		// Backend default is ON; an absent wire value degrades to true so a pre-feature row keeps learning from runs.
		memoryExtractionEnabled: dto.memoryExtractionEnabled ?? true,
		disableBaseScaffold: dto.disableBaseScaffold ?? false,
		disableToolRelevanceFilter: dto.disableToolRelevanceFilter ?? false,
		version: dto.version ?? 0,
		createdAtUtc: dto.createdAtUtc ?? 0,
		updatedAtUtc: dto.updatedAtUtc ?? 0,
	};
}

export function toAgentDefinitions(dto: XeLocalAiEngineClientEndpointsAgentsV1ListAgentDefinitionsResponse): AgentDefinition[] {
	return (dto.items ?? []).map(toAgentDefinition);
}

// Build the save request body from the form. `triageAgentDefinitionId` is the orchestrator definition's own id —
// known on edit, empty on create (the backend assigns identity and the triage is re-pinned on the next edit). The
// orchestration topology is serialized ONLY for Orchestrator definitions; a Single definition sends null so a stale
// topology never leaks into a non-orchestrator. The create and update request types are structurally identical, so
// one mapper feeds both. The domain form carries readonly arrays (allowedToolNames); they are spread to mutable
// arrays here so the value satisfies the generated body type.
export function toSaveAgentDefinitionRequest(
	form: AgentDefinitionFormValues,
	triageAgentDefinitionId = "",
): XeLocalAiEngineClientEndpointsAgentsV1CreateAgentDefinitionRequest {
	const trimmedDescription = form.description.trim();

	return {
		name: form.name.trim(),
		description: trimmedDescription.length > 0 ? trimmedDescription : null,
		instructions: form.instructions.trim(),
		modelProfile: form.modelProfile,
		reasoningEffort: form.reasoningEffort,
		kind: form.kind,
		allowedToolNames: [...form.allowedToolNames],
		// Persist only approvals for currently selected tools so the stored map never drifts from the tool list.
		toolApprovals: Object.fromEntries(
			Object.entries(form.toolApprovals).filter(([toolName]) => form.allowedToolNames.includes(toolName)),
		),
		allowedSkillIds: [...form.allowedSkillIds],
		orchestrationTopologyJson:
			form.kind === "Orchestrator" ? serializeOrchestrationTopology(form.orchestration, triageAgentDefinitionId) : null,
		playbookEnabled: form.playbookEnabled,
		defaultTemporaryChat: form.defaultTemporaryChat,
		memoryExtractionEnabled: form.memoryExtractionEnabled,
		disableBaseScaffold: form.disableBaseScaffold,
		disableToolRelevanceFilter: form.disableToolRelevanceFilter,
		// Provenance of an applied AI draft, echoed back exactly as the draft endpoint returned it. Null on an
		// ordinary edit, which the server reads as "leave the stored provenance alone" rather than "clear it".
		generationMetadata: form.generationMetadata,
	};
}
