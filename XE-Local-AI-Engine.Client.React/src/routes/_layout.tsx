import { createFileRoute } from "@tanstack/react-router";

import { AppLayout } from "@/AppLayout";
import { guardFirstRun } from "@/core/auth/utils/FirstRunGuard";
import { OnboardingProvider } from "@/features/onboarding/components/OnboardingProvider";

export const Route = createFileRoute("/_layout")({
	// Runs on every navigation, already-authenticated ones included: the only place a typed-URL bypass of a first-run
	// step can be caught.
	beforeLoad: ({ context, location }) => guardFirstRun("/", { location, queryClient: context.queryClient }),
	// The tour lives here, not above the router: mounted at the app root it opened its welcome dialog the moment setup
	// stamped a token, on top of the still-unanswered `/external-access` chooser.
	component: (): React.ReactElement | null => (
		<OnboardingProvider>
			<AppLayout />
		</OnboardingProvider>
	),
});
