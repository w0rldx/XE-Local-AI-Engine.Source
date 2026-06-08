import { describe, expect, it } from "vitest";

import { toCloudModelOption } from "@/features/chat/queries/useCodexModelOptions";

describe("toCloudModelOption — Codex model shape", () => {
	it("marks Codex model options as tool-capable", () => {
		const option = toCloudModelOption("gpt-5.3-codex-spark");

		expect(option.isToolCapable).toBe(true);
	});

	it("marks Codex model options as reasoning-capable", () => {
		const option = toCloudModelOption("gpt-5.3-codex-spark");

		expect(option.isReasoningModel).toBe(true);
	});

	it("marks Codex model options as cloud", () => {
		const option = toCloudModelOption("gpt-5.3-codex-spark");

		expect(option.isCloud).toBe(true);
	});

	it("always reports available (cloud availability is determined by sign-in, not Ollama status)", () => {
		const option = toCloudModelOption("gpt-5.3-codex-spark");

		expect(option.isAvailable).toBe(true);
	});

	it("uses the model name as value, label, and displayName", () => {
		const option = toCloudModelOption("gpt-5.3-codex-spark");

		expect(option.value).toBe("gpt-5.3-codex-spark");
		expect(option.label).toBe("gpt-5.3-codex-spark");
		expect(option.displayName).toBe("gpt-5.3-codex-spark");
	});
});
