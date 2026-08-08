import { createFileRoute, redirect } from "@tanstack/react-router";

import { getNodeAuthStatus } from "@/core/auth/api/NodeAuthApi";
import { Login } from "@/core/auth/pages/Login";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { getSafeRedirectPath } from "@/core/auth/utils/RedirectPath";
import { restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";

interface LoginSearch {
	redirect?: string;
}

function validateSearch(search: Record<string, unknown>): LoginSearch {
	const redirectValue = search["redirect"];

	return {
		redirect: typeof redirectValue === "string" ? redirectValue : undefined,
	};
}

export const Route = createFileRoute("/login")({
	validateSearch,
	beforeLoad: async ({ search }) => {
		const safeRedirect = getSafeRedirectPath(search.redirect);
		const status = await getNodeAuthStatus();
		if (status.setupRequired) {
			throw redirect({ to: "/setup" });
		}

		if (useNodeAuthStore.getState().accessToken) {
			throw redirect({ to: safeRedirect });
		}

		const restoreResult = await restoreNodeAuthSession();
		if (restoreResult === "authenticated") {
			throw redirect({ to: safeRedirect });
		}
	},
	component: Login,
});
