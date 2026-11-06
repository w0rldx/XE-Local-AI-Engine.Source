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

			if (restoreResult === "vault-locked") {
				throw redirect({ to: "/vault" });
			}

			if (restoreResult === "vault-setup-required") {
				throw redirect({ to: "/vault-setup" });
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

		// The server stamps "pending" the moment an administrator is persisted. Null behind a signed-in session is a data
		// directory from before the profile existed, which the layout sends here too, so both are asked.
		if (settings.externalAccessProfile !== "pending" && settings.externalAccessProfile != null) {
			throw redirect({ to: "/" });
		}
	},
	component: ExternalAccessSetup,
});
