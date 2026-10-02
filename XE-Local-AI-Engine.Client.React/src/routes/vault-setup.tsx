import { createFileRoute, redirect } from "@tanstack/react-router";

import { VaultSetup } from "@/core/auth/pages/VaultSetup";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { getPendingVaultStep, restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";

// Open only to a signed-in operator of a node whose vault is still pending; every other state has a better place to be.
export const Route = createFileRoute("/vault-setup")({
	beforeLoad: async () => {
		const result = useNodeAuthStore.getState().accessToken
			? ((await getPendingVaultStep()) ?? "authenticated")
			: await restoreNodeAuthSession();
		if (result === "vault-setup-required") {
			return;
		}

		if (result === "vault-locked") {
			throw redirect({ to: "/vault" });
		}

		if (result === "setup-required") {
			throw redirect({ to: "/setup" });
		}

		if (result === "unauthenticated") {
			throw redirect({ to: "/login" });
		}

		throw redirect({ to: "/" });
	},
	component: VaultSetup,
});
