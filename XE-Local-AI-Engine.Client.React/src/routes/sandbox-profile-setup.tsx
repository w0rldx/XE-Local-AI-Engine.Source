import { createFileRoute, redirect } from "@tanstack/react-router";

import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";
import { SandboxProfileSetup } from "@/features/node-settings/pages/SandboxProfileSetup";

export const Route = createFileRoute("/sandbox-profile-setup")({
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
			// Fail TOWARD the chooser, exactly as the other first-run steps do: this screen has no skip control and the
			// layout bounces the operator straight back to it, so an error boundary here would be a dead end, while
			// choosing again is idempotent.
			return;
		}

		// The earlier step first: a node still answering external access must not be asked this question out of order.
		if (settings.externalAccessProfile === "pending") {
			throw redirect({ to: "/external-access" });
		}

		// "pending" is the discriminator, as for external access: the server stamps it for a fresh node and the boot
		// backfill gives every upgraded node a real profile. Anything else means the question is answered.
		if (settings.sandboxSecurityProfile !== "pending") {
			throw redirect({ to: "/" });
		}
	},
	component: SandboxProfileSetup,
});
