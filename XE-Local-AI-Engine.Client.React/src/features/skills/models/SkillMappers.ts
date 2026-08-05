import type {
	XeLocalAiEngineClientEndpointsSkillsV1CreateSkillRequest,
	XeLocalAiEngineClientEndpointsSkillsV1SkillResponse,
	XeLocalAiEngineClientEndpointsSkillsV1SkillSummaryResponse,
	XeLocalAiEngineClientEndpointsSkillsV1UpdateSkillRequest,
} from "@/core/api/generated";
import type { Skill, SkillFormValues, SkillSummary } from "@/features/skills/models/SkillModels";

// An empty/blank frontmatter input means "absent", which is `null` on the wire — never an empty string, so a cleared
// field round-trips as cleared rather than as a stored blank.
function toOptionalText(value: string): string | null {
	const trimmed = value.trim();
	return trimmed.length > 0 ? trimmed : null;
}

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
		license: dto.license ?? null,
		compatibility: dto.compatibility ?? null,
		allowedTools: dto.allowedTools ?? null,
		metadata: dto.metadata ?? null,
		// Provenance fails safe to Imported: a row whose origin did not survive the wire is treated as untrusted
		// rather than silently presented as locally authored.
		origin: dto.origin ?? "Imported",
		sourceUri: dto.sourceUri ?? null,
		importedAtUtc: dto.importedAtUtc ?? null,
		resourceCount: dto.resourceCount ?? 0,
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
		license: dto.license ?? null,
		compatibility: dto.compatibility ?? null,
		allowedTools: dto.allowedTools ?? null,
		metadata: dto.metadata ?? null,
		origin: dto.origin ?? "Imported",
		sourceUri: dto.sourceUri ?? null,
		importedAtUtc: dto.importedAtUtc ?? null,
	};
}

// Projects form values to the generated create request body. Trimmed so a stored skill never carries leading/
// trailing whitespace. Create has no `enabled` field on the wire (a new skill always persists enabled by the
// store default); the form's enabled flag only matters on update.
export function toCreateSkillRequest(form: SkillFormValues): XeLocalAiEngineClientEndpointsSkillsV1CreateSkillRequest {
	return {
		name: form.name.trim(),
		description: form.description.trim(),
		body: form.body.trim(),
		license: toOptionalText(form.license),
		compatibility: toOptionalText(form.compatibility),
		allowedTools: toOptionalText(form.allowedTools),
		metadata: form.metadata,
	};
}

// Projects form values to the generated update request body. Update carries the enabled flag so the operator can
// toggle a skill from the editor (the same posture as the UpdateSkillRequest wire contract). Every frontmatter field
// is sent on every update: the backend mapper treats the request as a full replace, so an omitted field is stored as
// null — an edit that dropped them would strip an imported skill's frontmatter.
export function toUpdateSkillRequest(form: SkillFormValues): XeLocalAiEngineClientEndpointsSkillsV1UpdateSkillRequest {
	return {
		name: form.name.trim(),
		description: form.description.trim(),
		body: form.body.trim(),
		enabled: form.enabled,
		license: toOptionalText(form.license),
		compatibility: toOptionalText(form.compatibility),
		allowedTools: toOptionalText(form.allowedTools),
		metadata: form.metadata,
	};
}
