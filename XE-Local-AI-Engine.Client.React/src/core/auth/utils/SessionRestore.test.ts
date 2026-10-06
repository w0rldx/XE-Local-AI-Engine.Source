import { AxiosError, AxiosHeaders } from "axios";
import { beforeEach, describe, expect, it, vi } from "vitest";

type SessionRestoreModule = typeof import("@/core/auth/utils/SessionRestore");

const { authApiMock } = vi.hoisted(() => ({
	authApiMock: {
		getNodeAuthStatus: vi.fn(),
		refreshNodeAuthToken: vi.fn(),
	},
}));

vi.mock("@/core/auth/api/NodeAuthApi", () => authApiMock);

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";

function refreshRejection(status: number): AxiosError {
	return new AxiosError("Refresh failed", AxiosError.ERR_BAD_REQUEST, undefined, undefined, {
		data: undefined,
		status,
		statusText: "",
		headers: {},
		config: { headers: new AxiosHeaders() },
	});
}

// A set token ends a settled sign-out, so each test starts from a fresh restore.
function resetAuth(): void {
	vi.clearAllMocks();
	useNodeAuthStore.getState().actions.setToken({ accessToken: "reset", expiresAtUtc: "2026-05-25T12:00:00Z" });
	useNodeAuthStore.getState().actions.clear();
}

describe("restoreNodeAuthSession", () => {
	beforeEach(resetAuth);

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

	it("returns unauthenticated and clears token when refresh answers 401", async () => {
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old", expiresAtUtc: "2026-05-25T12:00:00Z" });
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false });
		authApiMock.refreshNodeAuthToken.mockRejectedValue(refreshRejection(401));

		await expect(restoreNodeAuthSession()).resolves.toBe("unauthenticated");

		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
	});

	// F-02: a rate-limited refresh signed the operator out although the refresh cookie was still valid.
	it("keeps the session and rethrows when refresh is rate limited", async () => {
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old", expiresAtUtc: "2026-05-25T12:00:00Z" });
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false });
		const rejection = refreshRejection(429);
		authApiMock.refreshNodeAuthToken.mockRejectedValue(rejection);

		await expect(restoreNodeAuthSession()).rejects.toBe(rejection);

		expect(useNodeAuthStore.getState().accessToken).toBe("old");
	});

	// The layout's failed restore is followed straight away by /login's guard; a second refresh there doubled the calls.
	it("reuses a settled unauthenticated result until a token is set", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false });
		authApiMock.refreshNodeAuthToken.mockRejectedValue(refreshRejection(401));

		await expect(restoreNodeAuthSession()).resolves.toBe("unauthenticated");
		await expect(restoreNodeAuthSession()).resolves.toBe("unauthenticated");
		expect(authApiMock.refreshNodeAuthToken).toHaveBeenCalledTimes(1);

		useNodeAuthStore.getState().actions.setToken({ accessToken: "signed-in", expiresAtUtc: "2026-05-25T12:00:00Z" });
		useNodeAuthStore.getState().actions.clear();
		authApiMock.refreshNodeAuthToken.mockResolvedValue({ accessToken: "new-token", expiresAtUtc: "2026-05-25T12:15:00Z" });

		await expect(restoreNodeAuthSession()).resolves.toBe("authenticated");
		expect(authApiMock.refreshNodeAuthToken).toHaveBeenCalledTimes(2);
	});
});

describe("restoreNodeAuthSession vault states", () => {
	beforeEach(resetAuth);

	it("returns vault-locked without trying to refresh", async () => {
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old", expiresAtUtc: "2026-05-25T12:00:00Z" });
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false, vault: "locked" });

		await expect(restoreNodeAuthSession()).resolves.toBe("vault-locked");

		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
		expect(authApiMock.refreshNodeAuthToken).not.toHaveBeenCalled();
	});

	it("returns vault-setup-required for a signed-in operator of a pending node with an admin", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false, vault: "pending" });
		authApiMock.refreshNodeAuthToken.mockResolvedValue({ accessToken: "new-token", expiresAtUtc: "2026-05-25T12:15:00Z" });

		await expect(restoreNodeAuthSession()).resolves.toBe("vault-setup-required");

		expect(useNodeAuthStore.getState().accessToken).toBe("new-token");
	});

	it("returns unauthenticated for a signed-out visitor of a pending node", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false, vault: "pending" });
		authApiMock.refreshNodeAuthToken.mockRejectedValue(refreshRejection(401));

		await expect(restoreNodeAuthSession()).resolves.toBe("unauthenticated");
	});

	// A fresh install is pending too, but /setup wraps the key itself.
	it("returns setup-required for a fresh install", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: true, authenticated: false, vault: "pending" });

		await expect(restoreNodeAuthSession()).resolves.toBe("setup-required");
	});
});

// The module memoizes "the vault is settled", so each test gets a fresh copy.
describe("getPendingVaultStep", () => {
	let module: SessionRestoreModule;

	beforeEach(async () => {
		vi.clearAllMocks();
		vi.resetModules();
		module = await import("@/core/auth/utils/SessionRestore");
	});

	it("reports the confirm step owed by a pending node with an admin", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: true, vault: "pending" });

		await expect(module.getPendingVaultStep()).resolves.toBe("vault-setup-required");
	});

	it("reports a locked vault", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false, vault: "locked" });

		await expect(module.getPendingVaultStep()).resolves.toBe("vault-locked");
	});

	it("stops asking once the vault has been seen unlocked", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: true, vault: "unlocked" });

		await expect(module.getPendingVaultStep()).resolves.toBeUndefined();
		await expect(module.getPendingVaultStep()).resolves.toBeUndefined();

		expect(authApiMock.getNodeAuthStatus).toHaveBeenCalledTimes(1);
	});

	it("keeps asking while the vault is pending", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValueOnce({ setupRequired: false, authenticated: true, vault: "pending" });
		authApiMock.getNodeAuthStatus.mockResolvedValueOnce({ setupRequired: false, authenticated: true, vault: "unlocked" });

		await expect(module.getPendingVaultStep()).resolves.toBe("vault-setup-required");
		await expect(module.getPendingVaultStep()).resolves.toBeUndefined();
	});

	// Fails open like the layout's settings guard: a throw would make every authenticated page unreachable.
	it("lets the session through when the status read fails, and asks again next time", async () => {
		authApiMock.getNodeAuthStatus.mockRejectedValueOnce(new Error("503"));
		authApiMock.getNodeAuthStatus.mockResolvedValueOnce({ setupRequired: false, authenticated: true, vault: "pending" });

		await expect(module.getPendingVaultStep()).resolves.toBeUndefined();
		await expect(module.getPendingVaultStep()).resolves.toBe("vault-setup-required");
	});
});
