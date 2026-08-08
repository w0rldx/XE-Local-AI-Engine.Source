import { z } from "zod";

// Agent Skills specification name: lowercase letters/digits separated by SINGLE hyphens; no leading, trailing or
// consecutive hyphens. This is a client-side convenience only — the backend delegates to MAF's own
// AgentSkillFrontmatter.ValidateName and stays authoritative. The earlier pattern
// /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/ accepted consecutive hyphens, which MAF rejects, so a name like `foo--bar`
// passed both this check and the backend and then threw when the skill was built into an agent.
export const SKILL_NAME_PATTERN = /^[a-z0-9]+(-[a-z0-9]+)*$/;

// Length caps mirror the backend: Name ≤64, Description ≤1024, Body ≤20000 (matches the instructions cap), and the
// frontmatter caps in AgentSkillService (License 200, Compatibility 500, AllowedTools 1024).
export const SKILL_NAME_MAX = 64;
export const SKILL_DESCRIPTION_MAX = 1024;
export const SKILL_BODY_MAX = 20000;
const SKILL_LICENSE_MAX = 200;
const SKILL_COMPATIBILITY_MAX = 500;
const SKILL_ALLOWED_TOOLS_MAX = 1024;

// Authoring guidance from the Agent Skills specification (NOT a hard cap — the backend accepts a longer body up to
// SKILL_BODY_MAX). A body past either figure is worth trimming: it burns the agent's context on every load.
export const SKILL_BODY_GUIDANCE_LINES = 500;
export const SKILL_BODY_GUIDANCE_TOKENS = 5000;

/** Provenance of a skill row. `Imported` skills carry third-party content this node never validated. */
export type SkillOrigin = "Local" | "Imported";

// Frontmatter carried by both the summary and the full record. `allowedTools` is a SPACE-DELIMITED string exactly as
// authored — it is DISPLAY ONLY on this node: it neither grants nor restricts any tool at run time.
export interface SkillFrontmatter {
	readonly license: string | null;
	readonly compatibility: string | null;
	readonly allowedTools: string | null;
	readonly metadata: Readonly<Record<string, string>> | null;
}

// Where a skill came from. `sourceUri` is one of exactly two values — `upload` or `github:owner/repo` — enforced by
// AgentSkillStore.ValidateSourceUri, which throws on anything else. A pasted SKILL.md is recorded as `upload`; there
// is no `paste` source, so do not switch on one. For an upload the kind is all that is stored: an operator-chosen
// filename would be the one plaintext string in a table where everything else is AEAD-encrypted.
export interface SkillProvenance {
	readonly origin: SkillOrigin;
	readonly sourceUri: string | null;
	readonly importedAtUtc: number | null;
}

// Domain view-model for a single skill (full record, body included). Description + body are plaintext here for
// editing; they are encrypted at rest on the node. Timestamps are epoch milliseconds (long on the wire).
export interface Skill extends SkillFrontmatter, SkillProvenance {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly body: string;
	readonly enabled: boolean;
	readonly version: number;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
	readonly resourceCount: number;
}

// List/summary view-model: the list endpoint omits `body` for payload economy; the editor GETs the
// single skill to load its body. A summary is a Skill without the body field. It also omits `resourceCount`
// deliberately — the list projection cannot populate it, so exposing it here would surface a constant zero.
export interface SkillSummary extends SkillFrontmatter, SkillProvenance {
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
// editor; create defaults it to true (the store default). The frontmatter fields ride along because the update
// endpoint is a FULL REPLACE (SkillMapper.ToInput passes them straight through): omitting them from an edit would
// silently strip an imported skill's license/compatibility/allowed-tools/metadata.
export interface SkillFormValues {
	name: string;
	description: string;
	body: string;
	enabled: boolean;
	license: string;
	compatibility: string;
	allowedTools: string;
	metadata: Readonly<Record<string, string>> | null;
}

// Zod schema validating the form before submit. Name is required and must match the MAF-safe pattern; description
// and body are required (non-empty after trim) and length-capped. The trimmed-then-pattern check matches the
// backend so an invalid name fails the same way client- and server-side. Frontmatter fields are optional (empty
// string = absent) and only length-capped, mirroring the backend's own validation.
export const skillFormSchema = z.object({
	name: z.string().trim().min(1).max(SKILL_NAME_MAX).regex(SKILL_NAME_PATTERN, { message: "skillNameInvalid" }),
	description: z.string().trim().min(1).max(SKILL_DESCRIPTION_MAX),
	body: z.string().trim().min(1).max(SKILL_BODY_MAX),
	enabled: z.boolean(),
	license: z.string().trim().max(SKILL_LICENSE_MAX),
	compatibility: z.string().trim().max(SKILL_COMPATIBILITY_MAX),
	allowedTools: z.string().trim().max(SKILL_ALLOWED_TOOLS_MAX),
	metadata: z.record(z.string(), z.string()).nullable(),
});

/** True when a stored name would be REJECTED by MAF and therefore dropped at resolve time (the operator must rename
 * it). Existing rows predate the tightened pattern, so the list flags them rather than silently ignoring them. */
export function isSkillNameResolvable(name: string): boolean {
	return SKILL_NAME_PATTERN.test(name);
}

export type SkillFormSchema = z.infer<typeof skillFormSchema>;
