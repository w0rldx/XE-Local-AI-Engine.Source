import { createFileRoute, redirect } from "@tanstack/react-router";

import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";
import { ExternalAccessSetup } from "@/features/node-settings/pages/ExternalAccessSetup";

export const Route = createFileRoute("/external-access")({
	beforeLoad: async ({ context }) => {
		if (!useNodeAuthStore.getState().accessToken) {
			const restoreResult = await restoreNodeAuthSession();
			if (restoreResult === "setup-required") {
				throw redirect({ to: "/setup" });
			}

			if (restoreResult !== "authenticated") {
				throw redirect({ to: "/login" });
			}
		}

		let settings;
		try {
			settings = await context.queryClient.ensureQueryData(getNodeSettingsOptions());
		} catch {
			// Fail TOWARD the chooser, the opposite of the layout guard. This screen has no skip control and the layout
			// bounces the operator straight back to it, so an error boundary here would be a dead end — while choosing
			// again is idempotent.
			return;
		}

		// "pending" is the discriminator, never null: the server stamps it the moment an administrator is persisted, so
		// null means "no admin at all" and is not this route's business.
		if (settings.externalAccessProfile !== "pending") {
			throw redirect({ to: "/" });
		}
	},
	component: ExternalAccessSetup,
});
