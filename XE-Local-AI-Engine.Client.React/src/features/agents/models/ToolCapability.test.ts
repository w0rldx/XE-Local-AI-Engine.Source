import { describe, expect, it } from "vitest";

import { isModelToolCapable } from "@/features/agents/models/ToolCapability";

describe("isModelToolCapable", () => {
	it("does not enforce when the capable-model list is empty (source unavailable)", () => {
		expect(isModelToolCapable("any-model", [])).toBe(true);
	});

	it("treats the node default (null/blank model) as capable", () => {
		expect(isModelToolCapable(null, ["qwen3:8b"])).toBe(true);
		expect(isModelToolCapable("   ", ["qwen3:8b"])).toBe(true);
	});

	it("returns true for a model in the capable list", () => {
		expect(isModelToolCapable("qwen3:8b", ["qwen3:8b", "qwen3:14b"])).toBe(true);
	});

	it("returns false for a model absent from a non-empty capable list", () => {
		expect(isModelToolCapable("llama3:8b", ["qwen3:8b"])).toBe(false);
	});
});
