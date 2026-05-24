import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
	axiosInstanceMock: {
		delete: vi.fn(),
		get: vi.fn(),
		put: vi.fn(),
	},
	buildLocalApiUrlMock: vi.fn((path: string) => `/local/${path}`),
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
	axiosInstance: axiosInstanceMock,
}));

vi.mock("@/core/api/utils/LocalApiUrl", () => ({
	buildLocalApiUrl: buildLocalApiUrlMock,
}));

import { clearCloudSettings, getCloudSettings, saveCloudSettings } from "@/features/cloud-settings/api/CloudSettingsApi";

describe("cloud settings API", () => {
	it("loads cloud settings without requiring secrets in responses", async () => {
		const settings = { providerName: "AzureFoundry", endpoint: "https://example.openai.azure.com/", deploymentName: "gpt-4o", hasStoredApiKey: true };
		axiosInstanceMock.get.mockResolvedValue({ data: settings });

		await expect(getCloudSettings()).resolves.toBe(settings);
		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/cloud-settings", undefined);
	});

	it("saves cloud settings through PUT", async () => {
		const settings = { providerName: "AzureFoundry", endpoint: "https://example.openai.azure.com/", deploymentName: "gpt-4o", hasStoredApiKey: true };
		const request = { providerName: "AzureFoundry" as const, endpoint: "https://example.openai.azure.com/", apiKey: "secret", deploymentName: "gpt-4o" };
		axiosInstanceMock.put.mockResolvedValue({ data: settings });

		await expect(saveCloudSettings(request)).resolves.toBe(settings);
		expect(axiosInstanceMock.put).toHaveBeenCalledWith("/local/cloud-settings", request, undefined);
	});

	it("clears cloud settings through DELETE", async () => {
		const settings = { providerName: "None", endpoint: null, deploymentName: null, hasStoredApiKey: false };
		axiosInstanceMock.delete.mockResolvedValue({ data: settings });

		await expect(clearCloudSettings()).resolves.toBe(settings);
		expect(axiosInstanceMock.delete).toHaveBeenCalledWith("/local/cloud-settings", undefined);
	});
});
