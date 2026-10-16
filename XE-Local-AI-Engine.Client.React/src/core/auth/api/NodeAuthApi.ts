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

export async function refreshNodeAuthToken(): Promise<NodeAccessTokenResponse> {
	if (refreshTokenPromise) {
		return refreshTokenPromise;
	}

	refreshTokenPromise = nodeRefresh({ client: nodeAuthClient })
		.then(({ data }) => data)
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
