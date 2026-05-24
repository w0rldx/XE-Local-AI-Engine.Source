import { describe, expect, it } from "vitest";

import { isHttpsAbsoluteUrl, validateCloudSettingsForm } from "@/features/cloud-settings/models/CloudSettingsModel";

describe("cloud settings model", () => {
	it("accepts only absolute HTTPS URLs", () => {
		expect(isHttpsAbsoluteUrl("https://example.openai.azure.com/")).toBe(true);
		expect(isHttpsAbsoluteUrl("http://example.openai.azure.com/")).toBe(false);
		expect(isHttpsAbsoluteUrl("example.openai.azure.com")).toBe(false);
	});

	it("requires endpoint, API key, and deployment name", () => {
		const errors = validateCloudSettingsForm({ endpoint: "http://example.openai.azure.com/", apiKey: "", deploymentName: "" });

		expect(errors.endpoint).toBeDefined();
		expect(errors.apiKey).toBeDefined();
		expect(errors.deploymentName).toBeDefined();
	});
});
