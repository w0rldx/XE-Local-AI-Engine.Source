import { createFileRoute, redirect } from "@tanstack/react-router";

import { getNodeAuthStatus } from "@/core/auth/api/NodeAuthApi";
import { VaultUnlock } from "@/core/auth/pages/VaultUnlock";

// Only the locked pre-host answers "locked"; once the real host runs, the unlock page has nothing to unlock.
export const Route = createFileRoute("/vault")({
	beforeLoad: async () => {
		const status = await getNodeAuthStatus();
		if (status.vault !== "locked") {
			throw redirect({ to: "/" });
		}
	},
	component: VaultUnlock,
});
