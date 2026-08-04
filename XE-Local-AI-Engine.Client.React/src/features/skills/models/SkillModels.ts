import { z } from "zod";

// Agent Skills specification name: lowercase letters/digits separated by SINGLE hyphens; no leading, trailing or
// consecutive hyphens. This is a client-side convenience only — the backend delegates to MAF's own
// AgentSkillFrontmatter.ValidateName and stays authoritative. The earlier pattern
// /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/ accepted consecutive hyphens, which MAF rejects, so a name like `foo--bar`
// passed both this check and the backend and then threw when the skill was built into an agent.
export const SKILL_NAME_PATTERN = /^[a-z0-9]+(-[a-z0-9]+)*$/;

// Length caps mirror the backend: Name ≤64, Description ≤1024, Body ≤20000 (matches the instructions cap).
export const SKILL_NAME_MAX = 64;
export const SKILL_DESCRIPTION_MAX = 1024;
export const SKILL_BODY_MAX = 20000;

// Domain view-model for a single skill (full record, body included). Description + body are plaintext here for
// editing; they are encrypted at rest on the node. Timestamps are epoch milliseconds (long on the wire).
export interface Skill {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly body: string;
	readonly enabled: boolean;
	readonly version: number;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
}

// List/summary view-model: the list endpoint omits `body` for payload economy; the editor GETs the
// single skill to load its body. A summary is a Skill without the body field.
export interface SkillSummary {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly enabled: boolean;
	readonly version: number;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
}

// Form values are narrower than the persisted entity: identity/version/timestamps are backend-managed. `enabled`
// is edited here (mirrors the UpdateSkillRequest, which carries enabled) so the operator can toggle a skill in the
// editor; create defaults it to true (the store default).
export interface SkillFormValues {
	name: string;
	description: string;
	body: string;
	enabled: boolean;
}

// Zod schema validating the form before submit. Name is required and must match the MAF-safe pattern; description
// and body are required (non-empty after trim) and length-capped. The trimmed-then-pattern check matches the
// backend so an invalid name fails the same way client- and server-side.
export const skillFormSchema = z.object({
	name: z.string().trim().min(1).max(SKILL_NAME_MAX).regex(SKILL_NAME_PATTERN, { message: "skillNameInvalid" }),
	description: z.string().trim().min(1).max(SKILL_DESCRIPTION_MAX),
	body: z.string().trim().min(1).max(SKILL_BODY_MAX),
	enabled: z.boolean(),
});

export type SkillFormSchema = z.infer<typeof skillFormSchema>;
