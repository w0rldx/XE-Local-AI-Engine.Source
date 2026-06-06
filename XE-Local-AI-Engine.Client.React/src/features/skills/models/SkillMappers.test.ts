import { describe, expect, it } from "vitest";

import { toCreateSkillRequest, toSkill, toSkillSummary, toUpdateSkillRequest } from "@/features/skills/models/SkillMappers";
import type { SkillFormValues } from "@/features/skills/models/SkillModels";

describe("toSkill", () => {
	it("coalesces a full response into the domain shape", () => {
		expect(
			toSkill({
				id: "skill-1",
				name: "invoice-review",
				description: "How to review",
				body: "# Body",
				enabled: true,
				version: 3,
				createdAtUtc: 100,
				updatedAtUtc: 200,
			}),
		).toEqual({
			id: "skill-1",
			name: "invoice-review",
			description: "How to review",
			body: "# Body",
			enabled: true,
			version: 3,
			createdAtUtc: 100,
			updatedAtUtc: 200,
		});
	});

	it("defaults every omitted optional field", () => {
		expect(toSkill({})).toEqual({
			id: "",
			name: "",
			description: "",
			body: "",
			enabled: false,
			version: 0,
			createdAtUtc: 0,
			updatedAtUtc: 0,
		});
	});
});

describe("toSkillSummary", () => {
	it("maps a summary response (no body field) into the domain summary", () => {
		expect(toSkillSummary({ id: "skill-2", name: "legal-redline", description: "d", enabled: false, version: 1 })).toEqual({
			id: "skill-2",
			name: "legal-redline",
			description: "d",
			enabled: false,
			version: 1,
			createdAtUtc: 0,
			updatedAtUtc: 0,
		});
	});
});

const form: SkillFormValues = {
	name: "  invoice-review  ",
	description: "  How to review  ",
	body: "  # Body  ",
	enabled: false,
};

describe("toCreateSkillRequest", () => {
	it("trims the fields and omits enabled (create has no enabled on the wire)", () => {
		expect(toCreateSkillRequest(form)).toEqual({
			name: "invoice-review",
			description: "How to review",
			body: "# Body",
		});
	});
});

describe("toUpdateSkillRequest", () => {
	it("trims the fields and carries the enabled flag", () => {
		expect(toUpdateSkillRequest(form)).toEqual({
			name: "invoice-review",
			description: "How to review",
			body: "# Body",
			enabled: false,
		});
	});
});
