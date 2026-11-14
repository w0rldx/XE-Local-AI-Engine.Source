import type { QueryClient } from "@tanstack/react-query";
import { type ParsedLocation, redirect } from "@tanstack/react-router";

import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { getPendingVaultStep, restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";

/** The first-run screens in the order a fresh install meets them; the stepper numbers itself from this list. */
export const firstRunSteps = ["account", "vault", "externalAccess"] as const;
export type FirstRunStep = (typeof firstRunSteps)[number];

// Every place the guard can send a visitor. "/" is the app itself: the `/_layout` subtree and `/command`.
type GuardStop = "/setup" | "/vault" | "/vault-setup" | "/login" | "/external-access" | "/";

// The one place the first-run order lives: account, vault, then the external-access choice (the first-run model download
// waits on it through ExternalAccessGate). Undefined means the settings read failed behind a signed-in session: the
// layout fails open and the chooser (no skip control) renders rather than dead-ending; `guardFirstRun` decides the rest.
async function owedStop(queryClient: QueryClient | undefined): Promise<GuardStop | undefined> {
	const session = useNodeAuthStore.getState().accessToken
		? ((await getPendingVaultStep()) ?? "authenticated")
		: await restoreNodeAuthSession();
	switch (session) {
		case "authenticated":
			break;
		case "setup-required":
			return "/setup";
		case "vault-locked":
			return "/vault";
		case "vault-setup-required":
			return "/vault-setup";
		default:
			return "/login";
	}

	if (queryClient === undefined) {
		return "/";
	}

	try {
		// `ensureQueryData` shares the router's QueryClient: one request per session, not per navigation. A null profile
		// behind a signed-in session is a data directory from before the profile existed, so it is asked like "pending".
		const settings = await queryClient.ensureQueryData(getNodeSettingsOptions());
		return settings.externalAccessProfile === "pending" || settings.externalAccessProfile == null ? "/external-access" : "/";
	} catch {
		return undefined;
	}
}

/**
 * The `beforeLoad` of every first-run route and of the app: redirects to the first stop the node still owes unless it is
 * `here`. Without a `queryClient` the external-access step is not checked (`/command`, outside the app chrome).
 * `"retired"` is a first-run screen that no longer exists (an old bookmark): it always leaves, for the app at the least.
 */
export async function guardFirstRun(
	here: GuardStop | "retired",
	{ location, queryClient }: { location: ParsedLocation; queryClient?: QueryClient },
): Promise<void> {
	// A failed settings read happens only behind a signed-in session, so the account and vault are done: the app (which
	// fails open) and the chooser stay, while setup, vault setup and a retired screen go on to the app.
	const owed = (await owedStop(queryClient)) ?? (here === "/external-access" ? undefined : "/");
	if (owed === undefined || owed === here) {
		return;
	}

	// Only the app carries the page the operator was on: login and unlock return there, a first-run screen never does.
	if (here === "/" && (owed === "/vault" || owed === "/login")) {
		throw redirect({ to: owed, search: { redirect: location.href } });
	}

	throw redirect({ to: owed });
}
