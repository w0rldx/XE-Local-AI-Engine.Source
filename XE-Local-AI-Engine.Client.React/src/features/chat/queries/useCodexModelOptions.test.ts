import { describe, expect, it } from "vitest";

import { toAzureFoundryModelOption, toCloudModelOption } from "@/features/chat/queries/useCodexModelOptions";

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

	it("tags Codex options with the CodexOAuth provider", () => {
		expect(toCloudModelOption("gpt-5.3-codex-spark").provider).toBe("CodexOAuth");
	});
});

describe("toAzureFoundryModelOption — Azure deployment shape", () => {
	it("tags Azure options with the AzureFoundry provider and marks them cloud + tool-capable", () => {
		const option = toAzureFoundryModelOption("gpt-4o");

		expect(option.provider).toBe("AzureFoundry");
		expect(option.isCloud).toBe(true);
		expect(option.isToolCapable).toBe(true);
		expect(option.isAvailable).toBe(true);
	});

	it("uses the deployment name as value, label, and displayName", () => {
		const option = toAzureFoundryModelOption("gpt-4o");

		expect(option.value).toBe("gpt-4o");
		expect(option.label).toBe("gpt-4o");
		expect(option.displayName).toBe("gpt-4o");
	});
});
