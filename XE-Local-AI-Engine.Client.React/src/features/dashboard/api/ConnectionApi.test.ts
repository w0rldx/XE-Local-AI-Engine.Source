import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
	axiosInstanceMock: {
		get: vi.fn(),
		post: vi.fn(),
	},
	buildLocalApiUrlMock: vi.fn((path: string) => `/local/${path}`),
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
	axiosInstance: axiosInstanceMock,
}));

vi.mock("@/core/api/utils/LocalApiUrl", () => ({
	buildLocalApiUrl: buildLocalApiUrlMock,
}));

import { connectWorker, disableAutoConnect, disconnectWorker, enableAutoConnect, getConnectionStatus } from "@/features/dashboard/api/ConnectionApi";

describe("connection dashboard API", () => {
	it("loads connection status from the local API", async () => {
		const status = { state: "disconnected", lastUpdatedAt: new Date(0).toISOString(), isPaired: false };
		axiosInstanceMock.get.mockResolvedValue({ data: status });

		await expect(getConnectionStatus()).resolves.toBe(status);
		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/connection", undefined);
	});

	it("posts connection actions to explicit local endpoints", async () => {
		const status = { state: "connected", lastUpdatedAt: new Date(0).toISOString(), isPaired: true };
		axiosInstanceMock.post.mockResolvedValue({ data: status });

		await connectWorker();
		await disconnectWorker();
		await enableAutoConnect();
		await disableAutoConnect();

		expect(axiosInstanceMock.post).toHaveBeenNthCalledWith(1, "/local/connection/connect", undefined, undefined);
		expect(axiosInstanceMock.post).toHaveBeenNthCalledWith(2, "/local/connection/disconnect", undefined, undefined);
		expect(axiosInstanceMock.post).toHaveBeenNthCalledWith(3, "/local/connection/auto-connect/enable", undefined, undefined);
		expect(axiosInstanceMock.post).toHaveBeenNthCalledWith(4, "/local/connection/auto-connect/disable", undefined, undefined);
	});
});
