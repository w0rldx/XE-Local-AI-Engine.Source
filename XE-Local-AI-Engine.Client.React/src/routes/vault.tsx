import { createFileRoute, redirect } from "@tanstack/react-router";

import { getNodeAuthStatus } from "@/core/auth/api/NodeAuthApi";
import { VaultUnlock } from "@/core/auth/pages/VaultUnlock";
import { getSafeRedirectPath } from "@/core/auth/utils/RedirectPath";

interface VaultSearch {
	redirect?: string;
}

function validateSearch(search: Record<string, unknown>): VaultSearch {
	const redirectValue = search["redirect"];

	return {
		redirect: typeof redirectValue === "string" ? redirectValue : undefined,
	};
}

// Only the locked pre-host answers "locked"; once the real host runs, the unlock page has nothing to unlock.
export const Route = createFileRoute("/vault")({
	validateSearch,
	beforeLoad: async ({ search }) => {
		const status = await getNodeAuthStatus();
		if (status.vault !== "locked") {
			throw redirect({ to: getSafeRedirectPath(search.redirect) });
		}
	},
	component: VaultUnlock,
});
