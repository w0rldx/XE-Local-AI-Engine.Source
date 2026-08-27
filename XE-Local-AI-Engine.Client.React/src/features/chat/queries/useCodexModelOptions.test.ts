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

	// The backend's Azure mapper sets IsReasoningCapable = false, but this factory used to hard-code `true`. That gave
	// every Azure deployment the "Reasoning" pill and — because isCloud is true — the full six-level Codex effort menu,
	// none of which the pipeline reads: InvocationAgentFactory only writes the reasoning side channel inside
	// `if (definition.SupportsThinking)`. The picker must not advertise a control the runtime cannot honour.
	it("does not advertise reasoning by default, because the backend reports Azure deployments as not reasoning-capable", () => {
		const option = toAzureFoundryModelOption("gpt-4o");

		expect(option.isReasoningModel).toBe(false);
	});

	it("honours the backend capability flags rather than asserting its own", () => {
		const option = toAzureFoundryModelOption("gpt-4o", { isReasoningCapable: true, isToolCapable: false });

		expect(option.isReasoningModel).toBe(true);
		expect(option.isToolCapable).toBe(false);
	});
});

describe("cloud options honour the backend as the capability authority", () => {
	it("lets the DTO turn Codex reasoning off", () => {
		// Guards the same class in the other direction: if the backend ever reports a non-reasoning Codex model, the
		// picker must follow it instead of keeping its own optimistic literal.
		const option = toCloudModelOption("gpt-5.3-codex-spark", { isReasoningCapable: false });

		expect(option.isReasoningModel).toBe(false);
	});
});

describe("toAzureFoundryModelOption — operator display label", () => {
	it("prefers the operator's display label over the raw deployment name", () => {
		const option = toAzureFoundryModelOption("gpt-4o-prod-eastus", { displayLabel: "GPT-4o (prod)" });

		expect(option.displayName).toBe("GPT-4o (prod)");
		// The value still routes by deployment name — only what the picker SHOWS changes.
		expect(option.value).toBe("gpt-4o-prod-eastus");
	});

	it("falls back to the deployment name when no label is configured", () => {
		expect(toAzureFoundryModelOption("gpt-4o", { displayLabel: null }).displayName).toBe("gpt-4o");
		expect(toAzureFoundryModelOption("gpt-4o").displayName).toBe("gpt-4o");
	});
});
