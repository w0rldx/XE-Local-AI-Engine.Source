import { createFileRoute } from "@tanstack/react-router";

import { PlaceholderPage } from "@/core/ui/pages/PlaceholderPage/PlaceholderPage";

export const Route = createFileRoute("/_layout/cloud-settings")({
	component: () => (
		<PlaceholderPage
			titleKey="pages.cloudSettings.placeholder.title"
			titleFallback="Cloud settings"
			descriptionKey="pages.cloudSettings.placeholder.description"
			descriptionFallback="Provider configuration will use secret-safe local API responses."
		/>
	),
});
