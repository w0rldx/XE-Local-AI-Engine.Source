import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
	axiosInstanceMock: {
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

import { getNodeSettings, saveNodeSettings } from "@/features/node-settings/api/NodeSettingsApi";

describe("node settings API", () => {
	it("loads node settings from the local API", async () => {
		const settings = {
			maxMessageRequestTimeoutSeconds: 300,
			minMessageRequestTimeoutSeconds: 5,
			maxAllowedMessageRequestTimeoutSeconds: 3600,
		};
		axiosInstanceMock.get.mockResolvedValue({ data: settings });

		await expect(getNodeSettings()).resolves.toBe(settings);
		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/node-settings", undefined);
	});

	it("saves node settings through PUT", async () => {
		const settings = {
			maxMessageRequestTimeoutSeconds: 600,
			minMessageRequestTimeoutSeconds: 5,
			maxAllowedMessageRequestTimeoutSeconds: 3600,
		};
		const request = { maxMessageRequestTimeoutSeconds: 600 };
		axiosInstanceMock.put.mockResolvedValue({ data: settings });

		await expect(saveNodeSettings(request)).resolves.toBe(settings);
		expect(axiosInstanceMock.put).toHaveBeenCalledWith("/local/node-settings", request, undefined);
	});
});
