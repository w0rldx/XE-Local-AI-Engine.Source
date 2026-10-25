import { AxiosError, AxiosHeaders } from "axios";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { authApiMock, loginNavigationMock } = vi.hoisted(() => ({
	authApiMock: { refreshNodeAuthToken: vi.fn() },
	loginNavigationMock: { redirectToLoginOnce: vi.fn() },
}));

vi.mock("@/core/auth/api/NodeAuthApi", () => authApiMock);
vi.mock("@/core/api/axios/LoginNavigation", () => loginNavigationMock);

import { markHubConnectionLost, resolveHubAccessToken } from "@/core/api/signalr/ResolveHubAccessToken";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

function freshToken(accessToken: string) {
	return { accessToken, expiresAtUtc: new Date(Date.now() + 15 * 60_000).toISOString() };
}

function refreshRejected(status: number): AxiosError {
	const config = { headers: new AxiosHeaders() };
	return new AxiosError("refresh failed", AxiosError.ERR_BAD_REQUEST, config, undefined, {
		data: undefined,
		status,
		statusText: String(status),
		headers: {},
		config,
	});
}

describe("resolveHubAccessToken after a dropped hub connection", () => {
	beforeEach(async () => {
		// The drop count is module state: settle whatever an earlier test left with one successful refresh.
		markHubConnectionLost();
		authApiMock.refreshNodeAuthToken.mockResolvedValueOnce(freshToken("held"));
		await resolveHubAccessToken();
		vi.resetAllMocks();
	});

	it("reuses an unexpired token while no connection dropped", async () => {
		await expect(resolveHubAccessToken()).resolves.toBe("held");
		expect(authApiMock.refreshNodeAuthToken).not.toHaveBeenCalled();
	});

	it("refreshes once after a drop even though the held token has not expired", async () => {
		markHubConnectionLost();
		authApiMock.refreshNodeAuthToken.mockResolvedValueOnce(freshToken("renewed"));

		await expect(resolveHubAccessToken()).resolves.toBe("renewed");
		await expect(resolveHubAccessToken()).resolves.toBe("renewed");
		expect(authApiMock.refreshNodeAuthToken).toHaveBeenCalledTimes(1);
	});

	it("signs out and goes to sign-in when the refresh after a drop answers 401", async () => {
		markHubConnectionLost();
		authApiMock.refreshNodeAuthToken.mockRejectedValueOnce(refreshRejected(401));

		await expect(resolveHubAccessToken()).resolves.toBe("");
		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
		expect(loginNavigationMock.redirectToLoginOnce).toHaveBeenCalledTimes(1);
	});

	it("keeps the session when the refresh after a drop fails for another reason", async () => {
		markHubConnectionLost();
		authApiMock.refreshNodeAuthToken.mockRejectedValueOnce(refreshRejected(503));

		await expect(resolveHubAccessToken()).resolves.toBe("held");
		expect(loginNavigationMock.redirectToLoginOnce).not.toHaveBeenCalled();
	});
});
