import { createFileRoute } from "@tanstack/react-router";

import { PlaceholderPage } from "@/core/ui/pages/PlaceholderPage/PlaceholderPage";

export const Route = createFileRoute("/_layout/dashboard")({
	component: () => (
		<PlaceholderPage
			titleKey="pages.dashboard.placeholder.title"
			titleFallback="Dashboard"
			descriptionKey="pages.dashboard.placeholder.description"
			descriptionFallback="Remote connection state and node health controls will be added here."
		/>
	),
});
