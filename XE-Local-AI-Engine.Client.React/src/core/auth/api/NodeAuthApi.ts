import axios, { type AxiosRequestConfig } from "axios";

import { createClient, createConfig } from "@/core/api/generated/client";
import {
	nodeAuthStatus,
	nodeChangePassword,
	nodeLogin,
	nodeLogout,
	nodeRefresh,
	nodeSetup,
	nodeVaultConfirm,
} from "@/core/api/generated/sdk.gen";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import type {
	NodeAccessTokenResponse,
	NodeAuthStatusResponse,
	NodeChangePasswordRequest,
	NodeLoginRequest,
	NodeSetupRequest,
	NodeSetupResponse,
	NodeVaultConfirmResponse,
	NodeVaultPasswordRequest,
	NodeVaultRecoveryUnlockRequest,
	NodeVaultRecoveryUnlockResponse,
} from "@/core/auth/models/NodeAuthModels";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// A plain instance with NO interceptors, kept apart from the shared one: a failed login or refresh must not enter
// the 401 refresh/replay path, and a 429 must not toast on top of the page's own message.
// `baseURL` MUST stay "" (not "/"): see AxiosInstance.ts for the protocol-relative URL it would produce.
const authAxios = axios.create({
	baseURL: "",
	withCredentials: true,
	headers: {
		"Content-Type": "application/json",
		Accept: "application/json",
	},
});

export const nodeAuthClient = createClient(
	createConfig({
		axios: authAxios,
		baseURL: "",
		throwOnError: true,
		auth: () => useNodeAuthStore.getState().accessToken ?? undefined,
	}),
);

let refreshTokenPromise: Promise<NodeAccessTokenResponse> | undefined;

export async function getNodeAuthStatus(
	config?: Pick<AxiosRequestConfig, "timeout" | "signal">,
): Promise<NodeAuthStatusResponse> {
	const { data } = await nodeAuthStatus({ ...config, client: nodeAuthClient });
	return data as NodeAuthStatusResponse;
}

export async function setupNodeAuth(request: NodeSetupRequest): Promise<NodeSetupResponse> {
	const { data } = await nodeSetup({ body: request, client: nodeAuthClient });
	return data;
}

export async function loginNodeAuth(request: NodeLoginRequest): Promise<NodeAccessTokenResponse> {
	const { data } = await nodeLogin({ body: request, client: nodeAuthClient });
	return data;
}

// The node's limiter answers 429 with `Retry-After: 60`; a longer value is clamped so a boot never hangs for minutes.
const MAX_REFRESH_RETRY_AFTER_MS = 60_000;

// A refresh that stayed rate limited answers every caller until its Retry-After has passed, without a request: the
// boot guard and the 401 interceptor (a provider outside the router polling before a token exists) each refreshed
// again, and every extra attempt spent the shared auth budget the operator was waiting on.
let refreshBlocked: { until: number; error: unknown } | undefined;

function retryAfterMs(error: unknown): number | undefined {
	if (!axios.isAxiosError(error) || error.response?.status !== 429) {
		return undefined;
	}

	const seconds = Number(error.response.headers["retry-after"]);
	return Number.isFinite(seconds) && seconds >= 0
		? Math.min(seconds * 1000, MAX_REFRESH_RETRY_AFTER_MS)
		: MAX_REFRESH_RETRY_AFTER_MS;
}

/** Seconds until a rate-limited refresh may be sent again, rounded up; 0 when it is not rate limited. */
export function refreshRateLimitedForSeconds(): number {
	const remainingMs = refreshBlocked ? refreshBlocked.until - Date.now() : 0;
	return remainingMs > 0 ? Math.ceil(remainingMs / 1000) : 0;
}

// The deadline of the Retry-After wait a refresh is sitting out right now, read by the boot's loading screen so the
// operator sees why it waits instead of a bare "Loading..." for a minute.
let refreshWaitUntil: number | undefined;

/** Seconds left on the Retry-After wait a refresh is sitting out right now, rounded up; 0 when none is running. */
export function refreshRetryWaitSeconds(): number {
	const remainingMs = refreshWaitUntil === undefined ? 0 : refreshWaitUntil - Date.now();
	return remainingMs > 0 ? Math.ceil(remainingMs / 1000) : 0;
}

async function refreshOnce(): Promise<NodeAccessTokenResponse> {
	const { data } = await nodeRefresh({ client: nodeAuthClient });
	return data;
}

// A 429 leaves the refresh cookie valid, so it is waited out once (Retry-After) and retried instead of failing the
// boot; concurrent callers share the one promise, wait included.
export async function refreshNodeAuthToken(): Promise<NodeAccessTokenResponse> {
	if (refreshTokenPromise) {
		return refreshTokenPromise;
	}

	if (refreshBlocked && Date.now() < refreshBlocked.until) {
		throw refreshBlocked.error;
	}

	refreshTokenPromise = refreshOnce()
		.catch(async (error: unknown) => {
			const waitMs = retryAfterMs(error);
			if (waitMs === undefined) {
				throw error;
			}

			refreshWaitUntil = Date.now() + waitMs;
			try {
				await new Promise((resolve) => setTimeout(resolve, waitMs));
			} finally {
				refreshWaitUntil = undefined;
			}

			return refreshOnce();
		})
		.catch((error: unknown) => {
			const waitMs = retryAfterMs(error);
			if (waitMs !== undefined) {
				refreshBlocked = { until: Date.now() + waitMs, error };
			}

			throw error;
		})
		.finally(() => {
			refreshTokenPromise = undefined;
		});

	return refreshTokenPromise;
}

export async function logoutNodeAuth(): Promise<void> {
	await nodeLogout({ client: nodeAuthClient });
}

// Returns 204 and NO new token pair: the node rotates the security stamp and revokes every refresh token, so the
// caller's own access token is dead on its next request and the refresh cookie is cleared. A caller must treat a
// resolved promise as a sign-out, not as a session that continues.
export async function changeNodePassword(request: NodeChangePasswordRequest): Promise<void> {
	await nodeChangePassword({ body: request, client: nodeAuthClient });
}

// Legacy data dir: wraps the existing node key with the operator's (re-entered) login password. 409 once not pending.
export async function confirmNodeVault(request: NodeVaultPasswordRequest): Promise<NodeVaultConfirmResponse> {
	const { data } = await nodeVaultConfirm({ body: request, client: nodeAuthClient });
	return data;
}

// Answered by the locked pre-host, so not in the spec. 204 starts the hand-over to the real host on the same origin;
// it issues no token.
export async function unlockNodeVault(request: NodeVaultPasswordRequest, config?: AxiosRequestConfig): Promise<void> {
	await authAxios.post(buildLocalApiUrl("auth/vault/unlock"), request, config);
}

// 200 carries the rotated recovery code and, like the password unlock, starts the hand-over.
export async function unlockNodeVaultWithRecovery(
	request: NodeVaultRecoveryUnlockRequest,
	config?: AxiosRequestConfig,
): Promise<NodeVaultRecoveryUnlockResponse> {
	const { data } = await authAxios.post<NodeVaultRecoveryUnlockResponse>(
		buildLocalApiUrl("auth/vault/unlock-recovery"),
		request,
		config,
	);
	return data;
}
