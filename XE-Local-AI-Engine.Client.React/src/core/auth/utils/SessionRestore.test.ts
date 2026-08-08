import { beforeEach, describe, expect, it, vi } from "vitest";

const { authApiMock } = vi.hoisted(() => ({
	authApiMock: {
		getNodeAuthStatus: vi.fn(),
		refreshNodeAuthToken: vi.fn(),
	},
}));

vi.mock("@/core/auth/api/NodeAuthApi", () => authApiMock);

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";

describe("restoreNodeAuthSession", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeAuthStore.getState().actions.clear();
	});

	it("returns setup-required and clears local token when setup is needed", async () => {
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old", expiresAtUtc: "2026-05-25T12:00:00Z" });
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: true, authenticated: false });

		await expect(restoreNodeAuthSession()).resolves.toBe("setup-required");

		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
		expect(authApiMock.refreshNodeAuthToken).not.toHaveBeenCalled();
	});

	it("refreshes and stores a new access token when setup is complete", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false });
		authApiMock.refreshNodeAuthToken.mockResolvedValue({ accessToken: "new-token", expiresAtUtc: "2026-05-25T12:15:00Z" });

		await expect(restoreNodeAuthSession()).resolves.toBe("authenticated");

		expect(useNodeAuthStore.getState().accessToken).toBe("new-token");
	});

	it("returns unauthenticated and clears token when refresh fails", async () => {
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old", expiresAtUtc: "2026-05-25T12:00:00Z" });
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false });
		authApiMock.refreshNodeAuthToken.mockRejectedValue(new Error("expired"));

		await expect(restoreNodeAuthSession()).resolves.toBe("unauthenticated");

		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
	});
});
