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
				license: "MIT",
				compatibility: "claude-code >=1.0",
				allowedTools: "read_file list_files",
				metadata: { author: "acme" },
				origin: "Imported",
				sourceUri: "github:microsoft/skills",
				importedAtUtc: 300,
				resourceCount: 2,
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
			license: "MIT",
			compatibility: "claude-code >=1.0",
			allowedTools: "read_file list_files",
			metadata: { author: "acme" },
			origin: "Imported",
			sourceUri: "github:microsoft/skills",
			importedAtUtc: 300,
			resourceCount: 2,
		});
	});

	it("defaults every omitted optional field", () => {
		expect(
			toSkill({ id: "", name: "", description: "", body: "", enabled: false, version: 0, createdAtUtc: 0, updatedAtUtc: 0, origin: "Local", resourceCount: 0 }),
		).toEqual({
			id: "",
			name: "",
			description: "",
			body: "",
			enabled: false,
			version: 0,
			createdAtUtc: 0,
			updatedAtUtc: 0,
			license: null,
			compatibility: null,
			allowedTools: null,
			metadata: null,
			origin: "Local",
			sourceUri: null,
			importedAtUtc: null,
			resourceCount: 0,
		});
	});
});

describe("toSkillSummary", () => {
	it("maps a summary response (no body field) into the domain summary", () => {
		expect(
			toSkillSummary({
				id: "skill-2",
				name: "legal-redline",
				description: "d",
				enabled: false,
				version: 1,
				createdAtUtc: 0,
				updatedAtUtc: 0,
				origin: "Imported",
				sourceUri: "upload",
				importedAtUtc: 42,
			}),
		).toEqual({
			id: "skill-2",
			name: "legal-redline",
			description: "d",
			enabled: false,
			version: 1,
			createdAtUtc: 0,
			updatedAtUtc: 0,
			license: null,
			compatibility: null,
			allowedTools: null,
			metadata: null,
			origin: "Imported",
			sourceUri: "upload",
			importedAtUtc: 42,
		});
	});
});

const form: SkillFormValues = {
	name: "  invoice-review  ",
	description: "  How to review  ",
	body: "  # Body  ",
	enabled: false,
	license: "  MIT  ",
	compatibility: "   ",
	allowedTools: "  read_file  ",
	metadata: { author: "acme" },
};

describe("toCreateSkillRequest", () => {
	it("trims the fields and omits enabled (create has no enabled on the wire)", () => {
		expect(toCreateSkillRequest(form)).toEqual({
			name: "invoice-review",
			description: "How to review",
			body: "# Body",
			license: "MIT",
			// A blank frontmatter field is absent, not a stored empty string.
			compatibility: null,
			allowedTools: "read_file",
			metadata: { author: "acme" },
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
			license: "MIT",
			compatibility: null,
			allowedTools: "read_file",
			metadata: { author: "acme" },
		});
	});

	// The update endpoint is a FULL REPLACE: if the mapper dropped these, editing an imported skill's body would
	// silently wipe its license/compatibility/allowed-tools/metadata.
	it("round-trips frontmatter so an edit never strips it", () => {
		const request = toUpdateSkillRequest({ ...form, compatibility: "claude-code >=1.0" });

		expect(request.license).toBe("MIT");
		expect(request.compatibility).toBe("claude-code >=1.0");
		expect(request.allowedTools).toBe("read_file");
		expect(request.metadata).toEqual({ author: "acme" });
	});
});
