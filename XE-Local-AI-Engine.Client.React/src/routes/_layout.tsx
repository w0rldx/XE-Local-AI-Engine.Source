import { createFileRoute, isRedirect, redirect } from "@tanstack/react-router";
import type { ParsedLocation } from "@tanstack/react-router";

import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { getPendingVaultStep, restoreNodeAuthSession } from "@/core/auth/utils/SessionRestore";
import { Layout } from "@/core/layout/components/Layout/Layout";
import { OnboardingProvider } from "@/features/onboarding/components/OnboardingProvider";

// The authentication chain, hoisted out of `beforeLoad` so the profile guard below it runs UNCONDITIONALLY. As a chain
// of early returns inside `beforeLoad` it would have short-circuited on every already-authenticated navigation, which
// is every navigation after the first — leaving the profile guard unreachable exactly when it matters.
// The vault steps come first, so a legacy node confirms its vault before the external-access and ui-mode choices.
async function ensureAuthenticated(location: ParsedLocation): Promise<void> {
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

export const Route = createFileRoute("/_layout")({
	beforeLoad: async ({ context, location }) => {
		await ensureAuthenticated(location);

		// The only place a typed-URL bypass of the first-run profile choice can be caught. `ensureQueryData` shares the
		// router's QueryClient, so this is one request per session rather than per navigation.
		try {
			const settings = await context.queryClient.ensureQueryData(getNodeSettingsOptions());
			if (settings.externalAccessProfile === "pending") {
				throw redirect({ to: "/external-access" });
			}

			// The sandbox security profile is the second question: a security decision, so it comes before the interface
			// mode. "pending" is stamped for a fresh node only; SandboxSecurityProfileBackfillService gives an upgraded node
			// a real profile before Kestrel accepts a request, so it never lands here.
			if (settings.sandboxSecurityProfile === "pending") {
				throw redirect({ to: "/sandbox-profile-setup" });
			}

			// The same typed-URL catch for the LAST step. Order is load-bearing: the two checks above run first, so a
			// fresh install answers the questions in sequence and cannot bounce between them. An upgraded node
			// never reaches this branch — UiModeBackfillService stamps "advanced" before Kestrel accepts a request — so a
			// null mode here really does mean "nobody has been asked yet".
			if (settings.uiMode === null || settings.uiMode === undefined) {
				throw redirect({ to: "/ui-mode-setup" });
			}
		} catch (error) {
			// Fail open on a settings read failure — a throw here would make every authenticated page unreachable, which is
			// far worse than a skipped profile screen. The re-throw is load-bearing: the catch wraps the block that threw
			// the redirect, so without it the guard would swallow its own result and silently never fire.
			if (isRedirect(error)) {
				throw error;
			}
		}
	},
	// The tour lives here, not above the router: mounted at the app root it opened its welcome dialog the moment setup
	// stamped a token, on top of the still-unanswered `/external-access` chooser.
	component: (): React.ReactElement | null => (
		<OnboardingProvider>
			<Layout />
		</OnboardingProvider>
	),
});
