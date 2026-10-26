import { describe, expect, it } from "vitest";

import { emptyFormValues } from "@/features/cloud-providers/external/models/ExternalProviderFormState";
import { externalTemplateValues, providerTemplateOptions } from "@/features/cloud-providers/models/ProviderTemplates";

const noSlots = { azureFoundryConfigured: false, codexSignedIn: false };

describe("providerTemplateOptions", () => {
	it("offers every template while both capabilities are on", () => {
		expect(providerTemplateOptions({ cloudSettings: true, externalProviders: true }, noSlots)).toEqual([
			{ id: "generic", blockedBy: null },
			{ id: "gateway", blockedBy: null },
			{ id: "azureOpenAi", blockedBy: null },
			{ id: "azureFoundry", blockedBy: null },
			{ id: "codex", blockedBy: null },
		]);
	});

	it("offers only the OpenAI-compatible templates without the cloudSettings capability", () => {
		const ids = providerTemplateOptions({ cloudSettings: false, externalProviders: true }, noSlots).map((option) => option.id);

		expect(ids).toEqual(["generic", "gateway", "azureOpenAi"]);
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
		expect(options.filter((option) => option.blockedBy === null).map((option) => option.id)).toEqual([
			"generic",
			"gateway",
			"azureOpenAi",
		]);
	});
});

describe("externalTemplateValues", () => {
	it("starts the gateway template declared Cloud with one non-secret project-id header row", () => {
		const values = externalTemplateValues("gateway");

		expect(values.locality).toBe("Cloud");
		expect(values.headers).toEqual([{ name: "project-id", value: "", isSecret: false, hasStoredSecret: false }]);
		// The base URL is never guessed; the editor shows it as a placeholder only.
		expect(values.baseUrl).toBe("");
	});

	it("starts the generic and Azure OpenAI templates as the empty form", () => {
		expect(externalTemplateValues("generic")).toEqual(emptyFormValues);
		expect(externalTemplateValues("azureOpenAi")).toEqual(emptyFormValues);
	});
});
