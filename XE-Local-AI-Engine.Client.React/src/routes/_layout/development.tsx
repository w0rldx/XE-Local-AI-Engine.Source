import { createFileRoute } from "@tanstack/react-router";

import { DevelopmentConsentGate } from "@/features/development/components/DevelopmentConsentGate";
import { DevelopmentPage } from "@/features/development/pages/DevelopmentPage";

// The consent disclosure gates the route rather than sitting inside the page: it has to be impossible to start an
// attempt behind an unacknowledged notice, and keeping it here leaves the page's own tests free of the gate.
function DevelopmentRoute() {
	return (
		<DevelopmentConsentGate>
			<DevelopmentPage />
		</DevelopmentConsentGate>
	);
}

export const Route = createFileRoute("/_layout/development")({
	component: DevelopmentRoute,
});
