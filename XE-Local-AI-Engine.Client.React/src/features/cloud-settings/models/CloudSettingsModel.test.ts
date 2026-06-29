import { describe, expect, it } from "vitest";

import {
	type CloudSettingsFormValues,
	hasAtLeastOneModel,
	isHttpsAbsoluteUrl,
	validateCloudSettingsForm,
} from "@/features/cloud-settings/models/CloudSettingsModel";

function baseValues(overrides: Partial<CloudSettingsFormValues> = {}): CloudSettingsFormValues {
	return {
		endpoint: "https://example.openai.azure.com/",
		authMode: "ApiKey",
		apiKey: "secret-key",
		models: [{ deploymentName: "gpt-4o", displayLabel: "" }],
		...overrides,
	};
}

describe("cloud settings model", () => {
	it("accepts only absolute HTTPS URLs", () => {
		expect(isHttpsAbsoluteUrl("https://example.openai.azure.com/")).toBe(true);
		expect(isHttpsAbsoluteUrl("http://example.openai.azure.com/")).toBe(false);
		expect(isHttpsAbsoluteUrl("example.openai.azure.com")).toBe(false);
	});

	it("reports a models list valid only when a row carries a non-blank deployment name", () => {
		expect(hasAtLeastOneModel([{ deploymentName: "", displayLabel: "" }])).toBe(false);
		expect(hasAtLeastOneModel([{ deploymentName: "  ", displayLabel: "x" }])).toBe(false);
		expect(hasAtLeastOneModel([{ deploymentName: "gpt-4o", displayLabel: "" }])).toBe(true);
	});

	it("requires endpoint, API key, and at least one model in API-key mode", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				endpoint: "http://example.openai.azure.com/",
				apiKey: "",
				models: [{ deploymentName: "", displayLabel: "" }],
			}),
		);

		expect(errors.endpoint).toBeDefined();
		expect(errors.apiKey).toBeDefined();
		expect(errors.models).toBeDefined();
	});

	it("does not require the API key in managed-identity mode", () => {
		const errors = validateCloudSettingsForm(baseValues({ authMode: "ManagedIdentity", apiKey: "" }));

		expect(errors.apiKey).toBeUndefined();
		expect(errors.endpoint).toBeUndefined();
		expect(errors.models).toBeUndefined();
	});

	it("accepts a fully valid API-key connection", () => {
		expect(validateCloudSettingsForm(baseValues())).toEqual({});
	});
});
