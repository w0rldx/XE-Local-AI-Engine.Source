import { createFileRoute, isRedirect, redirect } from "@tanstack/react-router";

import { AppLayout } from "@/AppLayout";
import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { ensureAuthenticated } from "@/core/auth/utils/EnsureAuthenticated";
import { OnboardingProvider } from "@/features/onboarding/components/OnboardingProvider";

export const Route = createFileRoute("/_layout")({
	beforeLoad: async ({ context, location }) => {
		await ensureAuthenticated(location);

		// The only place a typed-URL bypass of the first-run profile choice can be caught. `ensureQueryData` shares the
		// router's QueryClient, so this is one request per session rather than per navigation. A null profile behind a
		// signed-in session is a data directory from before the profile existed (no boot backfill stamps it any more), so
		// it is asked exactly like "pending".
		try {
			const settings = await context.queryClient.ensureQueryData(getNodeSettingsOptions());
			if (settings.externalAccessProfile === "pending" || settings.externalAccessProfile == null) {
				throw redirect({ to: "/external-access" });
			}

			// The sandbox security profile is the second question: a security decision, so it comes before the interface
			// mode. "pending" is stamped for a fresh node only; a null profile from an older data directory reads as low,
			// the behavior it always had, so it is not asked.
			if (settings.sandboxSecurityProfile === "pending") {
				throw redirect({ to: "/sandbox-profile-setup" });
			}

			// The same typed-URL catch for the LAST step. Order is load-bearing: the two checks above run first, so a
			// fresh install answers the questions in sequence and cannot bounce between them. A null mode means nobody
			// has been asked yet, including an older data directory that predates the setting.
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
			<AppLayout />
		</OnboardingProvider>
	),
});
