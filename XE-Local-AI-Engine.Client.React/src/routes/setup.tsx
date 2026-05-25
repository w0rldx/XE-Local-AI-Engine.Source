import { createFileRoute, redirect } from "@tanstack/react-router";

import { getNodeAuthStatus } from "@/core/auth/api/NodeAuthApi";
import { Setup } from "@/core/auth/pages/Setup";
import { restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";

export const Route = createFileRoute("/setup")({
	beforeLoad: async () => {
		const status = await getNodeAuthStatus();
		if (status.setupRequired) {
			return;
		}

		const restoreResult = await restoreNodeAuthSession();
		if (restoreResult === "authenticated") {
			throw redirect({ to: "/" });
		}

		throw redirect({ to: "/login" });
	},
	component: Setup,
});
