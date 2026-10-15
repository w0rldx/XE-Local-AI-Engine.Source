import { type ParsedLocation, redirect } from "@tanstack/react-router";

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { getPendingVaultStep, restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";

// The authentication chain, hoisted out of the `/_layout` `beforeLoad` so the profile guard there runs UNCONDITIONALLY.
// As a chain of early returns inside `beforeLoad` it would have short-circuited on every already-authenticated
// navigation, which is every navigation after the first — leaving the profile guard unreachable exactly when it matters.
// The vault steps come first, so a legacy node confirms its vault before the external-access and ui-mode choices.
// Shared with the chrome-less `/command` page, which sits outside `/_layout` but needs the same sign-in chain.
export async function ensureAuthenticated(location: ParsedLocation): Promise<void> {
	const restoreResult = useNodeAuthStore.getState().accessToken
		? ((await getPendingVaultStep()) ?? "authenticated")
		: await restoreNodeAuthSession();
	if (restoreResult === "authenticated") {
		return;
	}

	if (restoreResult === "vault-locked") {
		throw redirect({ to: "/vault" });
	}

	if (restoreResult === "vault-setup-required") {
		throw redirect({ to: "/vault-setup" });
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
}
