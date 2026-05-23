import { createFileRoute } from "@tanstack/react-router";

import { PlaceholderPage } from "@/core/ui/pages/PlaceholderPage/PlaceholderPage";

export const Route = createFileRoute("/_layout/models")({
	component: () => (
		<PlaceholderPage
			titleKey="pages.models.placeholder.title"
			titleFallback="Models"
			descriptionKey="pages.models.placeholder.description"
			descriptionFallback="Local model listing, selection, pull, and delete controls will be added here."
		/>
	),
});
