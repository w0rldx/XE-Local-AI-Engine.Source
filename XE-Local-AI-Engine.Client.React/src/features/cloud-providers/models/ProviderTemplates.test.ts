import { describe, expect, it } from "vitest";

import { providerTemplateOptions } from "@/features/cloud-providers/models/ProviderTemplates";

const noSlots = { azureFoundryConfigured: false, codexSignedIn: false };

describe("providerTemplateOptions", () => {
	it("offers every template while both capabilities are on", () => {
		expect(providerTemplateOptions({ cloudSettings: true, externalProviders: true }, noSlots)).toEqual([
			{ id: "generic", blockedBy: null },
			{ id: "azureOpenAi", blockedBy: null },
			{ id: "azureFoundry", blockedBy: null },
			{ id: "codex", blockedBy: null },
		]);
	});

	it("offers only the OpenAI-compatible templates without the cloudSettings capability", () => {
		const ids = providerTemplateOptions({ cloudSettings: false, externalProviders: true }, noSlots).map((option) => option.id);

		expect(ids).toEqual(["generic", "azureOpenAi"]);
	});

	it("offers only Azure Foundry and Codex without the externalProviders capability", () => {
		const ids = providerTemplateOptions({ cloudSettings: true, externalProviders: false }, noSlots).map((option) => option.id);

		expect(ids).toEqual(["azureFoundry", "codex"]);
	});

	it("blocks a single-slot template whose slot is already taken", () => {
		const options = providerTemplateOptions(
			{ cloudSettings: true, externalProviders: true },
			{ azureFoundryConfigured: true, codexSignedIn: true },
		);

		expect(options.find((option) => option.id === "azureFoundry")?.blockedBy).toBe("azureFoundryConfigured");
		expect(options.find((option) => option.id === "codex")?.blockedBy).toBe("codexSignedIn");
		// Connections are not a slot: any number of them can be added.
		expect(options.filter((option) => option.blockedBy === null).map((option) => option.id)).toEqual(["generic", "azureOpenAi"]);
	});
});
