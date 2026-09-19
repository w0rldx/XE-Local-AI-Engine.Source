import { createFileRoute, redirect } from "@tanstack/react-router";

import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";
import { UiModeSetup } from "@/features/node-settings/pages/UiModeSetup";

export const Route = createFileRoute("/ui-mode-setup")({
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
			// Fail TOWARD the chooser, exactly as the external-access route does: this screen has no skip control and the
			// layout bounces the operator straight back to it, so an error boundary here would be a dead end — while
			// choosing again is idempotent.
			return;
		}

		// Null is the discriminator here, unlike the external-access route's "pending": nothing stamps a placeholder mode,
		// and the boot backfill has already given every node that finished onboarding a real value. So an operator who
		// types this URL after deciding is sent home rather than asked twice.
		if (settings.uiMode !== null && settings.uiMode !== undefined) {
			throw redirect({ to: "/" });
		}
	},
	component: UiModeSetup,
});
