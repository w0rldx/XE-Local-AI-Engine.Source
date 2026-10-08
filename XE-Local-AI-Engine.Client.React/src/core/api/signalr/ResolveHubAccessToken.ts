import { refreshNodeAuthToken } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// Renew the access token a little before it expires so the SignalR HTTP negotiate /
// WebSocket upgrade never carries a token that is about to lapse.
const tokenRenewSkewMs = 30_000;

function isExpired(expiresAtUtc: string | undefined): boolean {
	if (!expiresAtUtc) {
		return true;
	}
	const expiresAt = Date.parse(expiresAtUtc);
	if (Number.isNaN(expiresAt)) {
		return true;
	}
	return expiresAt - Date.now() <= tokenRenewSkewMs;
}

// accessTokenFactory runs before each negotiate/transport HTTP request. Renew the token
// when it is missing or near expiry so a long-lived connection keeps authenticating
// across reconnects. Never log the returned token — it may end up in WS/SSE query strings.
export async function resolveHubAccessToken(): Promise<string> {
	const state = useNodeAuthStore.getState();
	if (state.accessToken && !isExpired(state.expiresAtUtc)) {
		return state.accessToken;
	}

	try {
		const token = await refreshNodeAuthToken();
		useNodeAuthStore.getState().actions.setToken(token);
		return token.accessToken;
	} catch {
		// Fall back to whatever token we hold; the hub will reject if it is invalid.
		return useNodeAuthStore.getState().accessToken ?? "";
	}
}
