import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";

import { logoutNodeAuth } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

/**
 * Shared logout flow: revokes the node session, clears local auth state, and routes to /login.
 * Auth is always cleared locally even when the revoke call fails, so the client never stays
 * signed in against a dead session. Used by the desktop HeaderBar and the mobile navigation drawer.
 */
export function useNodeLogout() {
	const [logoutPending, setLogoutPending] = useState(false);
	const navigate = useNavigate();
	const clearAuth = useNodeAuthStore((state) => state.actions.clear);

	const logout = async (): Promise<void> => {
		setLogoutPending(true);
		try {
			await logoutNodeAuth();
		} finally {
			clearAuth();
			setLogoutPending(false);
			await navigate({ to: "/login" });
		}
	};

	return { logout, logoutPending } as const;
}
