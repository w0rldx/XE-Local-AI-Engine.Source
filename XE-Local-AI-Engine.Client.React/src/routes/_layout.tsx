import { createFileRoute, redirect } from "@tanstack/react-router";

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";
import { Layout } from "@/core/layout/components/Layout/Layout";

export const Route = createFileRoute("/_layout")({
	beforeLoad: async ({ location }) => {
		if (useNodeAuthStore.getState().accessToken) {
			return;
		}

		const restoreResult = await restoreNodeAuthSession();
		if (restoreResult === "authenticated") {
			return;
		}

		if (restoreResult === "setup-required") {
			throw redirect({ to: "/setup" });
		}

		throw redirect({
			to: "/login",
			search: {
				redirect: location.href,
			},
		});
	},
	component: (): React.ReactElement | null => <Layout />,
});
