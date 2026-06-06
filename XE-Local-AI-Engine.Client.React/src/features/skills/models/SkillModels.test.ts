import { describe, expect, it } from "vitest";

import {
	SKILL_BODY_MAX,
	SKILL_DESCRIPTION_MAX,
	SKILL_NAME_MAX,
	SKILL_NAME_PATTERN,
	skillFormSchema,
} from "@/features/skills/models/SkillModels";

const valid = { name: "invoice-review", description: "How to review", body: "# Body", enabled: true };

describe("SKILL_NAME_PATTERN", () => {
	it.each(["a", "abc", "a1", "invoice-review", "invoice--review", "ab-cd-ef", "x9z"])("accepts the MAF-safe name %s", (name) => {
		expect(SKILL_NAME_PATTERN.test(name)).toBe(true);
	});

	it.each(["", "-bad", "bad-", "Bad", "with space", "under_score", "Über", "a/b"])("rejects the invalid name %s", (name) => {
		expect(SKILL_NAME_PATTERN.test(name)).toBe(false);
	});
});

describe("skillFormSchema", () => {
	it("accepts a fully valid form", () => {
		const result = skillFormSchema.safeParse(valid);
		expect(result.success).toBe(true);
	});

	it("trims name, description and body on parse", () => {
		const result = skillFormSchema.safeParse({
			name: "  invoice-review  ",
			description: "  desc  ",
			body: "  body  ",
			enabled: true,
		});
		expect(result.success).toBe(true);
		if (result.success) {
			expect(result.data).toMatchObject({ name: "invoice-review", description: "desc", body: "body" });
		}
	});

	it("flags an empty name as required", () => {
		const result = skillFormSchema.safeParse({ ...valid, name: "" });
		expect(result.success).toBe(false);
	});

	it("flags an invalid (uppercase) name with the skillNameInvalid message", () => {
		const result = skillFormSchema.safeParse({ ...valid, name: "BadName" });
		expect(result.success).toBe(false);
		if (!result.success) {
			expect(result.error.issues.some((issue) => issue.message === "skillNameInvalid")).toBe(true);
		}
	});

	it("flags a blank description and a blank body", () => {
		expect(skillFormSchema.safeParse({ ...valid, description: "   " }).success).toBe(false);
		expect(skillFormSchema.safeParse({ ...valid, body: "" }).success).toBe(false);
	});

	it("enforces the length caps", () => {
		expect(skillFormSchema.safeParse({ ...valid, name: "a".repeat(SKILL_NAME_MAX + 1) }).success).toBe(false);
		expect(skillFormSchema.safeParse({ ...valid, description: "d".repeat(SKILL_DESCRIPTION_MAX + 1) }).success).toBe(false);
		expect(skillFormSchema.safeParse({ ...valid, body: "b".repeat(SKILL_BODY_MAX + 1) }).success).toBe(false);
	});
});
