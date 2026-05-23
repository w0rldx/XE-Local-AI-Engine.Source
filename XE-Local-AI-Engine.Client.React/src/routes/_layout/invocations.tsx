import { createFileRoute } from "@tanstack/react-router";

import { PlaceholderPage } from "@/core/ui/pages/PlaceholderPage/PlaceholderPage";

export const Route = createFileRoute("/_layout/invocations")({
	component: () => (
		<PlaceholderPage
			titleKey="pages.invocations.placeholder.title"
			titleFallback="Invocations"
			descriptionKey="pages.invocations.placeholder.description"
			descriptionFallback="Current and historical invocation monitoring will be ported here."
		/>
	),
});
