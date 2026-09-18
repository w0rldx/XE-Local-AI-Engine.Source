import axios, { type AxiosRequestConfig } from "axios";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import type {
	NodeAccessTokenResponse,
	NodeAuthStatusResponse,
	NodeChangePasswordRequest,
	NodeLoginRequest,
	NodeSetupRequest,
} from "@/core/auth/models/NodeAuthModels";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

const authClient = axios.create({
	baseURL: "/",
	withCredentials: true,
	headers: {
		"Content-Type": "application/json",
		Accept: "application/json",
	},
});

let refreshTokenPromise: Promise<NodeAccessTokenResponse> | undefined;

function withBearer(config?: AxiosRequestConfig): AxiosRequestConfig {
	const accessToken = useNodeAuthStore.getState().accessToken;

	return {
		...config,
		headers: {
			...config?.headers,
			...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
		},
	};
}

export async function getNodeAuthStatus(config?: AxiosRequestConfig): Promise<NodeAuthStatusResponse> {
	const { data } = await authClient.get<NodeAuthStatusResponse>(buildLocalApiUrl("auth/status"), config);
	return data;
}

export async function setupNodeAuth(request: NodeSetupRequest, config?: AxiosRequestConfig): Promise<void> {
	await authClient.post(buildLocalApiUrl("auth/setup"), request, config);
}

export async function loginNodeAuth(request: NodeLoginRequest, config?: AxiosRequestConfig): Promise<NodeAccessTokenResponse> {
	const { data } = await authClient.post<NodeAccessTokenResponse>(buildLocalApiUrl("auth/login"), request, config);
	return data;
}

export async function refreshNodeAuthToken(): Promise<NodeAccessTokenResponse> {
	if (refreshTokenPromise) {
		return refreshTokenPromise;
	}

	refreshTokenPromise = authClient
		.post<NodeAccessTokenResponse>(buildLocalApiUrl("auth/refresh"), {})
		.then(({ data }) => data)
		.finally(() => {
			refreshTokenPromise = undefined;
		});

	return refreshTokenPromise;
}

export async function logoutNodeAuth(config?: AxiosRequestConfig): Promise<void> {
	await authClient.post(buildLocalApiUrl("auth/logout"), {}, withBearer(config));
}

// Returns 204 and NO new token pair: the node rotates the security stamp and revokes every refresh token, so the
// caller's own access token is dead on its next request and the refresh cookie is cleared. A caller must treat a
// resolved promise as a sign-out, not as a session that continues.
export async function changeNodePassword(request: NodeChangePasswordRequest, config?: AxiosRequestConfig): Promise<void> {
	await authClient.post(buildLocalApiUrl("auth/change-password"), request, withBearer(config));
}
