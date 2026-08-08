import { getNodeAuthStatus, refreshNodeAuthToken } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

export type NodeAuthRestoreResult = "authenticated" | "setup-required" | "unauthenticated";

let restoreSessionPromise: Promise<NodeAuthRestoreResult> | undefined;

export async function restoreNodeAuthSession(): Promise<NodeAuthRestoreResult> {
	if (restoreSessionPromise) {
		return restoreSessionPromise;
	}

	restoreSessionPromise = (async () => {
		const status = await getNodeAuthStatus();
		if (status.setupRequired) {
			useNodeAuthStore.getState().actions.clear();
			return "setup-required";
		}

		try {
			const token = await refreshNodeAuthToken();
			useNodeAuthStore.getState().actions.setToken(token);
			return "authenticated";
		} catch {
			useNodeAuthStore.getState().actions.clear();
			return "unauthenticated";
		}
	})().finally(() => {
		restoreSessionPromise = undefined;
	});

	return restoreSessionPromise;
}
