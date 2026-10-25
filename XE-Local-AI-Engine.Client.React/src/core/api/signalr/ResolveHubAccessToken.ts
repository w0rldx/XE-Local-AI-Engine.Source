import { redirectToLoginOnce } from "@/core/api/axios/LoginNavigation";
import { getErrorStatus } from "@/core/api/errors/RetryClassification";
import { refreshNodeAuthToken } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// Renew the access token a little before it expires so the SignalR HTTP negotiate /
// WebSocket upgrade never carries a token that is about to lapse.
const tokenRenewSkewMs = 30_000;

// The node closes every hub connection of a sign-in that ended (logout elsewhere, a revoked session, a password change).
// The token in memory still looks unexpired, so after a drop the next connect refreshes first: a dead cookie answers 401
// and ends on the sign-in page instead of reconnecting with a revoked token until the page happens to call REST.
let connectionLosses = 0;
let tokenCheckedAtLoss = 0;

/** Record that a hub connection dropped or closed, so the next connect proves the sign-in is still live. */
export function markHubConnectionLost(): void {
	connectionLosses += 1;
}

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
	if (state.accessToken && !isExpired(state.expiresAtUtc) && tokenCheckedAtLoss === connectionLosses) {
		return state.accessToken;
	}

	const losses = connectionLosses;
	try {
		const token = await refreshNodeAuthToken();
		useNodeAuthStore.getState().actions.setToken(token);
		tokenCheckedAtLoss = losses;
		return token.accessToken;
	} catch (error) {
		// Only a 401 from refresh ends the session, as in the REST interceptor; a 429, 5xx or network failure keeps it.
		if (getErrorStatus(error) === 401) {
			useNodeAuthStore.getState().actions.clear();
			redirectToLoginOnce();
		}
		// Fall back to whatever token we hold; the hub will reject if it is invalid.
		return useNodeAuthStore.getState().accessToken ?? "";
	}
}
