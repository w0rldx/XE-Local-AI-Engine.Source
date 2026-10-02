import { getNodeAuthStatus, refreshNodeAuthToken } from "@/core/auth/api/NodeAuthApi";
import type { NodeAuthStatusResponse } from "@/core/auth/models/NodeAuthModels";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

type NodeVaultStep = "vault-locked" | "vault-setup-required";

export type NodeAuthRestoreResult = "authenticated" | "setup-required" | "unauthenticated" | NodeVaultStep;

let restoreSessionPromise: Promise<NodeAuthRestoreResult> | undefined;

// Once the vault has been seen unlocked it stays unlocked until the engine restarts. A tab that outlives that restart
// keeps this flag, so the vault-locked interceptor resets it (resetVaultSettled) when the relaunched engine answers
// 503 "Vault locked". Spares the token-holding path a status call on every navigation.
let vaultSettled = false;

export function resetVaultSettled(): void {
	vaultSettled = false;
}

// A pending vault on a node that already has an admin is a legacy data dir: it owes the confirm step. Pending with
// `setupRequired` is a fresh install, which /setup wraps on its own.
function vaultStep(status: NodeAuthStatusResponse): NodeVaultStep | undefined {
	if (status.vault === "locked") {
		return "vault-locked";
	}

	if (status.vault === "pending" && !status.setupRequired) {
		return "vault-setup-required";
	}

	vaultSettled = status.vault !== "pending";
	return undefined;
}

export async function restoreNodeAuthSession(): Promise<NodeAuthRestoreResult> {
	if (restoreSessionPromise) {
		return restoreSessionPromise;
	}

	restoreSessionPromise = (async () => {
		const status = await getNodeAuthStatus();
		const step = vaultStep(status);
		if (step === "vault-locked") {
			useNodeAuthStore.getState().actions.clear();
			return step;
		}

		if (status.setupRequired) {
			useNodeAuthStore.getState().actions.clear();
			return "setup-required";
		}

		try {
			const token = await refreshNodeAuthToken();
			useNodeAuthStore.getState().actions.setToken(token);
			return step ?? "authenticated";
		} catch {
			useNodeAuthStore.getState().actions.clear();
			return "unauthenticated";
		}
	})().finally(() => {
		restoreSessionPromise = undefined;
	});

	return restoreSessionPromise;
}

/**
 * The vault step a session that already holds a token still owes, e.g. a legacy node right after login. Fails open on
 * a status read failure, like the layout's settings guard: a throw would make every authenticated page unreachable.
 */
export async function getPendingVaultStep(): Promise<NodeVaultStep | undefined> {
	if (vaultSettled) {
		return undefined;
	}

	try {
		return vaultStep(await getNodeAuthStatus());
	} catch {
		return undefined;
	}
}
