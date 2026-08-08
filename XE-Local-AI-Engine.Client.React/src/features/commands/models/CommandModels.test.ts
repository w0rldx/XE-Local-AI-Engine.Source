import { describe, expect, it } from "vitest";

import { commandFormSchema } from "@/features/commands/models/CommandModels";

function values(description: string, prompt: string) {
	return { name: "review", description, actionType: "SendPrompt" as const, prompt };
}

describe("commandFormSchema UTF-8 limits", () => {
	it("accepts values whose trimmed UTF-8 payload is exactly at each byte limit", () => {
		expect(commandFormSchema.safeParse(values("ä".repeat(512), "ä".repeat(10_000))).success).toBe(true);
	});

	it("rejects multibyte values that fit the character count but exceed the backend byte limits", () => {
		const description = commandFormSchema.safeParse(values("ä".repeat(513), "prompt"));
		const prompt = commandFormSchema.safeParse(values("description", "ä".repeat(10_001)));
		expect(description.success).toBe(false);
		expect(prompt.success).toBe(false);
	});

	it("applies the byte limit after trimming", () => {
		expect(commandFormSchema.safeParse(values(`  ${"ä".repeat(512)}  `, `  ${"ä".repeat(10_000)}  `)).success).toBe(true);
	});
});
