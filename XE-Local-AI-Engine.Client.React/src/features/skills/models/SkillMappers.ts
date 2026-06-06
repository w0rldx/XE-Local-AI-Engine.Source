import type {
	XeLocalAiEngineClientEndpointsSkillsV1CreateSkillRequest,
	XeLocalAiEngineClientEndpointsSkillsV1SkillResponse,
	XeLocalAiEngineClientEndpointsSkillsV1SkillSummaryResponse,
	XeLocalAiEngineClientEndpointsSkillsV1UpdateSkillRequest,
} from "@/core/api/generated";
import type { Skill, SkillFormValues, SkillSummary } from "@/features/skills/models/SkillModels";

// Maps the generated (OpenAPI) skill response types to the stricter domain view-models the components depend on,
// and projects the domain form values back onto the generated request bodies. The generated types are the single
// source of truth for the wire shape; their fields are all optional (`x?: T`), so each response mapper coalesces
// every field to a required value with a safe default. Boundary validation + ApiError convergence are owned by the
// generated zod validator + the callWithResponseValidation bridge at the hook — these mappers only project the
// already-validated wire shape into the immutable domain shape.

export function toSkill(dto: XeLocalAiEngineClientEndpointsSkillsV1SkillResponse): Skill {
	return {
		id: dto.id ?? "",
		name: dto.name ?? "",
		description: dto.description ?? "",
		body: dto.body ?? "",
		enabled: dto.enabled ?? false,
		version: dto.version ?? 0,
		createdAtUtc: dto.createdAtUtc ?? 0,
		updatedAtUtc: dto.updatedAtUtc ?? 0,
	};
}

export function toSkillSummary(dto: XeLocalAiEngineClientEndpointsSkillsV1SkillSummaryResponse): SkillSummary {
	return {
		id: dto.id ?? "",
		name: dto.name ?? "",
		description: dto.description ?? "",
		enabled: dto.enabled ?? false,
		version: dto.version ?? 0,
		createdAtUtc: dto.createdAtUtc ?? 0,
		updatedAtUtc: dto.updatedAtUtc ?? 0,
	};
}

// Projects form values to the generated create request body. Trimmed so a stored skill never carries leading/
// trailing whitespace. Create has no `enabled` field on the wire (a new skill always persists enabled by the
// store default, plan §6.1); the form's enabled flag only matters on update.
export function toCreateSkillRequest(form: SkillFormValues): XeLocalAiEngineClientEndpointsSkillsV1CreateSkillRequest {
	return {
		name: form.name.trim(),
		description: form.description.trim(),
		body: form.body.trim(),
	};
}

// Projects form values to the generated update request body. Update carries the enabled flag so the operator can
// toggle a skill from the editor (the same posture as the UpdateSkillRequest wire contract).
export function toUpdateSkillRequest(form: SkillFormValues): XeLocalAiEngineClientEndpointsSkillsV1UpdateSkillRequest {
	return {
		name: form.name.trim(),
		description: form.description.trim(),
		body: form.body.trim(),
		enabled: form.enabled,
	};
}
